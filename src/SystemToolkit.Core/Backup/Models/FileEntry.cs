using System.Text.Json.Serialization;

namespace SystemToolkit.Core.Backup.Models;

/// <summary>快照清单（manifest.json）中的单个文件条目。</summary>
public sealed class FileEntry
{
    /// <summary>备份时的源绝对路径（用于恢复到原路径与范围白名单校验）。</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>快照内 POSIX 风格相对路径（对应 files/ 目录下的存放位置）。</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>文件字节数。</summary>
    public long Size { get; set; }

    /// <summary>备份时源文件的最后修改时间（ISO 8601，恢复后回写）。</summary>
    public string Mtime { get; set; } = "";

    /// <summary>备份时计算的 SHA-256（小写十六进制），用于恢复后校验。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>该文件完成备份的时间（ISO 8601）。</summary>
    public string BackupTime { get; set; } = "";

    /// <summary>是否记录了 SHA-256（旧版快照可能为空，此时恢复后无法校验）。</summary>
    [JsonIgnore]
    public bool HasChecksum => !string.IsNullOrEmpty(Sha256);
}
