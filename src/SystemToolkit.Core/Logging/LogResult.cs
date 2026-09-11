namespace SystemToolkit.Core.Logging;

/// <summary>
/// 操作结果（06 册 §2 八字段之一）。长耗时 / 破坏性操作必须落一个值——
/// 🔴 含「用户取消」，静默返回是旧工程实测教训（NFR-LOG-01）。
/// </summary>
public enum LogResult
{
    /// <summary>成功完成。</summary>
    Success = 0,

    /// <summary>失败（异常、非零退出、校验不过）。</summary>
    Failed = 1,

    /// <summary>用户取消或超时中止（已按规范留痕，不算静默）。</summary>
    Cancelled = 2,

    /// <summary>被安全机制拒绝（白名单外提权、路径越界、确认门拒绝）。</summary>
    Rejected = 3,
}
