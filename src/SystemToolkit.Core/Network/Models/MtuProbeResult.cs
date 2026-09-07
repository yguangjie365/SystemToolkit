namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// MTU 路径探测结果（M6a P0-2）。
/// <see cref="PathMtu"/> = 路径 MTU（含 28 字节 IP+ICMP 头）；<see cref="SuggestedNicMtu"/> = 建议网卡 MTU（等于路径 MTU）。
/// </summary>
public sealed record MtuProbeResult(int PathMtu, int SuggestedNicMtu);
