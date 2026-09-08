using System.Text.Json.Serialization;
using SystemToolkit.Core.Music.Online;

namespace SystemToolkit.Core.Music.Models;

/// <summary>播放状态。</summary>
public enum PlayState
{
    /// <summary>已停止：无当前曲目，播放链路已释放。</summary>
    Stopped,

    /// <summary>正在播放。</summary>
    Playing,

    /// <summary>已暂停：播放链路保留，可 Resume 续播。</summary>
    Paused,
}

/// <summary>
/// 播放模式（对照旧工程 <c>MusicService.togglePlayMode</c>）。
/// <para>旧工程还有 <c>Heartbeat</c>（心动模式），依赖网易云「相似曲目推荐」接口，
/// 属二阶段第三方平台能力，本阶段不纳入。</para>
/// </summary>
public enum PlayMode
{
    /// <summary>列表循环：播完最后一首回到第一首。</summary>
    List,

    /// <summary>随机播放。</summary>
    Shuffle,

    /// <summary>单曲循环：播完重复当前曲目。</summary>
    One,
}

/// <summary>
/// 本地曲库中的一首曲目：音频标签 + 音频属性 + 文件系统元数据的合并结果。
/// </summary>
/// <remarks>
/// <para><b>与旧工程 <c>MusicSong</c> 的差异</b>（旧结构是 QQ/网易云/本地三源共用的，本阶段只做本地）：
/// 删去 <c>Provider</c>/<c>Mid</c>/<c>MediaMid</c>/<c>Fee</c>/<c>QqSongId</c>/<c>Playable</c> 六个平台字段，
/// 以及 <c>Cover</c> 字段——见下条。</para>
/// <para>🔴 <b>不内嵌封面数据</b>：旧工程把封面转成 base64 data URL 存进 <c>Cover</c>，
/// 而曲库是按 Q-008 裁定落成单个 JSON 文件的，5000 首 × 50–200 KB 封面会让 JSON 膨胀到
/// 250 MB–1 GB 量级，读写都不可接受。本结构只保留 <see cref="HasEmbeddedCover"/> 标志位，
/// 封面由 <c>IMusicTagReader.ReadCover</c> 在 UI 需要时按路径惰性读取。</para>
/// <para>可 JSON 往返：全部成员为 <c>init</c> 属性且带默认值，
/// <see cref="System.Text.Json.JsonSerializer"/> 无需自定义转换器即可序列化/反序列化。</para>
/// </remarks>
public sealed record MusicSong
{
    private const string LocalIdPrefix = "local:";

    /// <summary>
    /// 由绝对路径生成跨进程稳定的曲目 ID（<see cref="Id"/> 的唯一合法来源）：
    /// Windows 下路径大写归一 → SHA-256 前 8 字节十六进制。
    /// 从 LocalMusicScanner 提升到模型（MUSIC-5）：Id 格式是模型语义的一部分，
    /// 测试与持久化层也需要同一实现，避免双份逻辑漂移。
    /// </summary>
    public static string BuildIdFor(string fullPath)
    {
        string normalized = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return LocalIdPrefix + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// 曲目唯一 ID，形如 <c>local:&lt;绝对路径 SHA-256 前 16 位十六进制&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>必须跨进程稳定</b>——它是持久化曲库、播放队列、去重比对的键。
    /// 旧工程用 <c>path.GetHashCode(StringComparison.OrdinalIgnoreCase)</c> 生成，
    /// 而 .NET Core 起字符串哈希<b>每进程随机化</b>（防哈希碰撞 DoS），
    /// 重启后同一首歌 ID 就变了，缓存命中与去重会静默失效。故改用 SHA-256。
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>曲目本地文件绝对路径（本阶段唯一音源，非空）。</summary>
    public string LocalPath { get; init; } = string.Empty;

    /// <summary>标题。标签缺失时由扫描器用文件名（不含扩展名）兜底，故非空。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>艺术家。标签缺失时由扫描器兜底为「未知艺术家」，故非空。</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>专辑名。标签缺失时为空串。</summary>
    public string Album { get; init; } = string.Empty;

    /// <summary>
    /// 专辑艺术家（合辑里各曲 <see cref="Artist"/> 不同但本字段相同）。
    /// 标签缺失时为空串，「专辑」视图分组时应回退到 <see cref="Artist"/>。
    /// </summary>
    public string AlbumArtist { get; init; } = string.Empty;

    /// <summary>流派。标签缺失时为空串。</summary>
    public string Genre { get; init; } = string.Empty;

    /// <summary>音轨号（专辑内序号）。标签缺失或无效时为 0。</summary>
    public uint TrackNumber { get; init; }

    /// <summary>发行年份。标签缺失或无效时为 0。</summary>
    public uint Year { get; init; }

    /// <summary>时长（毫秒）。无法解析时为 0。</summary>
    public ulong DurationMs { get; init; }

    /// <summary>音频比特率（kbps）。无法解析时为 0。</summary>
    public int BitrateKbps { get; init; }

    /// <summary>采样率（Hz）。无法解析时为 0。</summary>
    public int SampleRateHz { get; init; }

    /// <summary>声道数。无法解析时为 0。</summary>
    public int Channels { get; init; }

    /// <summary>文件字节数（列表展示与「曲库占用」统计用）。</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// 文件最后修改时间（UTC）。增量扫描的判定键：
    /// 与曲库中已存记录相同则跳过重新解析，不同则重读标签。
    /// </summary>
    public DateTime ModifiedTimeUtc { get; init; }

    /// <summary>是否含内嵌封面。为 true 时 UI 才值得去调 <c>ReadCover</c>。</summary>
    public bool HasEmbeddedCover { get; init; }

    /// <summary>是否含内嵌歌词（USLT/LYRICS 帧）。为 false 时歌词 Tab 应回退找同名 .lrc。</summary>
    public bool HasEmbeddedLyrics { get; init; }

    /// <summary>
    /// 在线曲目元数据；<b>本地曲恒为 null</b>（OM-4 混合队列：本地/在线共用同一队列容器）。
    /// </summary>
    /// <remarks>
    /// <para>在线曲目的 <see cref="LocalPath"/> 存的是<b>代理播放 URL</b>（经
    /// <c>IAudioProxyService.GetProxiedAudioUrlAsync</c> 生成，引擎可直接打开），
    /// 而非磁盘路径——UI/持久化不得对在线曲目做文件系统假设（IsOnline 为 true 时）。</para>
    /// <para>曲库 JSON 持久化按 null 省略：本地曲序列化形态与 OM-4 之前完全一致。</para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OnlineTrack? Online { get; init; }

    /// <summary>是否为在线曲目（<see cref="Online"/> 非 null）。</summary>
    [JsonIgnore]
    public bool IsOnline => Online is not null;

    /// <summary>时长展示文本（如 <c>3:45</c> / <c>1:02:03</c>；未知时为 <c>—</c>）。</summary>
    /// <remarks>曲库列表、播放页、Shell 迷你条三处都要显示，收敛到一处避免三份格式化代码。</remarks>
    public string DurationText => DurationMs == 0 ? "—" : FormatDuration(DurationMs);

    /// <summary>把毫秒格式化为 <c>m:ss</c> 或 <c>h:mm:ss</c>。</summary>
    private static string FormatDuration(ulong durationMs)
    {
        var ts = TimeSpan.FromMilliseconds(durationMs);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }
}

/// <summary>
/// 持久化曲库文档（Q-008 裁定：模块私有 JSON + <c>AtomicFile</c> 落
/// <c>%AppData%/SystemToolkit/music-library.json</c>）。
/// </summary>
/// <remarks>
/// 本类型即 JSON 根对象。扫描根目录清单与「上次扫描时间」一并入库，
/// 这样重启后既能显示「上次扫描 09-07 14:20」，也能按
/// <see cref="MusicSong.ModifiedTimeUtc"/> 做增量扫描。
/// </remarks>
public sealed record MusicLibrary
{
    /// <summary>扫描根目录绝对路径清单（可多个；🟡 多值假设：不做「只取第一个」）。</summary>
    public List<string> ScanRoots { get; init; } = [];

    /// <summary>曲库全部曲目。</summary>
    public List<MusicSong> Songs { get; init; } = [];

    /// <summary>上次成功扫描完成时间（UTC）。从未扫描过时为 null。</summary>
    public DateTimeOffset? LastScanAtUtc { get; init; }

    /// <summary>曲目数（页头徽章「曲库 1234 首」直接绑定）。</summary>
    public int SongCount => Songs.Count;
}
