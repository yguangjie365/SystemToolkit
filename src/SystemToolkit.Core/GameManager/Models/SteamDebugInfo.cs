namespace SystemToolkit.Core.GameManager.Models;

/// <summary>诊断数据包（对应 NexBox steam_debug 命令，用于排障 VDF 解析失败时提供可观测性）。</summary>
public sealed record SteamDebugInfo
{
    /// <summary>Steam 安装目录（注册表解析结果；未安装为空）。</summary>
    public string? SteamPath { get; init; }

    /// <summary>当前是否存在 steam.exe 进程。</summary>
    public bool SteamRunning { get; init; }

    /// <summary>loginusers.vdf 完整路径。</summary>
    public string LoginUsersPath { get; init; } = string.Empty;

    /// <summary>loginusers.vdf 是否存在。</summary>
    public bool LoginUsersExists { get; init; }

    /// <summary>loginusers.vdf 文件原文（排障用）。</summary>
    public string? LoginUsersRaw { get; init; }

    /// <summary>loginusers.vdf 解析错误信息（无错误为空）。</summary>
    public string? LoginUsersParseError { get; init; }

    /// <summary>解析出的用户数。</summary>
    public int LoginUsersCount { get; init; }

    /// <summary>libraryfolders.vdf 完整路径（新/旧格式回退后的实际路径）。</summary>
    public string LibraryFoldersPath { get; init; } = string.Empty;

    /// <summary>libraryfolders.vdf 是否存在。</summary>
    public bool LibraryFoldersExists { get; init; }

    /// <summary>libraryfolders.vdf 文件原文（排障用）。</summary>
    public string? LibraryFoldersRaw { get; init; }

    /// <summary>libraryfolders.vdf 解析错误信息（无错误为空）。</summary>
    public string? LibraryFoldersParseError { get; init; }

    /// <summary>解析出的库目录数。</summary>
    public int LibraryFoldersCount { get; init; }

    /// <summary>实际扫描过的 steamapps 目录清单。</summary>
    public IReadOnlyList<string> SteamAppsDirsScanned { get; init; } = Array.Empty<string>();

    /// <summary>找到的 appmanifest_*.acf 文件清单。</summary>
    public IReadOnlyList<string> AppManifestFilesFound { get; init; } = Array.Empty<string>();

    /// <summary>成功解析的游戏数。</summary>
    public int GamesCount { get; init; }

    /// <summary>各文件解析错误明细。</summary>
    public IReadOnlyList<string> GamesParseErrors { get; init; } = Array.Empty<string>();
}
