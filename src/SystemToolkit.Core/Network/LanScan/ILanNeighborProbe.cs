namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// 邻居表一行（<c>GetIpNetTable2</c> 视角）：IP↔MAC 绑定 + 所走接口与 NL 状态。
/// </summary>
/// <param name="Ip">点分十进制 IPv4。</param>
/// <param name="Mac">归一化 MAC（大写冒号分隔）。</param>
/// <param name="IfIndex">Windows 接口索引（仅展示/诊断用，判归属用 MAC 对比本机网卡）。</param>
/// <param name="State"><c>NL_NEIGHBOR_STATE</c> 原值（0..6）。</param>
public readonly record struct NeighborEntry(string Ip, string Mac, int IfIndex, int State);

/// <summary>
/// ARP 探针抽象——把 iphlpapi 的 <c>SendARP</c> / <c>GetIpNetTable2</c> 收在这个接缝后面，
/// <see cref="LanScanService"/> 与 <see cref="LanDiffEngine"/> 全部逻辑可用假件脱离真网卡测试。
/// <para>
/// 实现见 <see cref="LanNeighborProbe"/>。两枚 API 均免提权（NET-6 调研定稿：仅
/// raw socket 抓包路线才需要管理员，V1 不走该路线）。
/// </para>
/// </summary>
public interface ILanNeighborProbe
{
    /// <summary>
    /// 主动 ARP 探测（<c>SendARP</c>）：<paramref name="localIp"/> 指定出接口，
    /// 命中返回 <c>true</c>（收到应答）；无应答/超时/参数错返回 <c>false</c>，不抛。
    /// </summary>
    bool TryPoke(string ipv4, string localIp);

    /// <summary>读全量 IPv4 邻居表快照（<c>GetIpNetTable2(AF_INET)</c>）。失败返回空表，不抛。</summary>
    IReadOnlyList<NeighborEntry> ReadNeighbors();
}
