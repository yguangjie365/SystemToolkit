namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// TCP 全局参数读取 / 保守调优 / 快照回退。
/// </summary>
public interface ITcpTuningService
{
    /// <summary>读当前 TCP 全局参数（netsh show global 解析 + 注册表 NetworkThrottlingIndex）。
    /// 【审查修复 R2】支持取消：netsh show global 最坏 30s，用户应可中断。</summary>
    Task<Models.TcpGlobalSettings> ReadAsync(CancellationToken ct = default);

    /// <summary>是否存在可还原的改前快照（仅保留最近一份）。</summary>
    bool HasSnapshot { get; }

    /// <summary>
    /// 应用目标参数。<b>内部先拍改前快照</b>（仅与当前不同的项才写 netsh / 注册表）。
    /// 【核实报告 N12】返回实际写入/跳过项摘要——确认清单与实际执行的一致性在 VM 可见。
    /// 【审查修复 R2】支持取消：最坏四项命令 × 60s = 240s 可调中取消。
    /// </summary>
    Task<Models.TcpApplyResult> ApplyAsync(Models.TcpGlobalSettings target, Action<string> onLine, CancellationToken ct = default);

    /// <summary>
    /// 依最近一份快照逐项还原。无快照时抛 <see cref="InvalidOperationException"/>（拒绝盲还原）。
    /// 还原本身<b>不覆盖</b>快照——保留这条「后悔药」直到下次应用。
    /// 【审查修复 R2】支持取消：逐项 netsh 命令最坏 60s。
    /// </summary>
    Task RestoreAsync(Action<string> onLine, CancellationToken ct = default);

    /// <summary>【M6c P1-6】列出各接口当前跃点数（Loopback 伪接口已过滤）。</summary>
    Task<IReadOnlyList<Models.InterfaceMetricInfo>> ListInterfaceMetricsAsync(CancellationToken ct = default);

    /// <summary>
    /// 【M6c P1-6】应用指定接口的跃点数。<b>应用前自动快照</b>（快照同时记录全部接口的
    /// 当前 metric，与 TCP 参数共用一份快照文件——「一份改前快照」语义不变）。
    /// </summary>
    Task ApplyInterfaceMetricAsync(string adapter, int metric, Action<string> onLine, CancellationToken ct = default);
}
