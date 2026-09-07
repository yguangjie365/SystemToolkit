namespace SystemToolkit.Core.GameManager.Models;

/// <summary>Steam 游戏库文件夹（一个库 = 一个分区上的 SteamLibrary 目录，可能有多块硬盘）。</summary>
public sealed record SteamLibrary
{
    /// <summary>libraryfolders.vdf 中的顺序索引（0 = Steam 主安装目录，1+ = 自定义库）。</summary>
    public uint Index { get; init; }

    /// <summary>绝对路径，如 "D:\\SteamLibrary"（反斜杠规范化，统一 \）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>用户自定义库标签（可能为空字符串）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>所在磁盘的总字节数（VDF totalsize 优先，缺失时取 DriveInfo 总字节）。</summary>
    public ulong TotalSize { get; init; }

    /// <summary>所在磁盘的剩余字节数（来自 DriveInfo，实时数据）。</summary>
    public ulong FreeSize { get; init; }

    /// <summary>此库中包含的 appid 列表（字符串，与 apps{} 下的 key 对应）。</summary>
    public IReadOnlyList<string> Apps { get; init; } = Array.Empty<string>();
}
