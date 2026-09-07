namespace SystemToolkit.Core.Music.Models;

/// <summary>
/// 音频标签读取结果（<c>IMusicTagReader.Read</c> 的返回值）。
/// </summary>
/// <remarks>
/// 用结果对象而不是抛异常：扫描 5000 个文件时「读到损坏文件」是<b>预期内</b>的常态
/// （半截下载、被截断的 FLAC、零字节占位文件都会命中），
/// 不是异常路径。用异常承载预期结果会让调用方靠 catch 做控制流，也拖慢大批量扫描。
/// <para>失败原因以字符串回传，扫描器据此填 <see cref="MusicScanFailure"/>——
/// 🔴 禁止静默失败：损坏文件必须在 UI 的失败态里列出来，不能只写日志。</para>
/// </remarks>
public sealed record MusicTagReadResult
{
    /// <summary>是否读取成功。</summary>
    public bool Success { get; init; }

    /// <summary>成功时的标签载荷；失败时为 null。</summary>
    public MusicTagInfo? Tags { get; init; }

    /// <summary>失败原因（可直接展示给用户）；成功时为 null。</summary>
    public string? FailureReason { get; init; }

    /// <summary>构造成功结果。</summary>
    public static MusicTagReadResult Ok(MusicTagInfo tags) => new() { Success = true, Tags = tags };

    /// <summary>构造失败结果。</summary>
    public static MusicTagReadResult Fail(string reason) => new() { Success = false, FailureReason = reason };
}

/// <summary>
/// 一个音频文件的标签与音频属性载荷（<b>未做任何兜底</b>的原始值）。
/// </summary>
/// <remarks>
/// 标签缺失时对应成员就是空串/0，<b>不</b>在这里填「未知艺术家」或文件名——
/// 兜底是曲库级策略（同一份标签，列表展示与导出可能需要不同兜底），
/// 由 <c>LocalMusicScanner</c> 统一决定，读取器只负责如实转述文件里有什么。
/// </remarks>
public sealed record MusicTagInfo
{
    /// <summary>标题（ID3 TIT2 / Vorbis TITLE / MP4 ©nam）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>艺术家（ID3 TPE1 / Vorbis ARTIST / MP4 ©ART）。</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>专辑名（ID3 TALB / Vorbis ALBUM / MP4 ©alb）。</summary>
    public string Album { get; init; } = string.Empty;

    /// <summary>专辑艺术家（ID3 TPE2 / Vorbis ALBUMARTIST / MP4 aART）。</summary>
    public string AlbumArtist { get; init; } = string.Empty;

    /// <summary>流派（ID3 TCON / Vorbis GENRE）。多值时取第一个。</summary>
    public string Genre { get; init; } = string.Empty;

    /// <summary>音轨号。缺失或无效时为 0。</summary>
    public uint TrackNumber { get; init; }

    /// <summary>发行年份。缺失或无效时为 0。</summary>
    public uint Year { get; init; }

    /// <summary>时长（毫秒）。容器未写出时长信息时为 0。</summary>
    public ulong DurationMs { get; init; }

    /// <summary>比特率（kbps）。</summary>
    public int BitrateKbps { get; init; }

    /// <summary>采样率（Hz）。</summary>
    public int SampleRateHz { get; init; }

    /// <summary>声道数。</summary>
    public int Channels { get; init; }

    /// <summary>是否含至少一张内嵌图片。</summary>
    /// <remarks>
    /// 只存标志位而不存字节，是为了让万级曲库的 JSON 不被 base64 封面撑爆
    /// （见 <see cref="MusicSong"/> 备注）；字节由 <c>IMusicTagReader.ReadCover</c> 按需取。
    /// <para>⚠️ 别指望用 <c>TagLib.ReadStyle.PictureLazy</c> 来省掉读图片字节的开销——
    /// 实测它会让 MP3/FLAC 的音频属性整个变成 null，详见 <c>TagLibMusicTagReader.Read</c> 备注。</para>
    /// </remarks>
    public bool HasCover { get; init; }

    /// <summary>内嵌歌词原文（ID3 USLT / Vorbis LYRICS / MP4 ©lyr）。无歌词时为 null。</summary>
    /// <remarks>
    /// 原文可能是带时间标签的 LRC，也可能是无时间标签的纯文本；
    /// 解析由 <c>LyricParser</c> 负责，读取器不做格式判断。
    /// </remarks>
    public string? Lyrics { get; init; }
}

/// <summary>一张内嵌封面图（原始字节 + MIME 类型）。</summary>
/// <param name="Data">图片字节（未解码，交给 UI 层按 WPF 位图规则解码并 Freeze）。</param>
/// <param name="MimeType">MIME 类型（如 <c>image/jpeg</c>、<c>image/png</c>）。</param>
/// <remarks>
/// 刻意<b>不</b>在 Core 层转 base64 data URL：转换成本与内存占用都落在
/// 「每首歌都留一份完整封面副本」上，而 WPF 侧需要的是可解码的字节流。
/// 旧工程的 data URL 做法是曲库 JSON 膨胀到 GB 量级的直接原因（见 <see cref="MusicSong"/> 备注）。
/// </remarks>
public sealed record MusicCover(byte[] Data, string MimeType);
