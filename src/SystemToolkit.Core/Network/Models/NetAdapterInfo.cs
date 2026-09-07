namespace SystemToolkit.Core.Network.Models;

/// <summary>适配器分类（BCL <c>NetworkInterfaceType</c> 的项目内投影，枚举名保持稳定便于 UI 判定）。</summary>
public enum NetType
{
    /// <summary>以太网（板载 / PCIe / USB 有线网卡）。</summary>
    Ethernet,
    /// <summary>无线网卡（Wi-Fi 802.11 系列）。</summary>
    Wireless,
    /// <summary>环回接口（127.0.0.1 / ::1，采集时通常已被过滤）。</summary>
    Loopback,
    /// <summary>隧道适配器（Teredo / ISATAP / VPN 隧道等）。</summary>
    Tunnel,
    /// <summary>其余未单列类型（PPP、蓝牙 PAN、虚拟交换机等）。</summary>
    Other,
}

/// <summary>适配器工作状态（BCL <c>OperationalStatus</c> 的投影；本项目只关心通/断）。</summary>
public enum OperStatus
{
    /// <summary>已连接，接口可正常收发。</summary>
    Up,
    /// <summary>不工作（断开 / 被禁用等）。</summary>
    Down,
    /// <summary>其余 BCL 状态（测试中 / 休眠 / 底层断开等）。</summary>
    Other,
}

/// <summary>
/// 一台网络适配器的只读快照（BCL <c>NetworkInterface</c> 一次采集的结果）。
/// <para>
/// <c>Name</c> 是 netsh 使用的接口名（如「以太网」，可能含空格与中文）——
/// 作为命令参数引用时必须加引号（见 <c>NetshArgs</c>）。
/// </para>
/// </summary>
public sealed record NetAdapterInfo(
    string Name,
    string Description,
    NetType Type,
    OperStatus Status,
    long SpeedMbps,
    string MacAddress,
    bool IsDhcp,
    IReadOnlyList<string> IPv4WithMask,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers);
