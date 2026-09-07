using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// <see cref="IMusicTagReader"/> 的 TagLibSharp 2.3.0 实现（替换旧工程的 ATL.NET）。
/// </summary>
/// <remarks>
/// <para><b>为什么换库</b>：ATL 的传递依赖 <c>Ude.NetStandard</c> 含 GPL 授权选项，触碰本项目
/// MIT 开源的许可证红线（ADR-002 §5.4）。TagLibSharp 是 LGPL-2.1-only，经 NuGet 动态引用、
/// 不合并不 ILMerge 即满足合规要求，且零传递依赖。</para>
/// <para><b>命名空间陷阱</b>：本文件位于 <c>SystemToolkit.Core.Music.Services</c>，
/// ImplicitUsings 会引入 <c>System.IO</c>，裸写 <c>File</c> 在 <see cref="System.IO.File"/>
/// 与 <c>TagLib.File</c> 之间产生 CS0104 歧义，因此所有 TagLibSharp 类型一律全限定。</para>
/// <para><b>资源释放</b>：<c>TagLib.File</c> 实现 <see cref="IDisposable"/>（旧工程 ATL 注释里
/// 「值对象无需释放」的说法对 TagLibSharp <b>不成立</b>），每次 <c>Create</c> 都必须 <c>using</c>，
/// 否则万级扫描会耗尽文件句柄。</para>
/// <para><b>线程安全</b>：无实例可变状态，每次调用独立打开/释放文件，
/// 可被 <c>LocalMusicScanner</c> 的 <c>Parallel.ForEachAsync</c>（并行度 8）安全并发调用。</para>
/// </remarks>
public sealed class TagLibMusicTagReader : IMusicTagReader
{
    /// <summary>
    /// 可扫描的音频扩展名（含前导点）。
    /// </summary>
    /// <remarks>
    /// 🔴 这份清单是<b>两个集合的交集</b>，不是 TagLibSharp 能力的全集：
    /// <list type="number">
    ///   <item>TagLibSharp 能读出标签（已核对 2.3.0 的 <c>SupportedMimeType</c> 注册表）；</item>
    ///   <item>NAudio / MediaFoundation 能解码播放（批次 4 的播放内核）。</item>
    /// </list>
    /// TagLibSharp 另支持 <c>.dsf .wv .mpc .mp+ .mpp .aa .aax .mkv .webm .avi .divx .mp4 .m4b
    /// .m4v .m4p .wmv .asf .ogv .mks .mp1 .mp2 .m2a</c> 等，<b>刻意不纳入</b>：
    /// 视频容器（mp4/mkv/webm/avi/wmv）不是音频曲库内容；
    /// 而 dsf/wv/mpc 等虽可读标签，MediaFoundation 却解不出来，
    /// 列进曲库的结果是「能扫到、点了没声音」——比不列更糟。
    /// <para>相对旧工程 11 个扩展名<b>新增 <c>.aif</c></b>：它是已支持的 <c>.aiff</c> 的等价别名
    /// （TagLibSharp 的 <c>Aiff</c> 处理器同时注册了两者），不加会让用户看到同目录下
    /// 一部分 AIFF 被收录、一部分被忽略。</para>
    /// </remarks>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".wav", ".aiff",
        ".aif", ".aac", ".opus", ".wma", ".ape", ".oga",
    };

    private readonly ILogger _logger;

    /// <summary>构造标签读取器。</summary>
    /// <param name="logger">日志契约（失败原因记 Warn，不记 Error——坏文件是预期内结果）。</param>
    public TagLibMusicTagReader(ILogger logger) => _logger = logger;

    /// <inheritdoc/>
    public IReadOnlySet<string> SupportedExtensions => Extensions;

    /// <inheritdoc/>
    /// <remarks>
    /// 🔴 <b>必须用 <c>ReadStyle.Average</c>，不能用 <c>PictureLazy</c></b>。实测 TagLibSharp 2.3.0：
    /// <list type="bullet">
    ///   <item><c>PictureLazy</c> 单独使用时，MP3 与 FLAC 的 <c>File.Properties</c> 直接是
    ///   <b>null</b>——时长、码率、采样率、声道数全部丢失（WAV 侥幸不受影响，故只看 WAV 会漏判）。</item>
    ///   <item>更糟的是它让<b>内容根本不是音频</b>的文件也「读取成功」（只是 Properties 为 null），
    ///   损坏文件检测彻底失效——违反 🔴 禁止静默失败。</item>
    ///   <item><c>Average | PictureLazy</c> 组合的行为与 <c>Average</c> <b>完全相同</b>，
    ///   组合换不来任何收益，反而让人误以为图片是惰性读的。</item>
    /// </list>
    /// <c>Average</c> 确实会把封面字节读进内存，但每个文件读完立即 <c>Dispose</c>，
    /// 峰值占用受扫描并行度约束（8 × 约 200 KB ≈ 1.6 MB），完全可接受。
    /// 原先担心的 GB 级膨胀来自「把 base64 封面<b>持久化</b>进曲库 JSON」（旧工程做法），
    /// 与瞬时读取无关——那个问题已由「曲库只存 <see cref="MusicTagInfo.HasCover"/> 标志位」解决。
    /// </remarks>
    public MusicTagReadResult Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return MusicTagReadResult.Fail("文件路径为空");
        }

        try
        {
            using var file = TagLib.File.Create(filePath, TagLib.ReadStyle.Average);

            TagLib.Tag tag = file.Tag;
            TagLib.Properties? props = file.Properties;

            TimeSpan duration = props?.Duration ?? TimeSpan.Zero;
            string[] genres = tag.Genres ?? [];

            return MusicTagReadResult.Ok(new MusicTagInfo
            {
                Title = Clean(tag.Title),
                Artist = Clean(tag.FirstPerformer),
                Album = Clean(tag.Album),
                AlbumArtist = Clean(tag.FirstAlbumArtist),
                Genre = genres.Length > 0 ? Clean(genres[0]) : string.Empty,
                TrackNumber = tag.Track,
                Year = tag.Year,
                DurationMs = duration.TotalMilliseconds > 0 ? (ulong)duration.TotalMilliseconds : 0UL,
                BitrateKbps = props?.AudioBitrate ?? 0,
                SampleRateHz = props?.AudioSampleRate ?? 0,
                Channels = props?.AudioChannels ?? 0,
                HasCover = tag.Pictures is { Length: > 0 },
                Lyrics = string.IsNullOrWhiteSpace(tag.Lyrics) ? null : tag.Lyrics,
            });
        }
        catch (TagLib.CorruptFileException ex)
        {
            return Fail(filePath, "文件已损坏", ex);
        }
        catch (TagLib.UnsupportedFormatException ex)
        {
            return Fail(filePath, "不支持的音频格式", ex);
        }
        catch (FileNotFoundException ex)
        {
            return Fail(filePath, "文件不存在", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Fail(filePath, "目录不存在", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(filePath, "无访问权限", ex);
        }
        catch (PathTooLongException ex)
        {
            return Fail(filePath, "路径过长", ex);
        }
        catch (IOException ex)
        {
            return Fail(filePath, "读取失败", ex);
        }
        catch (Exception ex)
        {
            return Fail(filePath, $"解析异常：{ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 与 <see cref="Read"/> 用的是同一个 <c>ReadStyle.Average</c>，分开两个方法的原因是
    /// <b>调用时机与返回内容不同</b>：<see cref="Read"/> 在扫描期对每个文件都调一次，
    /// 只回一个 <see cref="MusicTagInfo.HasCover"/> 布尔标志；本方法只在 UI 真的要把封面
    /// 画出来时才按路径调一次，回原始字节。这样曲库 JSON 里永远不会出现 base64 封面。
    /// </remarks>
    public MusicCover? ReadCover(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            using var file = TagLib.File.Create(filePath, TagLib.ReadStyle.Average);

            TagLib.IPicture[] pictures = file.Tag.Pictures ?? [];
            if (pictures.Length == 0)
            {
                return null;
            }

            TagLib.IPicture? chosen = null;
            foreach (TagLib.IPicture picture in pictures)
            {
                if (picture.Type == TagLib.PictureType.FrontCover)
                {
                    chosen = picture;
                    break;
                }

                chosen ??= picture;
            }

            if (chosen is null)
            {
                return null;
            }

            TagLib.ByteVector? vector = chosen.Data;
            byte[]? bytes = vector?.Data;
            if (bytes is not { Length: > 0 })
            {
                return null;
            }

            string mimeType = string.IsNullOrWhiteSpace(chosen.MimeType) ? "image/jpeg" : chosen.MimeType;
            return new MusicCover(bytes, mimeType);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Music] 读取封面失败：{filePath} —— {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>把标签值规整为可直接入库的字符串：null 与纯空白都归一为空串，两端空白裁掉。</summary>
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    /// <summary>记日志并构造失败结果（日志留技术细节，回给 UI 的只留中文标签）。</summary>
    private MusicTagReadResult Fail(string filePath, string reason, Exception ex)
    {
        _logger.Warn($"[Music] 标签读取失败：{filePath} —— {reason}（{ex.GetType().Name}: {ex.Message}）");
        return MusicTagReadResult.Fail(reason);
    }
}
