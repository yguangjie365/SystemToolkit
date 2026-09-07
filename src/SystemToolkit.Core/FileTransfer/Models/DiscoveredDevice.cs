using System.Net;

namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 局域网内发现的设备（通过 UDP 广播协议识别）。
/// </summary>
public sealed class DiscoveredDevice
{
    /// <summary>设备唯一标识（机器名 + 进程实例哈希），不可变。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>设备显示名称（默认为机器名），心跳刷新时可更新。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>设备 IP 地址，心跳刷新时可更新。</summary>
    /// <remarks>
    /// 【2026-09-02 修复】JSON 序列化必须忽略：System.Text.Json 反射枚举 IPAddress 的公共属性时，
    /// <c>ScopeId</c> getter 在 IPv4 地址上抛 SocketException 10045，导致整个 deviceList 推送失败、
    /// WS 连接被断（手机端表现为「未连接 + 局域网设备空」——推送其实从未成功过）。
    /// 网络信息经 <see cref="DisplayAddress"/>（纯字符串）输出，前端已按其渲染。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public IPAddress IPAddress { get; set; } = IPAddress.None;

    /// <summary>文件传输 TCP 端口，心跳刷新时可更新。</summary>
    public int TransferPort { get; set; }

    /// <summary>Web 服务 HTTP 端口（手机浏览器访问），心跳刷新时可更新。</summary>
    public int WebPort { get; set; }

    /// <summary>最后一次收到心跳的时间戳。</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>离线判定阈值（由 DeviceDiscoveryService 从 TransferSettings 同步）。</summary>
    public TimeSpan OfflineTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>设备是否在线（心跳超时判定）。</summary>
    public bool IsOnline => DateTimeOffset.UtcNow - LastSeen < OfflineTimeout;

    /// <summary>友好显示地址。</summary>
    public string DisplayAddress => $"{IPAddress}:{TransferPort}";

    /// <summary>Web 访问 URL。</summary>
    public string WebUrl => $"http://{IPAddress}:{WebPort}/";
}
