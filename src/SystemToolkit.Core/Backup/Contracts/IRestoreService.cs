using SystemToolkit.Core.Backup.Models;

namespace SystemToolkit.Core.Backup.Contracts;

/// <summary>恢复服务契约：把一份快照还原到原始路径或指定目标路径。</summary>
public interface IRestoreService
{
    /// <summary>
    /// 恢复一份快照：清单安全校验 → 磁盘空间预检 → 按冲突策略写入 → 恢复后校验 → 汇总报告。
    /// </summary>
    /// <param name="info">要恢复的快照元数据（含文件清单与数据目录路径）。</param>
    /// <param name="targetRoot">指定恢复目标根目录；null 表示恢复到原始路径。</param>
    /// <param name="policy">同名文件冲突处理策略。</param>
    /// <param name="userChoice">policy 为 Ask 时的逐条裁决回调：入参为冲突相对路径列表，返回用户选择的策略。</param>
    /// <param name="reporter">可选的进度/日志上报器。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="trustedRoots">
    /// 可信源路径白名单（S2，REVIEW-2026-08-30）：来自规则当前配置（rules.json）的源路径，
    /// 与 manifest 的 SourcePaths 来源分离——恢复到原路径时的范围白名单优先采用它，
    /// 篡改 manifest 单独一处无法扩大写入范围；为 null 时回退 manifest 白名单（弱信任，留痕）。
    /// </param>
    /// <returns>恢复结果报告（计数汇总与失败/跳过明细）。</returns>
    Task<RestoreReport> RestoreSnapshotAsync(SnapshotInfo info, string? targetRoot, ConflictPolicy policy, Func<IReadOnlyList<string>, ConflictPolicy>? userChoice = null, IProgressReporter? reporter = null, CancellationToken ct = default(CancellationToken), IReadOnlyList<string>? trustedRoots = null);
}

/// <summary>冲突预演提供者（批次二恢复向导第 3 步数据源）。</summary>
public interface IRestorePreviewProvider
{
    /// <summary>
    /// 按与真实恢复完全一致的目标解析规则，只读探测目标状态，不写任何文件。
    /// </summary>
    /// <param name="info">快照清单。</param>
    /// <param name="targetRoot">恢复目标根（null = 恢复到原路径）。</param>
    /// <param name="trustedRoots">可信源路径白名单（规则当前配置优先）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>逐文件冲突明细（含会被安全校验拒绝的条目）。</returns>
    Task<RestorePreviewReport> PreviewConflictsAsync(
        SnapshotInfo info, string? targetRoot,
        IReadOnlyList<string>? trustedRoots = null, CancellationToken ct = default);
}
