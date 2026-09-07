using SystemToolkit.Core.Backup.Models;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>规则导出文件（JSON）的容器格式：元数据 + 规则列表。</summary>
public sealed class RuleExportContainer
{
    /// <summary>导出文件格式版本（当前为 1，导入侧用于未来的格式兼容判断）。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>导出工具标识。</summary>
    public string Tool { get; set; } = "FileBackupTool";

    /// <summary>导出时间（本地时间 ISO 文本）。</summary>
    public string ExportedAt { get; set; } = "";

    /// <summary>导出的规则列表。</summary>
    public List<BackupRule> Rules { get; set; } = new List<BackupRule>();
}
