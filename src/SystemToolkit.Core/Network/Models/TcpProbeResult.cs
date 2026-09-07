namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// TCP 端口可达性结果（M6b P1-3）：成功带实测连接耗时；失败带原因
/// （「域名无法解析」/「连接超时」/「连接被拒绝」——区分「我网断了」与「那个服务挂了」）。
/// </summary>
public sealed record TcpProbeResult(bool Success, int? LatencyMs, string? Error);
