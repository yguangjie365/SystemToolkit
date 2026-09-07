using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="INetworkInfoService"/> 实现：适配器走 BCL（跨 API、零管理员、无 WMI 首启 20s 问题），
/// 代理走 HKCU 注册表 + WinINET 刷新。注册表 / BCL 枚举属系统边界，不做单测；
/// 值换算逻辑抽在 <see cref="ProxyRegistryMapping"/>（可测）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkInfoService : INetworkInfoService
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <inheritdoc cref="INetworkInfoService.GetAdaptersAsync"/>
    public Task<IReadOnlyList<NetAdapterInfo>> GetAdaptersAsync()
    {
        // BCL 采集是同步 API，包 Task.Run 避免阻塞 UI 线程（WMI 级耗时教训）
        return Task.Run<IReadOnlyList<NetAdapterInfo>>(() =>
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                                      and not NetworkInterfaceType.Tunnel)
                // 2026-09-02：过滤驱动子接口（QoS/WFP/Native WiFi Filter，与物理卡同类型
                // 同速率）与虚拟适配器（本地连接* N / BTLAN / 内核调试器等）不再进下拉
                // ——实机反馈「网卡一堆」（NetAdapterFilter 黑名单与 Overview 共用）。
                .Where(ni => !NetAdapterFilter.IsJunkAdapter(ni.Description, ni.Name))
                // 2026-09-07：过滤幽灵接口（实机实证：Killer 机型驱动升级残留 WLAN 2/4/5，
                // 断开+速率未知+无真实 IP，设备管理器仅 1 卡；MAC 互不相同，同 MAC 去重拦不住）
                .Where(ni => !NetAdapterFilter.IsGhostAdapter(
                    ni.OperationalStatus == OperationalStatus.Up,
                    ni.Speed,
                    ni.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address)
                        .ToArray(),
                    ni.Name,
                    ni.GetPhysicalAddress().GetAddressBytes().Length))
                // 同一物理卡经过滤驱动枚举出多条时（连接名形如 WLAN / WLAN 2 / WLAN 5），
                // 只保留主连接名（名称最短者）。去重键 = 描述 + MAC：过滤驱动子接口与宿主
                // 同 MAC，会被合并；双物理网卡（同型号双 LAN）MAC 不同，不会被误伤
                .GroupBy(ni => ni.Description.Trim().ToUpperInvariant() + "|" + ni.GetPhysicalAddress().ToString())
                .Select(g => g.OrderBy(ni => ni.Name.Length).First())
                .Select(ToInfo)
                .ToArray();
        });
    }

    /// <inheritdoc cref="INetworkInfoService.GetSystemProxy"/>
    public ProxyInfo GetSystemProxy()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        int enable = key?.GetValue("ProxyEnable") is int value ? value : 0;
        string? server = key?.GetValue("ProxyServer") as string;
        return ProxyRegistryMapping.FromValues(enable, server);
    }

    /// <inheritdoc cref="INetworkInfoService.SetSystemProxy"/>
    public void SetSystemProxy(bool enabled, string? server, Action<string>? log = null)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey);
        key.SetValue("ProxyEnable", ProxyRegistryMapping.ToEnableValue(enabled), RegistryValueKind.DWord);
        if (!string.IsNullOrWhiteSpace(server))
        {
            key.SetValue("ProxyServer", server!.Trim(), RegistryValueKind.String);
        }

        // 必须通知 WinINET 刷新，否则浏览器重开前不生效（见 WinInetInterop 注释）。
        // 【核实报告 S1】刷新失败经 log 回调上报（调用方落面板日志）——WinInetInterop 是
        // internal（Core 内部），模块层无法直接接线，故由本方法透传回调并在调用后还原。
        WinInetInterop.Log = log;
        try
        {
            WinInetInterop.RefreshSettings();
        }
        finally
        {
            WinInetInterop.Log = null;
        }
    }

    private static NetAdapterInfo ToInfo(NetworkInterface ni)
    {
        IPInterfaceProperties props = ni.GetIPProperties();

        bool isDhcp = false;
        try
        {
            isDhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? false;
        }
        catch (NetworkInformationException)
        {
            // 个别虚拟适配器拿不到 IPv4 属性：按非 DHCP 处理，不阻断列表
        }

        string[] ipv4WithMask = props.UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.PrefixLength is int len ? $"{a.Address}/{len}" : a.Address.ToString())
            .ToArray();

        string[] gateways = props.GatewayAddresses
            .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(g => g.Address.ToString())
            .ToArray();

        string[] dns = props.DnsAddresses
            .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
            .Select(d => d.ToString())
            .ToArray();

        byte[] macBytes = ni.GetPhysicalAddress().GetAddressBytes();

        return new NetAdapterInfo(
            Name: ni.Name,
            Description: ni.Description,
            Type: MapType(ni.NetworkInterfaceType),
            Status: ni.OperationalStatus switch
            {
                OperationalStatus.Up => OperStatus.Up,
                OperationalStatus.Down => OperStatus.Down,
                _ => OperStatus.Other,
            },
            SpeedMbps: ni.Speed / 1_000_000,
            MacAddress: macBytes.Length == 0 ? "" : string.Join(":", macBytes.Select(b => b.ToString("X2"))),
            IsDhcp: isDhcp,
            IPv4WithMask: ipv4WithMask,
            Gateways: gateways,
            DnsServers: dns);
    }

    private static NetType MapType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet => NetType.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetType.Wireless,
        NetworkInterfaceType.Loopback => NetType.Loopback,
        NetworkInterfaceType.Tunnel => NetType.Tunnel,
        _ => NetType.Other,
    };
}
