namespace SystemToolkit.Core.Backup.Contracts;

/// <summary>一次快照恢复的执行报告（计数汇总 + 失败/跳过明细）。</summary>
public sealed class RestoreReport
{
    /// <summary>快照所属规则的名称。</summary>
    public string RuleName { get; set; } = "";

    /// <summary>被恢复快照的 ID。</summary>
    public string SnapshotId { get; set; } = "";

    /// <summary>恢复是否全部成功（无写入失败且无校验失败）。</summary>
    public bool Success { get; set; }

    /// <summary>清单中的文件总数。</summary>
    public int Total { get; set; }

    /// <summary>成功恢复的文件数。</summary>
    public int Restored { get; set; }

    /// <summary>因冲突策略等原因被跳过的文件数（明细见 <see cref="SkippedItems"/>）。</summary>
    public int Skipped { get; set; }

    /// <summary>写入失败的文件数（明细见 <see cref="Failures"/>）。</summary>
    public int Failed { get; set; }

    /// <summary>恢复后 SHA-256 校验不一致的文件数。</summary>
    public int VerifyFailed { get; set; }

    /// <summary>面向用户展示的结果说明文本。</summary>
    public string Message { get; set; } = "";

    /// <summary>失败条目明细列表（相对路径 + 失败原因）。</summary>
    public List<string> Failures { get; set; } = new List<string>();

    /// <summary>
    /// 被跳过而非失败的条目及其原因（如多源快照无法精确还原空目录的归属位置）。
    /// 与 <see cref="Skipped"/> 计数配套，避免"跳过"像过去那样完全静默、用户无从得知。
    /// </summary>
    public List<string> SkippedItems { get; set; } = new List<string>();

    /// <summary>本次恢复是否被用户取消。</summary>
    public bool Canceled { get; set; }
}

/// <summary>恢复冲突预演条目（批次二；不落任何文件的只读探测结果）。</summary>
public sealed record RestorePreviewEntry(
    string RelativePath,
    string TargetPath,
    bool ExistsInTarget,
    bool Blocked,
    string? BlockReason);

/// <summary>恢复冲突预演报告：向导第 3 步展示的数据源。</summary>
public sealed class RestorePreviewReport
{
    /// <summary>清单中的文件总数。</summary>
    public int Total { get; set; }

    /// <summary>目标已存在（构成冲突）的文件数。</summary>
    public int ExistsCount { get; set; }

    /// <summary>恢复时会被安全校验拒绝的条目数（不安全相对路径 / 越界）。</summary>
    public int BlockedCount { get; set; }

    /// <summary>逐文件预演明细。</summary>
    public List<RestorePreviewEntry> Entries { get; set; } = new List<RestorePreviewEntry>();
}
