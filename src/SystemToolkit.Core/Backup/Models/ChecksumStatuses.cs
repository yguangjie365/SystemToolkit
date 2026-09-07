namespace SystemToolkit.Core.Backup.Models;

/// <summary>快照校验状态常量（持久化于 manifest/meta 的 ChecksumStatus 字段）。</summary>
public static class ChecksumStatuses
{
    /// <summary>全部文件 SHA-256 校验通过。</summary>
    public const string Passed = "passed";

    /// <summary>存在校验失败/未完成的文件（部分成功模式）。</summary>
    public const string Failed = "failed";

    /// <summary>校验被跳过（预留取值）。</summary>
    public const string Skipped = "skipped";
}
