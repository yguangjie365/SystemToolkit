using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 网络探测抽象（Ping + DNS 解析 + M6a 量化/DF 探测）。与 <see cref="ICommandRunner"/> /
/// <see cref="IElevationProvider"/> 同思路：把真实系统的不确定延迟与失败面挡在接口后面，
/// 让诊断服务的<b>结论规则</b>可以在测试里被完整覆盖（拔网线、ICMP 被过滤、DNS 故障等场景）。
/// </summary>
public interface INetProbe
{
    /// <summary>Ping 指定地址；返回是否可达。超时以毫秒计。</summary>
    Task<bool> PingAsync(string address, int timeoutMs, CancellationToken ct = default);

    /// <summary>DNS 解析指定主机名；返回是否解析成功（不关心具体地址）。</summary>
    Task<bool> ResolveAsync(string host, CancellationToken ct = default);

    /// <summary>
    /// 【M6a】量化探测：对目标连发 <paramref name="count"/> 次 Ping，统计丢包与延迟
    /// （min/avg/max，单位毫秒，仅统计成功包）。全部失败时 <see cref="PingQuantifyResult.Received"/>
    /// 为 0、延迟三值为 null（延迟无从谈起，≠ 0）。
    /// </summary>
    Task<PingQuantifyResult> PingQuantifyAsync(string host, int count, int timeoutMs, int intervalMs, CancellationToken ct = default);

    /// <summary>
    /// 【M6a】DF 位探测：发送指定<b>载荷</b>大小（不含 28 字节 IP+ICMP 头）的不分片 Ping，
    /// 返回是否在超时内收到回应。用于 MTU 路径二分。
    /// </summary>
    Task<bool> PingDontFragmentAsync(string host, int payloadSize, int timeoutMs, CancellationToken ct = default);

    /// <summary>
    /// 【M6b·P1-3】TCP 端口可达性：连接 host:port（host 支持 IP 与域名——域名解析失败
    /// 单独报，顺带覆盖 DNS 故障场景）。成功返回实测连接耗时，失败返回 Error。
    /// </summary>
    Task<TcpProbeResult> ConnectTcpAsync(string host, int port, int timeoutMs, CancellationToken ct = default);
}
