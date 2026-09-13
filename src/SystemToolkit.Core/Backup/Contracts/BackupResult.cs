using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Core.Backup.Contracts;

/// <summary>单条备份规则的执行结果汇总（成功状态、计数、校验状态与失败明细）。</summary>
public sealed class BackupResult
{
    /// <summary>触发本次备份的规则 ID。</summary>
    public required string RuleId { get; init; }

    /// <summary>触发本次备份的规则名称。</summary>
    public required string RuleName { get; init; }

    /// <summary>备份是否完全成功（存在任一失败文件时为 false）。</summary>
    public bool Success { get; init; }

    /// <summary>本次创建的快照目录完整路径（未能创建快照时为 null）。</summary>
    public string? SnapshotDir { get; init; }

    /// <summary>成功复制并登记到清单的文件数量。</summary>
    public int FileCount { get; init; }

    /// <summary>备份文件的总字节数。</summary>
    public long TotalSize { get; init; }

    /// <summary>校验状态常量（取值见 Models.ChecksumStatuses：passed/failed/skipped）。</summary>
    public string ChecksumStatus { get; init; } = "passed";

    /// <summary>面向用户展示的结果说明文本（成功/部分成功/失败/取消）。</summary>
    public string Message { get; init; } = "";

    /// <summary>
    /// 备份完成后的**读回校验**报告（抽样或全量）。B5a。
    /// 🔴 为什么需要它：复制阶段算的是**源**哈希（见 <c>BackupService.CopyAndVerifyAsync</c> 的
    /// "写入字节即源字节"注释），**目标盘是否真的写对从未被验证** —— 这份报告是唯一的证据。
    /// 校验未执行（按设置关闭 / 被取消 / 异常）时为 null，**调用方不得据此认为"已通过"**。
    /// </summary>
    public SnapshotVerifyReport? VerifyReport { get; init; }

    /// <summary>
    /// 本次备份中**复用上一份快照**（未重读源文件）的文件数（B5b-③；默认关时恒为 0）。
    /// 🔴 &gt; 0 时 <see cref="ChecksumStatus"/> 必为 <c>skipped</c> —— 本次没读源文件，
    /// 不能声称"源内容已校验"。
    /// </summary>
    public int ReusedFileCount { get; init; }

    /// <summary>失败条目明细列表（相对路径 + 失败原因）。</summary>
    public List<string> Failures { get; init; } = new List<string>();

    /// <summary>本次备份是否被用户取消。</summary>
    public bool Canceled { get; init; }
}
