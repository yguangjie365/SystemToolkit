using SystemToolkit.Core.Backup.Models;

namespace SystemToolkit.Core.Backup.Contracts;

/// <summary>备份服务契约：执行单条备份规则并返回结果汇总。</summary>
public interface IBackupService
{
    /// <summary>
    /// 执行一条备份规则：源校验 → 扫描 → 并发复制 + SHA-256 → 写快照元数据 → 快照上限清理。
    /// 失败不抛异常，统一经 <see cref="BackupResult"/> 的 Success/Failures/Canceled 表达。
    /// </summary>
    /// <param name="rule">要执行的备份规则。</param>
    /// <param name="reporter">可选的进度/日志上报器。</param>
    /// <param name="ct">取消令牌；取消时清理未完成的快照目录（已写完元数据的除外）。</param>
    /// <returns>本次备份的结果汇总。</returns>
    Task<BackupResult> BackupRuleAsync(BackupRule rule, IProgressReporter? reporter = null, CancellationToken ct = default(CancellationToken));
}
