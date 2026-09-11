using System.Security.Cryptography;
using System.Text;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// <see cref="LocalMusicScanner"/> 单测：枚举、去重、并发、进度、取消、失败归集与兜底映射。
/// </summary>
/// <remarks>
/// <para>全部用例走 <see cref="FakeTagReader"/> 而非真读取器——扫描器的职责与「TagLib 怎么解析容器」
/// 无关，用假件才能精确构造「第 3 个文件损坏」这类组合；真实容器解析由
/// <c>TagLibMusicTagReaderTests</c> 覆盖。</para>
/// <para>🔴 <b>两处已知覆盖缺口</b>（写进变更记录，不用假测试掩盖）：
/// ① <b>子目录级</b>无权限 → <see cref="MusicScanResult.InaccessiblePaths"/>，需要改 ACL 或管理员权限
/// 才能可移植地触发，这里只覆盖<b>根级</b>不可达（不存在 / 是文件 / 路径非法）；
/// ② ReparsePoint（junction / 符号链接）跳过，创建链接需要开发者模式或管理员权限，
/// 无法在 CI 上稳定成立。两者列入手工验证清单。</para>
/// </remarks>
public sealed class LocalMusicScannerTests
{
    /// <summary>占位文件内容（假读取器不解析，只需文件真实存在以便取大小与修改时间）。</summary>
    private static readonly byte[] StubBytes = [1, 2, 3, 4];

    private readonly CapturingLogger _logger = new();
    private readonly FakeTagReader _tagReader = new();
    private readonly LocalMusicScanner _scanner;

    /// <summary>每个用例一套全新假件与扫描器（xunit 每用例新建测试类实例，字段互不串味）。</summary>
    public LocalMusicScannerTests() => _scanner = new LocalMusicScanner(_logger, _tagReader);

    // ───────────────────────── 早退与根目录处理 ─────────────────────────

    [Fact]
    public async Task ScanAsync_EmptyRootList_ReturnsEmptyResultWithoutTouchingDisk()
    {
        MusicScanResult result = await _scanner.ScanAsync([]);

        Assert.Empty(result.Songs);
        Assert.Empty(result.Failures);
        Assert.Empty(result.InaccessiblePaths);
        Assert.True(result.IsClean);
        Assert.False(result.WasCancelled);
        Assert.Equal(0, _tagReader.ReadCount);
    }

    [Fact]
    public async Task ScanAsync_NullRootList_ReturnsEmptyResultWithoutThrowing()
    {
        MusicScanResult result = await _scanner.ScanAsync(null!);

        Assert.True(result.IsClean);
        Assert.Equal(0, _tagReader.ReadCount);
    }

    [Fact]
    public async Task ScanAsync_NonexistentRoot_IsReportedAsInaccessibleAndLogged()
    {
        using var dir = new TempDir();
        string missing = dir.Resolve("does-not-exist");

        MusicScanResult result = await _scanner.ScanAsync([missing]);

        Assert.Contains(missing, result.InaccessiblePaths);
        Assert.Empty(result.Songs);
        Assert.False(result.IsClean);
        Assert.True(_logger.HasWarning, _logger.Dump());
    }

    [Fact]
    public async Task ScanAsync_RootIsAFile_IsReportedAsInaccessible()
    {
        using var dir = new TempDir();
        string filePath = dir.Write("not-a-directory.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([filePath]);

        Assert.Contains(filePath, result.InaccessiblePaths);
        Assert.Empty(result.Songs);
        Assert.Equal(0, _tagReader.ReadCount);
    }

    [Fact]
    public async Task ScanAsync_RootWithNullCharacter_IsReportedAsInaccessibleInsteadOfThrowing()
    {
        MusicScanResult result = await _scanner.ScanAsync(["C:\\invalid\0path"]);

        Assert.Single(result.InaccessiblePaths);
        Assert.Empty(result.Songs);
        Assert.True(_logger.HasWarning, _logger.Dump());
    }

    [Fact]
    public async Task ScanAsync_BlankRoots_AreSkippedWithoutPollutingInaccessibleList()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync(["", "   ", dir.Path]);

        Assert.Single(result.Songs);
        Assert.Empty(result.InaccessiblePaths);
        Assert.True(result.IsClean);
    }

    [Fact]
    public async Task ScanAsync_EmptyDirectory_ReturnsCleanEmptyResult()
    {
        using var dir = new TempDir();

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Empty(result.Songs);
        Assert.Empty(result.Failures);
        Assert.Empty(result.InaccessiblePaths);
        Assert.True(result.IsClean);
        Assert.Equal(0, _tagReader.ReadCount);
    }

    /// <summary>
    /// 🔴 旧实现的核心缺陷回归：单个不可达根不得牵连其它根。
    /// </summary>
    /// <remarks>
    /// 旧工程用 <c>Directory.EnumerateFiles(root, "*.*", AllDirectories)</c>，
    /// 撞上第一个 <see cref="UnauthorizedAccessException"/> 就抛出并 <c>catch { return []; }</c>，
    /// 用户看到的是「扫描完成，0 首」且没有任何解释。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_OneRootUnreachable_OtherRootsAreStillScannedInFull()
    {
        using var dir = new TempDir();
        dir.Write("ok/song1.mp3", StubBytes);
        dir.Write("ok/song2.flac", StubBytes);
        string missingA = dir.Resolve("gone-a");
        string missingB = dir.Resolve("gone-b");
        string good = dir.Resolve("ok");

        MusicScanResult result = await _scanner.ScanAsync([missingA, good, missingB]);

        Assert.Equal(2, result.Songs.Count);
        Assert.Equal(2, result.InaccessiblePaths.Count);
        Assert.Contains(missingA, result.InaccessiblePaths);
        Assert.Contains(missingB, result.InaccessiblePaths);
        Assert.False(result.IsClean);
    }

    // ───────────────────────── 枚举 ─────────────────────────

    [Fact]
    public async Task ScanAsync_FindsAudioFilesRecursivelyThroughNestedDirectories()
    {
        using var dir = new TempDir();
        dir.Write("top.mp3", StubBytes);
        dir.Write("level1/a.mp3", StubBytes);
        dir.Write("level1/level2/b.flac", StubBytes);
        dir.Write("level1/level2/level3/c.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Equal(4, result.Songs.Count);
        Assert.Equal(4, _tagReader.ReadCount);
        Assert.True(result.IsClean);
    }

    [Fact]
    public async Task ScanAsync_IgnoresUnsupportedExtensionsBeforeReachingTheReader()
    {
        using var dir = new TempDir();
        dir.Write("song.mp3", StubBytes);
        dir.Write("cover.jpg", StubBytes);
        dir.Write("notes.txt", StubBytes);
        dir.Write("lyrics.lrc", StubBytes);
        dir.Write("track.flac", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        List<string> names = [.. result.Songs.Select(s => Path.GetFileName(s.LocalPath))];
        Assert.Equal(2, names.Count);
        Assert.Contains("song.mp3", names);
        Assert.Contains("track.flac", names);

        // 过滤发生在枚举阶段：不支持的文件根本不该送到读取器（否则万级曲库会白跑几万次解析）
        Assert.Equal(2, _tagReader.ReadCount);
    }

    [Fact]
    public async Task ScanAsync_MatchesExtensionsCaseInsensitively()
    {
        using var dir = new TempDir();
        dir.Write("A.MP3", StubBytes);
        dir.Write("B.Flac", StubBytes);
        dir.Write("C.FLAC", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Equal(3, result.Songs.Count);
    }

    [Fact]
    public async Task ScanAsync_IgnoresFileWithoutExtension()
    {
        using var dir = new TempDir();
        dir.Write("README", StubBytes);
        dir.Write("song.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Single(result.Songs);
    }

    [Fact]
    public async Task ScanAsync_OnlyUsesExtensionsDeclaredByTheInjectedReader()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);
        dir.Write("b.m4a", StubBytes);
        dir.Write("c.wav", StubBytes);
        var scanner = new LocalMusicScanner(_logger, new FakeTagReader(".m4a", ".wav"));

        MusicScanResult result = await scanner.ScanAsync([dir.Path]);

        List<string> names = [.. result.Songs.Select(s => Path.GetFileName(s.LocalPath))];
        Assert.Equal(2, names.Count);
        Assert.DoesNotContain("a.mp3", names);
    }

    // ───────────────────────── 跨根去重 ─────────────────────────

    [Fact]
    public async Task ScanAsync_SameRootListedTwice_IsEnumeratedOnlyOnce()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);
        dir.Write("b.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path, dir.Path]);

        Assert.Equal(2, result.Songs.Count);
        Assert.Equal(2, result.Songs.Select(s => s.Id).Distinct().Count());
        Assert.Equal(2, _tagReader.ReadCount);
    }

    [Fact]
    public async Task ScanAsync_NestedRoots_DoNotDuplicateTheSharedSubtree()
    {
        using var dir = new TempDir();
        dir.Write("root.mp3", StubBytes);
        dir.Write("rock/a.mp3", StubBytes);
        dir.Write("rock/deep/b.mp3", StubBytes);
        string rock = Path.Combine(dir.Path, "rock");
        string deep = Path.Combine(rock, "deep");

        MusicScanResult result = await _scanner.ScanAsync([dir.Path, rock, deep]);

        Assert.Equal(3, result.Songs.Count);
        Assert.Equal(3, result.Songs.Select(s => s.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, _tagReader.ReadCount);
    }

    /// <remarks>
    /// 目录选择框回填的路径常带结尾分隔符，<see cref="Path.GetFullPath(string)"/> 不会去掉它，
    /// 于是「D:\Music」与「D:\Music\」会被当成两个根、整棵子树枚举两遍。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_RootWithTrailingSeparator_IsNotTreatedAsASecondRoot()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync(
            [dir.Path, dir.Path + Path.DirectorySeparatorChar]);

        Assert.Single(result.Songs);
        Assert.Equal(1, _tagReader.ReadCount);
    }

    [Fact]
    public async Task ScanAsync_RootsDifferingOnlyInCasing_AreDeduplicatedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path, dir.Path.ToUpperInvariant()]);

        Assert.Single(result.Songs);
    }

    // ───────────────────────── 🔴 失败归集（禁止静默失败） ─────────────────────────

    [Fact]
    public async Task ScanAsync_CorruptFile_IsReportedInFailuresWhileSiblingsStillSucceed()
    {
        using var dir = new TempDir();
        dir.Write("good1.mp3", StubBytes);
        dir.Write("broken.mp3", StubBytes);
        dir.Write("sub/good2.flac", StubBytes);
        _tagReader.FailWhenPathContains = "broken";
        _tagReader.FailureReason = "文件已损坏";

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Equal(2, result.Songs.Count);
        Assert.DoesNotContain(result.Songs, s => s.LocalPath.Contains("broken", StringComparison.OrdinalIgnoreCase));

        MusicScanFailure failure = Assert.Single(result.Failures);
        Assert.Equal("文件已损坏", failure.Reason);
        Assert.EndsWith("broken.mp3", failure.FilePath, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, result.ProcessedCount);
        Assert.False(result.IsClean);
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public async Task ScanAsync_EveryFileIsAccountedFor_AsEitherSongOrFailure()
    {
        using var dir = new TempDir();
        const int directories = 25;
        const int perDirectory = 20;
        for (int d = 0; d < directories; d++)
        {
            WriteFiles(dir, perDirectory, $"disc{d:D2}");
        }

        // 每个目录里恰好一个损坏文件（500 个文件里 25 个失败）
        _tagReader.FailWhenPathContains = "track0013";

        var progress = new RecordingProgress();
        MusicScanResult result = await _scanner.ScanAsync([dir.Path], progress);

        Assert.Equal(directories * perDirectory, result.ProcessedCount);
        Assert.Equal(directories * (perDirectory - 1), result.Songs.Count);
        Assert.Equal(directories, result.Failures.Count);
        Assert.False(result.IsClean);
    }

    [Fact]
    public async Task ScanAsync_AllFilesCorrupt_ReturnsEmptySongListWithFullFailureList()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);
        dir.Write("b.mp3", StubBytes);
        _tagReader.FailWhenPathContains = ".mp3";

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Empty(result.Songs);
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.False(string.IsNullOrWhiteSpace(f.Reason)));
        Assert.All(result.Failures, f => Assert.False(string.IsNullOrWhiteSpace(f.FilePath)));
    }

    [Fact]
    public async Task ScanAsync_FailureReasonFromReader_IsPreservedVerbatim()
    {
        using var dir = new TempDir();
        dir.Write("x.m4a", StubBytes);
        var reader = new FakeTagReader(".m4a")
        {
            FailWhenPathContains = "x.m4a",
            FailureReason = "不支持的音频格式",
        };

        MusicScanResult result = await new LocalMusicScanner(_logger, reader).ScanAsync([dir.Path]);

        Assert.Equal("不支持的音频格式", Assert.Single(result.Failures).Reason);
    }

    // ───────────────────────── 兜底与字段映射 ─────────────────────────

    [Fact]
    public async Task ScanAsync_UntaggedFile_FallsBackToFilenameAndUnknownArtist()
    {
        using var dir = new TempDir();
        dir.Write("周杰伦 - 夜曲.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        MusicSong song = Assert.Single(result.Songs);
        Assert.Equal("周杰伦 - 夜曲", song.Name);
        Assert.Equal("未知艺术家", song.Artist);
        Assert.Equal(string.Empty, song.Album);
        Assert.Equal(string.Empty, song.AlbumArtist);
        Assert.Equal(string.Empty, song.Genre);
        Assert.Equal(0U, song.TrackNumber);
        Assert.Equal(0U, song.Year);
        Assert.Equal(0UL, song.DurationMs);
        Assert.Equal("—", song.DurationText);
        Assert.False(song.HasEmbeddedCover);
        Assert.False(song.HasEmbeddedLyrics);
    }

    [Fact]
    public async Task ScanAsync_WhitespaceOnlyTitleAndArtist_StillFallBack()
    {
        using var dir = new TempDir();
        dir.Write("real-name.mp3", StubBytes);
        _tagReader.TagFactory = _ => new MusicTagInfo { Title = "   ", Artist = "\t" };

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        MusicSong song = Assert.Single(result.Songs);
        Assert.Equal("real-name", song.Name);
        Assert.Equal("未知艺术家", song.Artist);
    }

    [Fact]
    public async Task ScanAsync_PresentTags_AreMappedOntoTheSongWithoutAlteration()
    {
        using var dir = new TempDir();
        dir.Write("filename-is-ignored.mp3", StubBytes);
        _tagReader.TagFactory = _ => new MusicTagInfo
        {
            Title = "夜曲",
            Artist = "周杰伦",
            Album = "十一月的萧邦",
            AlbumArtist = "周杰伦",
            Genre = "Pop",
            TrackNumber = 3U,
            Year = 2005U,
            DurationMs = 225_000UL,
            BitrateKbps = 320,
            SampleRateHz = 44100,
            Channels = 2,
            HasCover = true,
            Lyrics = "[00:12.34]第一句",
        };

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        MusicSong song = Assert.Single(result.Songs);
        Assert.Equal("夜曲", song.Name);
        Assert.Equal("周杰伦", song.Artist);
        Assert.Equal("十一月的萧邦", song.Album);
        Assert.Equal("周杰伦", song.AlbumArtist);
        Assert.Equal("Pop", song.Genre);
        Assert.Equal(3U, song.TrackNumber);
        Assert.Equal(2005U, song.Year);
        Assert.Equal(225_000UL, song.DurationMs);
        Assert.Equal(320, song.BitrateKbps);
        Assert.Equal(44100, song.SampleRateHz);
        Assert.Equal(2, song.Channels);
        Assert.Equal("3:45", song.DurationText);
        Assert.True(song.HasEmbeddedCover);
        Assert.True(song.HasEmbeddedLyrics);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ScanAsync_BlankEmbeddedLyrics_ReportsNoEmbeddedLyrics(string? lyrics)
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);
        _tagReader.TagFactory = _ => new MusicTagInfo { Lyrics = lyrics };

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.False(Assert.Single(result.Songs).HasEmbeddedLyrics);
    }

    /// <remarks>
    /// <see cref="MusicSong.ModifiedTimeUtc"/> 是批次 5 增量扫描的判定键（相等即跳过重读），
    /// 所以这里断言<b>精确相等</b>而不是「差不多」——文件系统时间戳必须无损往返。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_PopulatesFileSizeAndModifiedTimeFromTheFileSystem()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.mp3", MusicFixtures.BuildSyncFreeBytes(4096));
        var modified = new DateTime(2024, 3, 15, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        MusicSong song = Assert.Single(result.Songs);
        Assert.Equal(4096L, song.FileSizeBytes);
        Assert.Equal(modified, song.ModifiedTimeUtc);
        Assert.Equal(DateTimeKind.Utc, song.ModifiedTimeUtc.Kind);
    }

    // ───────────────────────── 曲目 ID ─────────────────────────

    [Fact]
    public async Task ScanAsync_SongIdHasTheDocumentedShape()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Matches("^local:[0-9a-f]{16}$", Assert.Single(result.Songs).Id);
    }

    /// <remarks>
    /// 锁死的是<b>算法</b>而不只是形状：批次 5 会把 Id 落进 <c>music-library.json</c>，
    /// 谁把 SHA-256 换成别的（比如「优化」成 GetHashCode 或加进文件大小），
    /// 存量曲库的 Id 就全部对不上、播放历史与队列静默断链。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_SongIdIsTheSha256PrefixOfTheEnumeratedPath()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        MusicSong song = Assert.Single(result.Songs);
        Assert.Equal(path, song.LocalPath);

        string normalized = OperatingSystem.IsWindows() ? song.LocalPath.ToUpperInvariant() : song.LocalPath;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        Assert.Equal("local:" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(), song.Id);
    }

    /// <remarks>
    /// 🔴 旧实现用 <c>path.GetHashCode(...)</c>，.NET Core 起字符串哈希<b>每进程随机化</b>，
    /// 重启后同一首歌 ID 就变了 → 曲库缓存永远命中不了。本用例锁死「跨实例稳定」。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_SongIdIsStableAcrossScannerInstances()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);

        MusicScanResult first = await new LocalMusicScanner(_logger, new FakeTagReader()).ScanAsync([dir.Path]);
        MusicScanResult second = await new LocalMusicScanner(_logger, new FakeTagReader()).ScanAsync([dir.Path]);

        Assert.Equal(Assert.Single(first.Songs).Id, Assert.Single(second.Songs).Id);
    }

    /// <remarks>
    /// 🔴 旧实现把文件大小拼进 ID（<c>:12345</c>），重新压制同一首歌（改标签、转码率）就会换 ID，
    /// 播放历史与队列断链。ID 只该由<b>位置</b>决定。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_SongIdDoesNotChangeWhenFileContentChanges()
    {
        using var dir = new TempDir();
        string path = dir.Write("a.mp3", StubBytes);
        MusicScanResult before = await _scanner.ScanAsync([dir.Path]);

        File.WriteAllBytes(path, MusicFixtures.BuildSyncFreeBytes(8192));
        MusicScanResult after = await _scanner.ScanAsync([dir.Path]);

        Assert.Equal(Assert.Single(before.Songs).Id, Assert.Single(after.Songs).Id);
        Assert.Equal(4L, Assert.Single(before.Songs).FileSizeBytes);
        Assert.Equal(8192L, Assert.Single(after.Songs).FileSizeBytes);
    }

    [Fact]
    public async Task ScanAsync_DifferentPathsProduceDifferentIds()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);
        dir.Write("b.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.Equal(2, result.Songs.Count);
        Assert.NotEqual(result.Songs[0].Id, result.Songs[1].Id);
    }

    [Fact]
    public async Task ScanAsync_SongIdIgnoresPathCasingOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);

        MusicScanResult mixedCase = await _scanner.ScanAsync([dir.Path]);
        MusicScanResult upperCase = await _scanner.ScanAsync([dir.Path.ToUpperInvariant()]);

        Assert.Equal(Assert.Single(mixedCase.Songs).Id, Assert.Single(upperCase.Songs).Id);
    }

    // ───────────────────────── 排序与进度 ─────────────────────────

    [Fact]
    public async Task ScanAsync_SongsAreSortedByPathSoRepeatedScansAreReproducible()
    {
        using var dir = new TempDir();
        dir.Write("zulu.mp3", StubBytes);
        dir.Write("alpha.mp3", StubBytes);
        dir.Write("mike.mp3", StubBytes);
        dir.Write("sub/bravo.mp3", StubBytes);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        // ConcurrentBag 的产出顺序不确定，故必须显式排序才能保证两次扫描结果可比对
        List<string> names = [.. result.Songs.Select(s => Path.GetFileName(s.LocalPath))];
        string[] expected = ["alpha.mp3", "mike.mp3", "bravo.mp3", "zulu.mp3"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public async Task ScanAsync_ProgressIsReportedAtTheStrideAndAlwaysReachesTotal()
    {
        using var dir = new TempDir();
        const int total = 60;
        WriteFiles(dir, total);
        var progress = new RecordingProgress();

        MusicScanResult result = await _scanner.ScanAsync([dir.Path], progress);

        Assert.Equal(total, result.Songs.Count);

        IReadOnlyList<MusicScanProgress> snapshots = progress.Snapshots;
        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, s => Assert.Equal(total, s.Total));

        // 并发上报，故断言「上报过哪些值」而不是「第 n 次上报了什么」
        List<int> reported = [.. snapshots.Select(s => s.Scanned).OrderBy(i => i)];
        int[] expectedReported = [25, 50, 60];
        Assert.Equal(expectedReported, reported.ToArray());
        Assert.Contains(snapshots, s => s.Scanned == total && s.Percent == 100 && s.Ratio == 1d);
    }

    [Fact]
    public async Task ScanAsync_FewerFilesThanStride_ReportsOnlyTheCompletionSnapshot()
    {
        using var dir = new TempDir();
        WriteFiles(dir, 7);
        var progress = new RecordingProgress();

        await _scanner.ScanAsync([dir.Path], progress);

        MusicScanProgress only = Assert.Single(progress.Snapshots);
        Assert.Equal(7, only.Scanned);
        Assert.Equal(7, only.Total);
        Assert.Equal(100, only.Percent);
    }

    [Fact]
    public async Task ScanAsync_ProgressCurrentFileIsAFilenameNotAFullPath()
    {
        using var dir = new TempDir();
        dir.Write("sub/夜曲.mp3", StubBytes);
        var progress = new RecordingProgress();

        await _scanner.ScanAsync([dir.Path], progress);

        MusicScanProgress snapshot = Assert.Single(progress.Snapshots);
        Assert.Equal("夜曲.mp3", snapshot.CurrentFile);
        Assert.DoesNotContain(dir.Path, snapshot.CurrentFile);
    }

    [Fact]
    public async Task ScanAsync_NullProgress_IsAccepted()
    {
        using var dir = new TempDir();
        WriteFiles(dir, 3);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path], progress: null);

        Assert.Equal(3, result.Songs.Count);
    }

    // ───────────────────────── 取消 ─────────────────────────

    [Fact]
    public async Task ScanAsync_AlreadyCancelledToken_ReturnsEmptyResultFlaggedAsCancelled()
    {
        using var dir = new TempDir();
        dir.Write("a.mp3", StubBytes);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = MusicScanResult.Empty();
        List<SystemToolkit.Core.Logging.LogEntry> bus = await BusCapture.RecordAsync(async () =>
        {
            result = await _scanner.ScanAsync([dir.Path], null, cts.Token);
        });

        Assert.True(result.WasCancelled);
        Assert.Empty(result.Songs);
        Assert.False(result.IsClean);
        Assert.Equal(0, _tagReader.ReadCount);
        // LOG-4：取消留痕由 timing 记录承载（Warn 级 + Cancelled），不再走散点 logger
        Assert.Contains(bus, e => e.Action == "ScanMusicLibrary"
            && e.Outcome == SystemToolkit.Core.Logging.LogResult.Cancelled
            && e.Level == SystemToolkit.Core.Logging.LogLevel.Warn);
    }

    /// <remarks>
    /// 🔴 旧实现取消时抛 <see cref="OperationCanceledException"/>，已解析的成果全部作废。
    /// 用户扫到第 3000 首点取消，前 3000 首必须留下来。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_CancelledMidScan_KeepsPartialResultsInsteadOfThrowing()
    {
        using var dir = new TempDir();
        const int total = 200;
        WriteFiles(dir, total);
        using var cts = new CancellationTokenSource();
        int reads = 0;
        _tagReader.TagFactory = path =>
        {
            if (Interlocked.Increment(ref reads) == 3)
            {
                cts.Cancel();
            }

            return new MusicTagInfo { Title = Path.GetFileNameWithoutExtension(path) };
        };

        var result = MusicScanResult.Empty();
        List<SystemToolkit.Core.Logging.LogEntry> bus = await BusCapture.RecordAsync(async () =>
        {
            result = await _scanner.ScanAsync([dir.Path], null, cts.Token);
        });

        Assert.True(result.WasCancelled);
        // 至少 3 首（触发取消的那次读取本身也会入账），且远少于总数
        Assert.InRange(result.Songs.Count, 3, total - 1);
        Assert.All(result.Songs, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        // LOG-4：取消（保留部分结果）由 timing 记录承载，消息含「保留部分结果」
        Assert.Contains(bus, e => e.Action == "ScanMusicLibrary"
            && e.Outcome == SystemToolkit.Core.Logging.LogResult.Cancelled
            && e.Message.Contains("保留部分结果", StringComparison.Ordinal));
    }

    // ───────────────────────── 日志 ─────────────────────────

    [Fact]
    public async Task ScanAsync_CleanScan_LogsNoWarning()
    {
        using var dir = new TempDir();
        WriteFiles(dir, 3);

        MusicScanResult result = await _scanner.ScanAsync([dir.Path]);

        Assert.True(result.IsClean);
        Assert.False(_logger.HasWarning, _logger.Dump());
    }

    /// <remarks>
    /// 完成日志必须把「成功 / 失败 / 不可达」三个数都报出来——这是 🔴 禁止静默失败在日志侧的落点：
    /// 排查「为什么少了 12 首」时，日志得先给出量级。
    /// </remarks>
    [Fact]
    public async Task ScanAsync_CompletionLogReportsSuccessFailureAndInaccessibleCounts()
    {
        using var dir = new TempDir();
        dir.Write("good.mp3", StubBytes);
        dir.Write("bad.mp3", StubBytes);
        _tagReader.FailWhenPathContains = "bad";

        List<SystemToolkit.Core.Logging.LogEntry> bus =
            await BusCapture.RecordAsync(async () =>
            {
                await _scanner.ScanAsync([dir.Path, dir.Resolve("gone")]);
            });

        Assert.Contains(_logger.Messages, m => m.Contains("开始扫描 2 个文件", StringComparison.Ordinal));
        // LOG-4：终态三计数由 ScanMusicLibrary 结构化记录承载（成功/失败/不可达必须可见）
        Assert.Contains(bus, e => e.Action == "ScanMusicLibrary"
            && e.Message.Contains("成功 1、失败 1、不可达目录 1", StringComparison.Ordinal));
    }

    // ───────────────────────── 夹具 ─────────────────────────

    /// <summary>在临时目录（可指定子目录）下批量写占位音频文件，命名 <c>track0000.mp3</c> 起。</summary>
    private static void WriteFiles(TempDir dir, int count, string subdirectory = "")
    {
        for (int i = 0; i < count; i++)
        {
            dir.Write(Path.Combine(subdirectory, $"track{i:D4}.mp3"), StubBytes);
        }
    }
}
