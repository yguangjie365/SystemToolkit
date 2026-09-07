namespace SystemToolkit.Core.Backup.Contracts;

/// <summary>恢复时的同名文件冲突处理策略。</summary>
public enum ConflictPolicy
{
    /// <summary>逐条询问用户后由用户裁决。</summary>
    Ask,

    /// <summary>直接覆盖目标位置已存在的同名文件。</summary>
    Overwrite,

    /// <summary>保留已有文件，恢复文件改名为唯一新名后写入。</summary>
    Rename,

    /// <summary>跳过冲突文件，不写入。</summary>
    Skip
}
