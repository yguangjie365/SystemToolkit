using System.Text.Json;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 音乐模型层的纯逻辑回归（批次1）：时长格式化、扫描进度百分比、扫描结果/曲库读取结果的
/// 状态判定，以及曲库 JSON 往返。
/// <para>
/// JSON 往返那组是<b>批次5 的前置保障</b>：Q-008 裁定曲库落成单个 JSON 文件，
/// 而 <see cref="MusicSong"/> / <see cref="MusicLibrary"/> 全是 <c>init</c>-only 属性，
/// 万一 System.Text.Json 在某个类型上（<c>ulong</c> 时长、<c>DateTime</c> UTC、
/// 可空 <c>DateTimeOffset</c>）往返丢字段，整份曲库缓存就会静默残缺。
/// </para>
/// </summary>
public class MusicModelTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ───────────────────────── 时长格式化 ─────────────────────────

    [Theory]
    [InlineData(0UL, "—")]
    [InlineData(1000UL, "0:01")]
    [InlineData(59_999UL, "0:59")]
    [InlineData(60_000UL, "1:00")]
    [InlineData(225_000UL, "3:45")]
    [InlineData(599_000UL, "9:59")]
    [InlineData(3_600_000UL, "1:00:00")]
    [InlineData(3_723_000UL, "1:02:03")]
    [InlineData(35_999_000UL, "9:59:59")]
    public void DurationText_FormatsAsMinutesSeconds_BelowOneHour(ulong durationMs, string expected)
    {
        Assert.Equal(expected, new MusicSong { DurationMs = durationMs }.DurationText);
    }

    [Fact]
    public void DurationText_PadsSecondsToTwoDigits_SoListColumnsDoNotJitter()
    {
        // 秒必须补零：列表里 "3:5" 与 "3:45" 混排会让时长列宽度抖动
        Assert.Equal("3:05", new MusicSong { DurationMs = 185_000 }.DurationText);
        Assert.Equal("1:02:03", new MusicSong { DurationMs = 3_723_000 }.DurationText);
    }

    // ───────────────────────── 扫描进度 ─────────────────────────

    [Fact]
    public void ScanProgress_WithZeroTotal_ReportsZeroInsteadOfNaN()
    {
        // 空目录扫描时 Total=0，Scanned/Total 会得 NaN；NaN 传进进度条 Value 会抛
        var progress = new MusicScanProgress(0, 0, string.Empty);

        Assert.Equal(0d, progress.Ratio);
        Assert.Equal(0, progress.Percent);
        Assert.False(double.IsNaN(progress.Ratio));
    }

    [Fact]
    public void ScanProgress_ComputesRatioAndPercent()
    {
        Assert.Equal(0.5d, new MusicScanProgress(250, 500, "a.mp3").Ratio, precision: 6);
        Assert.Equal(50, new MusicScanProgress(250, 500, "a.mp3").Percent);
        Assert.Equal(100, new MusicScanProgress(500, 500, "a.mp3").Percent);
    }

    [Fact]
    public void ScanProgress_ClampsRatioToOne_WhenScannedExceedsTotal()
    {
        // 并行扫描下 Interlocked 递增与 Total 快照存在竞态，Scanned 可能瞬时越界
        Assert.Equal(1d, new MusicScanProgress(600, 500, "a.mp3").Ratio, precision: 6);
        Assert.Equal(100, new MusicScanProgress(600, 500, "a.mp3").Percent);
    }

    // ───────────────────────── 扫描结果状态判定 ─────────────────────────

    [Fact]
    public void ScanResult_IsClean_OnlyWhenNoFailuresNoInaccessiblePathsAndNotCancelled()
    {
        Assert.True(MusicScanResult.Empty().IsClean);

        Assert.False(new MusicScanResult([], [new MusicScanFailure("C:\\a.mp3", "文件已损坏")], []).IsClean);
        Assert.False(new MusicScanResult([], [], ["C:\\Music"]).IsClean);
        Assert.False(new MusicScanResult([], [], []) { WasCancelled = true }.IsClean);
    }

    [Fact]
    public void ScanResult_ProcessedCount_SumsSongsAndFailures()
    {
        // 失败文件同样算「处理过」：进度条必须把它计入，否则扫完停在 98% 不动
        var result = new MusicScanResult(
            [new MusicSong { Id = "local:1" }],
            [new MusicScanFailure("C:\\a.mp3", "文件已损坏"), new MusicScanFailure("C:\\b.mp3", "不支持的音频格式")],
            []);

        Assert.Equal(3, result.ProcessedCount);
    }

    // ───────────────────────── 曲库读取结果 ─────────────────────────

    [Fact]
    public void LibraryLoadResult_IsDegraded_TracksWarningPresence()
    {
        Assert.False(new MusicLibraryLoadResult(new MusicLibrary(), null).IsDegraded);
        Assert.True(new MusicLibraryLoadResult(new MusicLibrary(), "曲库缓存文件损坏，已重置").IsDegraded);
    }

    [Fact]
    public void Library_SongCount_MirrorsSongsLength()
    {
        var library = new MusicLibrary
        {
            Songs = [new MusicSong { Id = "local:1" }, new MusicSong { Id = "local:2" }],
        };

        Assert.Equal(2, library.SongCount);
    }

    // ───────────────────────── 歌词文档三态 ─────────────────────────

    [Fact]
    public void LyricDocument_DistinguishesTimedPlainAndEmpty()
    {
        // 三态必须可区分：内嵌 USLT 常是无时间标签的纯文本，
        // 若与「真的没歌词」混淆，UI 会对有歌词的歌显示「暂无歌词」
        var empty = LyricDocument.None();
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsTimed);
        Assert.Equal(LyricSource.None, empty.Source);

        var plain = new LyricDocument { Source = LyricSource.Embedded, PlainText = "第一行\n第二行" };
        Assert.False(plain.IsEmpty);
        Assert.False(plain.IsTimed);

        var timed = new LyricDocument
        {
            Source = LyricSource.SidecarFile,
            Lines = [new LyricLine { Time = 12.5, Duration = 4.8, Text = "第一行" }],
        };
        Assert.False(timed.IsEmpty);
        Assert.True(timed.IsTimed);
    }

    [Fact]
    public void LyricDocument_WithWhitespaceOnlyPlainText_IsStillEmpty()
    {
        Assert.True(new LyricDocument { PlainText = "   \r\n  " }.IsEmpty);
    }

    // ───────────────────────── 曲库 JSON 往返（批次5 前置保障） ─────────────────────────

    [Fact]
    public void Library_JsonRoundTrip_PreservesEveryField()
    {
        var original = new MusicLibrary
        {
            ScanRoots = ["D:\\Music", "E:\\无损"],
            LastScanAtUtc = new DateTimeOffset(2026, 9, 7, 14, 20, 31, TimeSpan.Zero),
            Songs = [MakeSong()],
        };

        string json = JsonSerializer.Serialize(original, JsonOpts);
        MusicLibrary? restored = JsonSerializer.Deserialize<MusicLibrary>(json, JsonOpts);

        Assert.NotNull(restored);
        Assert.Equal(original.ScanRoots, restored!.ScanRoots);
        Assert.Equal(original.LastScanAtUtc, restored.LastScanAtUtc);
        Assert.Single(restored.Songs);
        Assert.Equal(original.Songs[0], restored.Songs[0]); // record 值等价：逐字段全覆盖
    }

    [Fact]
    public void Song_JsonRoundTrip_KeepsWideAndNullableNumericTypes()
    {
        // ulong 时长 / long 文件大小 / uint 音轨号与年份 是 STJ 容易出问题的边界，逐个钉死
        MusicSong song = MakeSong();

        MusicSong? restored = JsonSerializer.Deserialize<MusicSong>(JsonSerializer.Serialize(song, JsonOpts), JsonOpts);

        Assert.NotNull(restored);
        Assert.Equal(3_723_000UL, restored!.DurationMs);
        Assert.Equal(48_123_456L, restored.FileSizeBytes);
        Assert.Equal(7U, restored.TrackNumber);
        Assert.Equal(2011U, restored.Year);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc), restored.ModifiedTimeUtc);
    }

    [Fact]
    public void Song_JsonRoundTrip_OfDefaultInstance_YieldsDefaultInstance()
    {
        // 空曲目（反序列化缺字段时的形态）必须能原样往返，不得抛
        var song = new MusicSong();

        MusicSong? restored = JsonSerializer.Deserialize<MusicSong>(JsonSerializer.Serialize(song, JsonOpts), JsonOpts);

        Assert.Equal(song, restored);
        Assert.Equal("—", restored!.DurationText);
    }

    [Fact]
    public void Library_JsonRoundTrip_OfEmptyDocument_KeepsLastScanNull()
    {
        // 首次运行落一份空曲库，重启后 LastScanAtUtc 必须仍是 null（页头据此显示「从未扫描」）
        MusicLibrary? restored = JsonSerializer.Deserialize<MusicLibrary>(
            JsonSerializer.Serialize(new MusicLibrary(), JsonOpts), JsonOpts);

        Assert.NotNull(restored);
        Assert.Null(restored!.LastScanAtUtc);
        Assert.Empty(restored.Songs);
        Assert.Empty(restored.ScanRoots);
    }

    /// <summary>造一首字段全非默认的曲目（record 值等价断言才有意义）。</summary>
    private static MusicSong MakeSong() => new()
    {
        Id = "local:0A1B2C3D4E5F6071",
        LocalPath = "D:\\Music\\Album\\01 - Track.flac",
        Name = "Track",
        Artist = "Artist",
        Album = "Album",
        AlbumArtist = "Various Artists",
        Genre = "Electronic",
        TrackNumber = 7,
        Year = 2011,
        DurationMs = 3_723_000,
        BitrateKbps = 1411,
        SampleRateHz = 44100,
        Channels = 2,
        FileSizeBytes = 48_123_456,
        ModifiedTimeUtc = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc),
        HasEmbeddedCover = true,
        HasEmbeddedLyrics = false,
    };
}
