namespace SystemToolkit.Core.GameManager.Models;

/// <summary>Steam 已安装游戏（对应 appmanifest_{appid}.acf 一条记录 + 游玩时长拼接）。</summary>
public sealed record SteamGame
{
    /// <summary>AppID（正整数，> 0 为有效条目）。</summary>
    public uint AppId { get; init; }

    /// <summary>游戏名称（name 字段）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>安装子目录名（installdir，不是完整路径）。</summary>
    public string InstallDir { get; init; } = string.Empty;

    /// <summary>所属库路径（libraryfolders.vdf 对应项的 Path，规范化为反斜杠）。</summary>
    public string LibraryPath { get; init; } = string.Empty;

    /// <summary>磁盘占用字节数（SizeOnDisk）。</summary>
    public ulong SizeOnDisk { get; init; }

    /// <summary>
    /// 安装状态位（StateFlags），对应 Steam <c>EAppState</c> 的位或：
    /// <c>1</c>=Uninstalled · <c>2</c>=UpdateRequired · <c>4</c>=FullyInstalled · <c>8</c>=UpdateQueued ·
    /// <c>32</c>=FilesMissing · <c>256</c>=UpdateRunning · <c>512</c>=UpdatePaused · <c>1024</c>=UpdateStarted ·
    /// <c>2048</c>=Uninstalling · <c>4096</c>=BackupRunning。
    /// <para>
    /// 🔴 常见组合：<c>4</c> = 装好且最新；<c>6</c>（=2|4）= **已安装但待更新**——不是"下载中"。
    /// 消费方判「装全」必须同时看 bit2 置位**且** bit1 未置位，只看 bit2 会把 6 说成「已安装」。
    /// </para>
    /// </summary>
    public uint StateFlags { get; init; }

    /// <summary>最后更新时间（unix 秒）。</summary>
    public ulong LastUpdated { get; init; }

    /// <summary>最后拥有者 Steam64 ID（LastOwner，可能为空串）。</summary>
    public string LastOwner { get; init; } = string.Empty;

    /// <summary>当前构建 ID（buildid）。</summary>
    public uint BuildId { get; init; }

    /// <summary>待下载字节数（BytesToDownload，大于 0 且小于 BytesDownloaded 时显示为下载中）。</summary>
    public ulong BytesToDownload { get; init; }

    /// <summary>已下载字节数。</summary>
    public ulong BytesDownloaded { get; init; }

    /// <summary>游玩时长（分钟，从 userdata/*/config/localconfig.vdf 读取）。</summary>
    public ulong PlaytimeMinutes { get; init; }

    /// <summary>本地封面图绝对路径（librarycache 命中时为 header.jpg / library_600x900_2x.jpg）；null = 无封面，UI 用占位。</summary>
    public string? CoverImagePath { get; init; }

    /// <summary>最近游玩时间（unix 秒）。0 表示从未玩过。</summary>
    public long LastPlayed { get; init; }
}
