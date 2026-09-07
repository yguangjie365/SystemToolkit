namespace SystemToolkit.Core.Backup.Models;

/// <summary>快照状态常量（持久化于 manifest/meta 的 Status 字段）。</summary>
public static class SnapshotStatuses
{
    /// <summary>备份完成、全部文件写入成功。</summary>
    public const string Success = "success";

    /// <summary>存在写入失败的文件（部分成功模式）。</summary>
    public const string Failed = "failed";
}
