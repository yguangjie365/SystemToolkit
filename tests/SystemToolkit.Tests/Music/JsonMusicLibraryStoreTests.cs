using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 曲库存储测试（MUSIC-5）：往返一致性、损坏降级、消失文件剔除、自动建目录。
/// 契约红线：损坏/不可读必须带降级警告（🔴 禁止静默失败）。
/// </summary>
public class JsonMusicLibraryStoreTests
{
    private sealed class NoopLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
        public void Info(string message) { }
    }

    private static MusicSong Song(string path, string name = "T")
        => new() { Id = MusicSong.BuildIdFor(path), LocalPath = path, Name = name };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"music-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsLibrary()
    {
        string dir = TempDir();
        try
        {
            string file = Path.Combine(dir, "music-library.json");
            // 真实创建文件：LoadAsync 会剔除 LocalPath 不存在的条目，不建文件会被剔光
            foreach (string f in new[] { "a.mp3", "b.flac" })
            {
                File.WriteAllText(Path.Combine(dir, f), "audio-bytes");
            }

            var store = new JsonMusicLibraryStore(file);
            var library = new MusicLibrary
            {
                ScanRoots = [dir],
                Songs = [Song(Path.Combine(dir, "a.mp3"), "A"), Song(Path.Combine(dir, "b.flac"), "B")],
                LastScanAtUtc = new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero),
            };

            await store.SaveAsync(library);
            MusicLibraryLoadResult result = await store.LoadAsync();

            Assert.False(result.IsDegraded);
            Assert.Equal(2, result.Library.Songs.Count);
            Assert.Equal("A", result.Library.Songs[0].Name);
            Assert.Equal(dir, Assert.Single(result.Library.ScanRoots));
            Assert.Equal(library.LastScanAtUtc, result.Library.LastScanAtUtc);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不阻塞断言
            }
        }
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmptyWithoutWarning()
    {
        string file = Path.Combine(TempDir(), "no-such.json");
        var store = new JsonMusicLibraryStore(file);

        MusicLibraryLoadResult result = await store.LoadAsync();

        Assert.Empty(result.Library.Songs);
        Assert.Null(result.LoadWarning);
        Assert.False(result.IsDegraded);
        Assert.EndsWith("no-such.json", store.LibraryFilePath);
    }

    [Fact]
    public async Task Load_CorruptedJson_ReturnsEmptyWithDegradedWarning()
    {
        NoopLogger logger = new();
        string dir = TempDir();
        try
        {
            string file = Path.Combine(dir, "music-library.json");
            await File.WriteAllTextAsync(file, "{ this is not json !!!");
            var store = new JsonMusicLibraryStore(file, logger);

            MusicLibraryLoadResult result = await store.LoadAsync();

            Assert.Empty(result.Library.Songs);
            Assert.True(result.IsDegraded);
            Assert.Contains("损坏", result.LoadWarning);
            Assert.NotEmpty(logger.Warnings);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不阻塞断言
            }
        }
    }

    [Fact]
    public async Task Load_RemovesMissingFiles_AndReportsCount()
    {
        NoopLogger logger = new();
        string dir = TempDir();
        try
        {
            string exists = Path.Combine(dir, "exists.mp3");
            File.WriteAllText(exists, "x");
            string gone = Path.Combine(dir, "gone.mp3"); // 故意不创建

            string file = Path.Combine(dir, "music-library.json");
            var store = new JsonMusicLibraryStore(file, logger);
            await store.SaveAsync(new MusicLibrary
            {
                ScanRoots = [dir],
                Songs = [Song(exists, "在"), Song(gone, "消失")],
            });

            MusicLibraryLoadResult result = await store.LoadAsync();

            Assert.Single(result.Library.Songs);
            Assert.Equal("在", result.Library.Songs[0].Name);
            Assert.NotNull(result.LoadWarning);
            Assert.Contains("1", result.LoadWarning);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不阻塞断言
            }
        }
    }

    [Fact]
    public async Task Save_CreatesDirectoryAutomatically()
    {
        string dir = TempDir();
        try
        {
            string nested = Path.Combine(dir, "sub", "music-library.json");
            var store = new JsonMusicLibraryStore(nested);

            await store.SaveAsync(new MusicLibrary { Songs = [Song(Path.Combine(dir, "a.mp3"))] });

            Assert.True(File.Exists(nested));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不阻塞断言
            }
        }
    }
}
