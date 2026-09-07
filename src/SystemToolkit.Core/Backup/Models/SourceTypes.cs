namespace SystemToolkit.Core.Backup.Models;

/// <summary>备份源类型常量（<see cref="BackupRule.SourceType"/> 字段取值）。</summary>
public static class SourceTypes
{
    /// <summary>源为单个文件。</summary>
    public const string File = "file";

    /// <summary>源为文件夹（递归包含子目录）。</summary>
    public const string Folder = "folder";
}
