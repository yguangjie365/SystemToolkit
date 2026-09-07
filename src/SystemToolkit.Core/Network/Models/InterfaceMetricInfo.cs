namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// 接口跃点数（metric）条目（M6c P1-6）：多网卡共存时的路由优先级，
/// 数值越小优先级越高。Loopback 伪接口已过滤（无路由意义）。
/// </summary>
public sealed record InterfaceMetricInfo(string Name, int Metric);
