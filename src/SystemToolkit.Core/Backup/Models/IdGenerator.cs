namespace SystemToolkit.Core.Backup.Models;

/// <summary>短 ID 生成工具。</summary>
public static class IdGenerator
{
    /// <summary>生成 32 位无连字符的小写 GUID（N 格式），用作规则/快照的唯一 ID。</summary>
    public static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }
}
