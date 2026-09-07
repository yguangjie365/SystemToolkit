using System.Reflection;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// <see cref="TagLibMusicTagReader"/> 单测：MP3 / FLAC / 无标签 / 损坏文件四条路径
/// （Design/09 §8 与 MUSIC-2 验收要求），外加封面惰性读取与扩展名清单防漂移守卫。
/// </summary>
/// <remarks>
/// 音频文件由 <see cref="MusicFixtures"/> 在运行时合成（手写 RIFF / fLaC+STREAMINFO / MPEG 帧），
/// 标签则用 TagLibSharp 自己写回去——验证的是「能否读回业界工具写出的标签」，不是自造格式自读。
/// </remarks>
public class TagLibMusicTagReaderTests
{
    private readonly CapturingLogger _logger = new();
    private readonly TagLibMusicTagReader _reader;

    public TagLibMusicTagReaderTests() => _reader = new TagLibMusicTagReader(_logger);

    // ───────────────────────────────────────────────────────────
    // MP3 路径
    // ───────────────────────────────────────────────────────────

    [Fact]
    public void Read_Mp3WithFullTags_ReturnsEveryField()
    {
        using var dir = new TempDir();
        string path = dir.Write("song.mp3", MusicFixtures.BuildMp3Bytes());
        WriteTags(path, tag =>
        {
            tag.Title = "  夜曲  ";
            tag.Performers = ["周杰伦"];
            tag.AlbumArtists = ["周杰伦"];
            tag.Album = "十一月的萧邦";
            tag.Track = 3;
            tag.Year = 2005;
            tag.Genres = ["Pop", "Mandopop"];
            tag.Lyrics = "[00:12.00]第一句\n[00:16.00]第二句";
        });

        MusicTagReadResult result = _reader.Read(path);

        Assert.True(result.Success, _logger.Dump());
        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(result.Tags);
        Assert.Equal("夜曲", tags.Title);           // 两端空白已裁
        Assert.Equal("周杰伦", tags.Artist);
        Assert.Equal("十一月的萧邦", tags.Album);
        Assert.Equal("周杰伦", tags.AlbumArtist);
        Assert.Equal("Pop", tags.Genre);           // 多值取第一个
        Assert.Equal(3U, tags.TrackNumber);
        Assert.Equal(2005U, tags.Year);
        Assert.Contains("第一句", tags.Lyrics);
    }

    [Fact]
    public void Read_Mp3AudioProperties_MatchSynthesizedFrames()
    {
        using var dir = new TempDir();
        string path = dir.Write("cbr.mp3", MusicFixtures.BuildMp3Bytes(frameCount: 38));

        MusicTagReadResult result = _reader.Read(path);

        Assert.True(result.Success, _logger.Dump());
        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(result.Tags);
        Assert.Equal(44100, tags.SampleRateHz);
        Assert.Equal(2, tags.Channels);
        Assert.Equal(128, tags.BitrateKbps);
        // 38 帧 × 1152 采样 ÷ 44100 Hz = 992.6 ms；CBR 时长是「文件大小 ÷ 码率」估算值，故给容差带
        Assert.InRange(tags.DurationMs, 800UL, 1200UL);
    }

    [Fact]
    public void Read_Mp3WithoutArtistTag_ReportsEmptyStringNotFallback()
    {
        using var dir = new TempDir();
        string path = dir.Write("noartist.mp3", MusicFixtures.BuildMp3Bytes());
        WriteTags(path, tag => tag.Title = "只有标题");

        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(_reader.Read(path).Tags);

        // 读取器只如实转述，「未知艺术家」兜底是扫描器的策略（见 LocalMusicScannerTests）
        Assert.Equal(string.Empty, tags.Artist);
        Assert.Equal(string.Empty, tags.Album);
        Assert.Equal(string.Empty, tags.Genre);
    }

    // ───────────────────────────────────────────────────────────
    // FLAC 路径
    // ───────────────────────────────────────────────────────────

    [Fact]
    public void Read_FlacDuration_ComesFromStreamInfoExactly()
    {
        using var dir = new TempDir();
        // totalSamples 88200 ÷ 44100 Hz = 2.000 s
        string path = dir.Write("track.flac", MusicFixtures.BuildFlacBytes(totalSamples: 88200));

        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(_reader.Read(path).Tags);

        Assert.Equal(2000UL, tags.DurationMs);
        Assert.Equal(44100, tags.SampleRateHz);
        Assert.Equal(2, tags.Channels);
    }

    [Fact]
    public void Read_FlacWithVorbisComments_ReturnsTags()
    {
        using var dir = new TempDir();
        string path = dir.Write("tagged.flac", MusicFixtures.BuildFlacBytes());
        WriteTags(path, tag =>
        {
            tag.Title = "Flac 标题";
            tag.Performers = ["Flac 艺术家"];
            tag.Album = "Flac 专辑";
            tag.Genres = ["Lossless"];
            tag.Track = 7;
            tag.Year = 1999;
        });

        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(_reader.Read(path).Tags);

        Assert.Equal("Flac 标题", tags.Title);
        Assert.Equal("Flac 艺术家", tags.Artist);
        Assert.Equal("Flac 专辑", tags.Album);
        Assert.Equal("Lossless", tags.Genre);
        Assert.Equal(7U, tags.TrackNumber);
        Assert.Equal(1999U, tags.Year);
    }

    [Fact]
    public void Read_FlacWithPicture_ReportsHasCoverTrue()
    {
        using var dir = new TempDir();
        string path = dir.Write("cover.flac", MusicFixtures.BuildFlacBytes());
        WriteTags(path, tag => tag.Title = "带封面", pictures: [MakePicture(TagLib.PictureType.FrontCover)]);

        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(_reader.Read(path).Tags);

        Assert.True(tags.HasCover, "PictureLazy 模式下仍应能判定出有封面");
    }

    // ───────────────────────────────────────────────────────────
    // 无标签路径
    // ───────────────────────────────────────────────────────────

    [Fact]
    public void Read_UntaggedWav_SucceedsWithEmptyTagsButRealProperties()
    {
        using var dir = new TempDir();
        string path = dir.Write("plain.wav", MusicFixtures.BuildWavBytes(seconds: 1.0, sampleRate: 22050, channels: 1, bitsPerSample: 16));

        MusicTagReadResult result = _reader.Read(path);

        Assert.True(result.Success, _logger.Dump());
        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(result.Tags);
        Assert.Equal(string.Empty, tags.Title);
        Assert.Equal(string.Empty, tags.Artist);
        Assert.Equal(string.Empty, tags.Album);
        Assert.Equal(string.Empty, tags.AlbumArtist);
        Assert.Equal(string.Empty, tags.Genre);
        Assert.Equal(0U, tags.TrackNumber);
        Assert.Equal(0U, tags.Year);
        Assert.Null(tags.Lyrics);
        Assert.False(tags.HasCover);
        // 无标签不等于无属性：容器里的 fmt 块仍然读得出来
        Assert.Equal(22050, tags.SampleRateHz);
        Assert.Equal(1, tags.Channels);
        Assert.Equal(1000UL, tags.DurationMs);
    }

    [Fact]
    public void Read_WhitespaceOnlyLyrics_NormalizesToNull()
    {
        using var dir = new TempDir();
        string path = dir.Write("blanklyrics.mp3", MusicFixtures.BuildMp3Bytes());
        WriteTags(path, tag => tag.Lyrics = "   \r\n\t ");

        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(_reader.Read(path).Tags);

        // 空白歌词若原样回传，歌词页会显示一片空白而不是「暂无歌词」占位
        Assert.Null(tags.Lyrics);
    }

    // ───────────────────────────────────────────────────────────
    // 损坏 / 失败路径（🔴 必须回传原因，不得静默）
    // ───────────────────────────────────────────────────────────

    [Fact]
    public void Read_ZeroByteFile_FailsAndLogs()
    {
        using var dir = new TempDir();
        string path = dir.Write("empty.flac", []);

        MusicTagReadResult result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.Null(result.Tags);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.True(_logger.HasWarning, _logger.Dump());
    }

    [Fact]
    public void Read_BytesWithoutAnySyncPattern_FailsDeterministically()
    {
        using var dir = new TempDir();
        // 夹具保证一个 0xFF 都没有 → 任何音频格式的同步字都不可能命中，失败是确定结论
        string path = dir.Write("garbage.mp3", MusicFixtures.BuildSyncFreeBytes());

        MusicTagReadResult result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.Null(result.Tags);
        Assert.Equal("文件已损坏", result.FailureReason);
    }

    [Fact]
    public void Read_TruncatedFlac_KeepsTagsButReportsZeroDuration()
    {
        using var dir = new TempDir();
        string path = dir.Write("truncated.flac", MusicFixtures.BuildFlacBytes(totalSamples: 88200, withAudioFrame: false));
        WriteTags(path, tag => tag.Title = "下载被截断的歌");

        MusicTagReadResult result = _reader.Read(path);

        // 元数据完好但音频数据缺失：标签要能读出来，时长如实报 0（UI 显示「—」），不能伪造
        Assert.True(result.Success, _logger.Dump());
        MusicTagInfo tags = Assert.IsType<MusicTagInfo>(result.Tags);
        Assert.Equal("下载被截断的歌", tags.Title);
        Assert.Equal(0UL, tags.DurationMs);
        Assert.Equal(44100, tags.SampleRateHz);
    }

    /// <summary>
    /// 契约承诺 <see cref="IMusicTagReader.Read"/> <b>不抛异常</b>——本用例喂随机内容验证。
    /// </summary>
    /// <remarks>
    /// 固定种子：一旦失败可复现。不断言成功/失败，只断言两条不变量：
    /// ① 任何输入都不抛；② 声称成功时必须带 <see cref="MusicTagInfo"/>（不能返回半截结果）。
    /// </remarks>
    [Fact]
    public void Read_NeverThrows_OverRandomizedContent()
    {
        using var dir = new TempDir();
        Random rng = new(20260907);

        for (int i = 0; i < 25; i++)
        {
            byte[] bytes = new byte[rng.Next(0, 8192)];
            rng.NextBytes(bytes);
            string path = dir.Write($"fuzz{i}.mp3", bytes);

            MusicTagReadResult result = _reader.Read(path);

            Assert.False(result.Success && result.Tags is null,
                $"第 {i} 个随机文件声称成功却没带标签载荷");
            Assert.False(!result.Success && string.IsNullOrWhiteSpace(result.FailureReason),
                $"第 {i} 个随机文件失败了却没给原因");
        }
    }

    [Fact]
    public void Read_TextContentWithMp3Extension_Fails()
    {
        using var dir = new TempDir();
        string path = dir.Write("notaudio.mp3", "这根本不是音频"u8.ToArray());

        MusicTagReadResult result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.Equal("文件已损坏", result.FailureReason);
    }

    [Fact]
    public void Read_MissingFile_FailsWithExplanatoryReason()
    {
        using var dir = new TempDir();
        string path = dir.Resolve("never-existed.mp3");

        MusicTagReadResult result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.Equal("文件不存在", result.FailureReason);
    }

    [Fact]
    public void Read_DirectoryNamedLikeAudioFile_FailsWithoutThrowing()
    {
        using var dir = new TempDir();
        string path = dir.CreateDir("folder.mp3");

        MusicTagReadResult result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public void Read_UnsupportedExtension_FailsWithExplanatoryReason()
    {
        using var dir = new TempDir();
        string path = dir.Write("notes.txt", "hello"u8.ToArray());

        MusicTagReadResult result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.Equal("不支持的音频格式", result.FailureReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Read_NullOrBlankPath_FailsWithoutThrowing(string? path)
    {
        MusicTagReadResult result = _reader.Read(path!);

        Assert.False(result.Success);
        Assert.Equal("文件路径为空", result.FailureReason);
    }

    // ───────────────────────────────────────────────────────────
    // 封面惰性读取
    // ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadCover_ReturnsTheExactBytesThatWereWritten()
    {
        using var dir = new TempDir();
        byte[] png = MusicFixtures.OnePixelPng;
        string path = dir.Write("cover.flac", MusicFixtures.BuildFlacBytes());
        WriteTags(path, tag => tag.Title = "封面测试", pictures: [MakePicture(TagLib.PictureType.FrontCover, png, "image/png")]);

        MusicCover? cover = _reader.ReadCover(path);

        Assert.NotNull(cover);
        Assert.Equal("image/png", cover.MimeType);
        Assert.Equal(png, cover.Data);
    }

    [Fact]
    public void ReadCover_PrefersFrontCoverOverEarlierOtherPictures()
    {
        using var dir = new TempDir();
        byte[] front = MusicFixtures.OnePixelPng;
        byte[] back = new byte[front.Length + 8];
        Array.Copy(front, back, front.Length);

        string path = dir.Write("multi.flac", MusicFixtures.BuildFlacBytes());
        WriteTags(path, tag => tag.Title = "多图", pictures:
        [
            MakePicture(TagLib.PictureType.BackCover, back, "image/png"),
            MakePicture(TagLib.PictureType.FrontCover, front, "image/png"),
        ]);

        MusicCover? cover = _reader.ReadCover(path);

        Assert.NotNull(cover);
        Assert.Equal(front, cover.Data);
    }

    [Fact]
    public void ReadCover_FallsBackToFirstPictureWhenNoFrontCoverExists()
    {
        using var dir = new TempDir();
        byte[] other = MusicFixtures.OnePixelPng;
        string path = dir.Write("other.flac", MusicFixtures.BuildFlacBytes());
        WriteTags(path, tag => tag.Title = "只有其它图", pictures: [MakePicture(TagLib.PictureType.Other, other, "image/png")]);

        MusicCover? cover = _reader.ReadCover(path);

        Assert.NotNull(cover);
        Assert.Equal(other, cover.Data);
    }

    [Fact]
    public void ReadCover_UntaggedFile_ReturnsNull()
    {
        using var dir = new TempDir();
        string path = dir.Write("plain.wav", MusicFixtures.BuildWavBytes());

        Assert.Null(_reader.ReadCover(path));
    }

    [Fact]
    public void ReadCover_MissingFile_ReturnsNullAndLogs()
    {
        using var dir = new TempDir();

        Assert.Null(_reader.ReadCover(dir.Resolve("gone.flac")));
        Assert.True(_logger.HasWarning, _logger.Dump());
    }

    [Fact]
    public void ReadCover_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        using var dir = new TempDir();
        string path = dir.Write("broken.flac", "fLaC"u8.ToArray());

        Assert.Null(_reader.ReadCover(path));
    }

    // ───────────────────────────────────────────────────────────
    // 扩展名清单
    // ───────────────────────────────────────────────────────────

    [Fact]
    public void SupportedExtensions_KeepsFullParityWithOldProjectPlusAif()
    {
        string[] expected =
        [
            ".mp3", ".flac", ".ogg", ".m4a", ".wav", ".aiff", ".aif", ".aac", ".opus", ".wma", ".ape", ".oga",
        ];

        Assert.Equal(expected.Length, _reader.SupportedExtensions.Count);
        foreach (string ext in expected)
        {
            Assert.Contains(ext, _reader.SupportedExtensions);
        }
    }

    [Fact]
    public void SupportedExtensions_MatchesCaseInsensitively()
    {
        Assert.Contains(".MP3", _reader.SupportedExtensions);
        Assert.Contains(".Flac", _reader.SupportedExtensions);
    }

    /// <summary>
    /// 防漂移守卫：清单里每个扩展名都必须真的被 TagLibSharp 注册过。
    /// </summary>
    /// <remarks>
    /// 升级 TagLibSharp 或有人手改清单时，若某个扩展名不再被支持，
    /// 后果是「能扫进曲库、点开就报损坏」——比不列出来更难排查。这条守卫在构建期拦住它。
    /// </remarks>
    [Fact]
    public void SupportedExtensions_EveryEntryIsRegisteredByTagLibSharp()
    {
        Assembly taglib = typeof(TagLib.File).Assembly;
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Type type in taglib.GetTypes())
        {
            foreach (TagLib.SupportedMimeType attr in type.GetCustomAttributes<TagLib.SupportedMimeType>(inherit: true))
            {
                if (!string.IsNullOrEmpty(attr.Extension))
                {
                    registered.Add("." + attr.Extension);
                }
            }
        }

        Assert.NotEmpty(registered); // 自检：反射路径确实取到了东西，否则本守卫形同虚设

        var unregistered = _reader.SupportedExtensions.Where(e => !registered.Contains(e)).OrderBy(e => e, StringComparer.Ordinal).ToList();
        Assert.True(unregistered.Count == 0,
            "以下扩展名未被当前 TagLibSharp 版本注册（升级库后清单需同步）：" + string.Join(", ", unregistered));
    }

    // ───────────────────────────────────────────────────────────
    // 测试设施
    // ───────────────────────────────────────────────────────────

    /// <summary>用 TagLibSharp 自己把标签与图片写进已合成的容器（与真实世界写标签的工具同一条代码路径）。</summary>
    private static void WriteTags(string path, Action<TagLib.Tag> configure, TagLib.IPicture[]? pictures = null)
    {
        using var file = TagLib.File.Create(path);
        configure(file.Tag);
        if (pictures is not null)
        {
            file.Tag.Pictures = pictures;
        }

        file.Save();
    }

    /// <summary>造一张内嵌图片（默认用夹具里的 1×1 PNG）。</summary>
    private static TagLib.IPicture MakePicture(TagLib.PictureType type, byte[]? bytes = null, string mimeType = "image/png")
    {
        return new TagLib.Picture(new TagLib.ByteVector(bytes ?? MusicFixtures.OnePixelPng))
        {
            Type = type,
            MimeType = mimeType,
            Description = type.ToString(),
        };
    }
}
