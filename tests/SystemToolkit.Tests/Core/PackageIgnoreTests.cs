using System.Text.Json;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using Xunit;

namespace SystemToolkit.Tests;

/// <summary>
/// 忽略清单（包级「跳过此版本 / 永久忽略」）的判据与落盘 —— 落地计划 B4-①③。
/// 判据是**唯一**的（<see cref="PackageIgnoreList.IsIgnored"/>），这里钉住它的全部边界，
/// 因为它决定"要不要跳过这个包"，错了会**静默藏掉**本该处理的项。
/// </summary>
public sealed class PackageIgnoreTests : IDisposable
{
    private readonly string _dir;

    public PackageIgnoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stk-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    private static PackageIgnoreList WithEntry(
        string id,
        PackageIgnoreScope scope,
        string version = "",
        string source = "",
        DateTimeOffset? recordedAt = null)
    {
        var list = new PackageIgnoreList();
        list.AddOrUpdate(new PackageIgnoreEntry
        {
            Id = id,
            Source = source,
            Scope = scope,
            Version = version,
            RecordedAt = recordedAt ?? DateTimeOffset.UtcNow,
        });
        return list;
    }

    [Fact]
    public void IsIgnored_Permanent_MatchesAnyVersion()
    {
        PackageIgnoreList list = WithEntry("voidtools.Everything", PackageIgnoreScope.Permanent);

        Assert.True(list.IsIgnored("voidtools.Everything", "winget", "1.4.1"));
        Assert.True(list.IsIgnored("voidtools.everything", "winget", "9.9.9")); // Id 大小写不敏感
        Assert.True(list.IsIgnored("voidtools.Everything", "winget", null));
    }

    [Fact]
    public void IsIgnored_VersionScope_OnlyMatchesSameVersion()
    {
        PackageIgnoreList list = WithEntry("voidtools.Everything", PackageIgnoreScope.Version, "1.4.1");

        Assert.True(list.IsIgnored("voidtools.Everything", "winget", "1.4.1"));
        Assert.False(list.IsIgnored("voidtools.Everything", "winget", "1.5.0")); // 新版本要重新提示
    }

    [Fact]
    public void IsIgnored_VersionScope_UnknownCandidateVersion_DoesNotMatch()
    {
        // 🔴 保守方向：无法确认是同一版本时**不跳过**（跳过会静默藏掉东西，不跳过最多多提示一次）
        PackageIgnoreList list = WithEntry("voidtools.Everything", PackageIgnoreScope.Version, "1.4.1");

        Assert.False(list.IsIgnored("voidtools.Everything", "winget", ""));
        Assert.False(list.IsIgnored("voidtools.Everything", "winget", null));
    }

    [Fact]
    public void IsIgnored_EntryWithSource_RequiresSameSource()
    {
        PackageIgnoreList list = WithEntry("X", PackageIgnoreScope.Permanent, source: "msstore");

        Assert.True(list.IsIgnored("X", "msstore", null));
        Assert.False(list.IsIgnored("X", "winget", null));
    }

    [Fact]
    public void IsIgnored_EntryWithoutSource_MatchesAnySource()
    {
        PackageIgnoreList list = WithEntry("X", PackageIgnoreScope.Permanent);

        Assert.True(list.IsIgnored("X", "msstore", null));
        Assert.True(list.IsIgnored("X", "winget", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void IsIgnored_BlankId_IsNeverIgnored(string? id) =>
        Assert.False(WithEntry("X", PackageIgnoreScope.Permanent).IsIgnored(id, "winget", null));

    [Fact]
    public void AddOrUpdate_SameIdAndSource_ReplacesInsteadOfDuplicating()
    {
        PackageIgnoreList list = WithEntry("X", PackageIgnoreScope.Version, "1.0");

        list.AddOrUpdate(new PackageIgnoreEntry
        {
            Id = "X",
            Source = "",
            Scope = PackageIgnoreScope.Permanent,
            RecordedAt = DateTimeOffset.UtcNow,
        });

        Assert.Single(list.Entries);
        Assert.Equal(PackageIgnoreScope.Permanent, list.Entries[0].Scope);
    }

    [Fact]
    public void AddOrUpdate_OverLimit_DropsOldestAndReportsCount()
    {
        var list = new PackageIgnoreList();
        DateTimeOffset baseTime = DateTimeOffset.UtcNow;

        int dropped = 0;
        for (int i = 0; i < PackageIgnoreList.MaxEntries + 2; i++)
        {
            dropped += list.AddOrUpdate(new PackageIgnoreEntry
            {
                Id = $"pkg.{i}",
                Scope = PackageIgnoreScope.Permanent,
                RecordedAt = baseTime.AddMinutes(i),
            });
        }

        Assert.Equal(2, dropped);
        Assert.Equal(PackageIgnoreList.MaxEntries, list.Entries.Count);
        // 丢的是最旧的两条（pkg.0 / pkg.1）
        Assert.DoesNotContain(list.Entries, e => e.Id is "pkg.0" or "pkg.1");
        Assert.Contains(list.Entries, e => e.Id == $"pkg.{PackageIgnoreList.MaxEntries + 1}");
    }

    [Fact]
    public void Remove_DropsAllScopesForSameId()
    {
        PackageIgnoreList list = WithEntry("X", PackageIgnoreScope.Permanent);
        list.AddOrUpdate(new PackageIgnoreEntry
        {
            Id = "x",
            Source = "msstore",
            Scope = PackageIgnoreScope.Version,
            Version = "1.0",
            RecordedAt = DateTimeOffset.UtcNow,
        });

        int removed = list.Remove("X");

        Assert.Equal(2, removed);
        Assert.False(list.Contains("X"));
    }

    [Fact]
    public void Sanitize_DropsEntriesWithoutId()
    {
        var list = new PackageIgnoreList
        {
            Entries =
            {
                new PackageIgnoreEntry { Id = "" },
                new PackageIgnoreEntry { Id = "keep.me", Scope = PackageIgnoreScope.Permanent },
            },
        };

        Assert.Equal(1, list.Sanitize());
        Assert.Single(list.Entries);
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTrips()
    {
        var store = new PackageIgnoreStore(_dir);
        PackageIgnoreList list = WithEntry("voidtools.Everything", PackageIgnoreScope.Version, "1.4.1", "winget");

        store.Save(list);
        PackageIgnoreList loaded = store.Load();

        Assert.True(loaded.IsIgnored("voidtools.Everything", "winget", "1.4.1"));
        Assert.False(loaded.IsIgnored("voidtools.Everything", "winget", "1.5.0"));
    }

    [Fact]
    public void Store_LoadMissingFile_ReturnsEmpty()
    {
        var store = new PackageIgnoreStore(_dir);

        PackageIgnoreList loaded = store.Load();

        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public void Store_LoadCorruptFile_ReturnsEmptyAndKeepsBackup()
    {
        var store = new PackageIgnoreStore(_dir);
        File.WriteAllText(store.FilePath, "{ this is not json");

        PackageIgnoreList loaded = store.Load();

        Assert.Empty(loaded.Entries);
        Assert.NotEmpty(Directory.GetFiles(_dir, PackageIgnoreStore.FileName + ".corrupt_*"));
    }

    [Fact]
    public void Store_Save_WritesAtomicallyAndSurvivesReloadByOtherInstance()
    {
        // 跨实例：同一份存储、两个 store 实例（忽略清单要在重启后仍然生效）
        var first = new PackageIgnoreStore(_dir);
        var second = new PackageIgnoreStore(_dir);

        first.Save(WithEntry("X", PackageIgnoreScope.Permanent));

        Assert.True(second.Load().IsIgnored("X", "winget", null));
    }

    [Fact]
    public void Store_Save_FailureThrows()
    {
        // 写失败必须上抛：否则"用户以为记住了、其实没写"（界面在骗人）
        string filePath = Path.Combine(_dir, "a-file-not-a-dir");
        File.WriteAllText(filePath, "x");
        var store = new PackageIgnoreStore(filePath);

        Assert.Throws<IOException>(() => store.Save(new PackageIgnoreList()));
    }

    [Fact]
    public void Save_SerializesWithSnakeCaseNames()
    {
        var store = new PackageIgnoreStore(_dir);
        store.Save(WithEntry("X", PackageIgnoreScope.Version, "1.0", "winget"));

        string json = File.ReadAllText(store.FilePath);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("format_version").GetInt32());
        JsonElement entry = doc.RootElement.GetProperty("entries")[0];
        Assert.Equal("X", entry.GetProperty("id").GetString());
        Assert.Equal("1.0", entry.GetProperty("version").GetString());
    }
}
