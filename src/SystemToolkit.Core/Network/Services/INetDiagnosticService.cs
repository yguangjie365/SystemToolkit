namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 一键诊断：固定四步（适配器 → 网关 → 公网 → DNS 解析），<b>不短路、全部跑完再汇总</b>，
/// 让用户看到完整证据链。结论由 <see cref="BuildConclusion"/> 依步骤结果规则化生成
/// （含「ICMP 被过滤」分支——防把禁 Ping 的公网环境误报为断网，设计文档 §4.3 v0.2 新增）。
/// 连通性探测经 <see cref="INetProbe"/> 注入，测试不发真实 ICMP / DNS 包。
/// </summary>
public interface INetDiagnosticService
{
    /// <summary>执行四步诊断，返回各步结果（不含结论行——结论见 <see cref="Conclusion"/>）。</summary>
    Task<IReadOnlyList<Models.DiagStepResult>> RunAsync(CancellationToken ct = default);

    /// <summary>
    /// 带进度回调的诊断（2026-09-06 移植新增）：每步开始时上报 <see cref="Models.DiagStatus.Running"/>
    /// 快照、结束时上报最终结果（VM 按 Step 名对位刷新行状态）——替代旧 UI 的假 Running 动画。
    /// </summary>
    Task<IReadOnlyList<Models.DiagStepResult>> RunWithProgressAsync(IProgress<Models.DiagStepResult>? progress, CancellationToken ct = default);

    /// <summary>依步骤结果推导规则化结论（公开以便结论规则做表驱动测试）。</summary>
    string BuildConclusion(IReadOnlyList<Models.DiagStepResult> steps);

    /// <summary>最近一次诊断的结论（未诊断过为空串）。</summary>
    string Conclusion { get; }

    /// <summary>
    /// 【M6a】MTU 路径探测：对公网目标（<see cref="NetDiagnosticService.PublicProbeAddress"/>）
    /// 以 DF 位二分 576..1472 载荷，返回路径 MTU 与建议网卡 MTU；
    /// 探测失败（全尺寸不可通过 / 无公网路径）返回 null。
    /// </summary>
    Task<Models.MtuProbeResult?> ProbePathMtuAsync(CancellationToken ct = default);

    /// <summary>【M6b·P1-3】TCP 端口可达性测试（host 支持 IP 与域名；结果三态：成功带延迟 / 超时 / 被拒绝 / 域名无法解析）。</summary>
    Task<Models.TcpProbeResult> TestPortAsync(string host, int port, CancellationToken ct = default);

    /// <summary>【M6b·P1-4】hosts 异常检查（纯只读：解析非注释条目并标记「需留意」）。</summary>
    Task<HostsCheckResult> CheckHostsAsync(CancellationToken ct = default);
}
