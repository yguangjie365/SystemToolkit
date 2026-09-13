using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using Xunit;

namespace SystemToolkit.Tests;

/// <summary>
/// 安装历史（落地计划 B4-②）的裁剪、落盘与 CSV 导出。
/// 独立存储的理由见 <see cref="InstallHistoryEntry"/>：`AppLog` 零读取 API 且有保留期/滚动，
/// 不能当历史源（历史的价值就是经得起时间）。
/// </summary>
public sealed class InstallHistoryTests : IDisposable
{
    private readonly string _dir;

    public InstallHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stk-history-" + Guid.NewGuid().ToString("N"));
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

    private static InstallHistoryEntry Entry(string id, InstallAction action = InstallAction.Install,
        InstallOutcome outcome = InstallOutcome.Success, string name = "", string detail = "") =>
        new InstallHistoryEntry
        {
            Timestamp = DateTimeOffset.Now,
            Action = action,
            Outcome = outcome,
            PackageId = id,
            Name = name.Length > 0 ? name : id,
            Detail = detail,
        };

    [Fact]
    public void Append_KeepsNewestFirst()
    {
        var log = new InstallHistoryLog();

        log.Append(Entry("first"));
        log.Append(Entry("second"));

        Assert.Equal(new[] { "second", "first" }, log.Entries.Select(e => e.PackageId).ToArray());
    }

    [Fact]
    public void Append_OverLimit_DropsOldestAndReportsCount()
    {
        var log = new InstallHistoryLog();

        int dropped = 0;
        for (int i = 0; i < InstallHistoryLog.MaxEntries + 2; i++)
        {
            dropped += log.Append(Entry($"pkg.{i}"));
        }

        Assert.Equal(2, dropped);
        Assert.Equal(InstallHistoryLog.MaxEntries, log.Entries.Count);
        // 最新在前：最后追加的在首位；最旧两条已被丢弃
        Assert.Equal($"pkg.{InstallHistoryLog.MaxEntries + 1}", log.Entries[0].PackageId);
        Assert.DoesNotContain(log.Entries, e => e.PackageId is "pkg.0" or "pkg.1");
    }

    [Fact]
    public void Sanitize_DropsEntriesWithoutPackageId()
    {
        var log = new InstallHistoryLog
        {
            Entries = { Entry(""), Entry("keep.me") },
        };

        Assert.Equal(1, log.Sanitize());
        Assert.Single(log.Entries);
    }

    [Theory]
    [InlineData(InstallAction.Install, "安装")]
    [InlineData(InstallAction.Upgrade, "升级")]
    [InlineData(InstallAction.Uninstall, "卸载")]
    public void Labels_ActionText_CoversEveryValue(InstallAction action, string expected) =>
        Assert.Equal(expected, InstallHistoryLabels.ActionText(action));

    [Theory]
    [InlineData(InstallOutcome.Success, "成功")]
    [InlineData(InstallOutcome.Failed, "失败")]
    [InlineData(InstallOutcome.Cancelled, "取消")]
    [InlineData(InstallOutcome.Skipped, "跳过")]
    public void Labels_OutcomeText_CoversEveryValue(InstallOutcome outcome, string expected) =>
        Assert.Equal(expected, InstallHistoryLabels.OutcomeText(outcome));

    [Fact]
    public void Store_SaveThenLoad_RoundTrips()
    {
        var store = new InstallHistoryStore(_dir);
        var log = new InstallHistoryLog();
        log.Append(Entry("voidtools.Everything", InstallAction.Upgrade, InstallOutcome.Failed));
        store.Save(log);

        InstallHistoryLog loaded = store.Load();

        Assert.Single(loaded.Entries);
        InstallHistoryEntry entry = loaded.Entries[0];
        Assert.Equal("voidtools.Everything", entry.PackageId);
        Assert.Equal(InstallAction.Upgrade, entry.Action);
        Assert.Equal(InstallOutcome.Failed, entry.Outcome);
    }

    [Fact]
    public void Store_LoadMissingFile_ReturnsEmpty()
    {
        var store = new InstallHistoryStore(_dir);

        Assert.Empty(store.Load().Entries);
    }

    [Fact]
    public void Store_LoadCorruptFile_ReturnsEmptyAndKeepsBackup()
    {
        var store = new InstallHistoryStore(_dir);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Empty(store.Load().Entries);
        Assert.NotEmpty(Directory.GetFiles(_dir, InstallHistoryStore.FileName + ".corrupt_*"));
    }

    [Fact]
    public void Store_Save_FailureThrows()
    {
        // 写失败必须上抛：调用方据此决定"提示用户"还是"降级留痕"，不能静默当成功
        string filePath = Path.Combine(_dir, "not-a-dir");
        File.WriteAllText(filePath, "x");
        var store = new InstallHistoryStore(filePath);

        Assert.Throws<IOException>(() => store.Save(new InstallHistoryLog()));
    }

    [Fact]
    public void Csv_OneLinePerEntryWithSharedLabels()
    {
        string csv = InstallHistoryCsv.Build(new[]
        {
            Entry("a.b", InstallAction.Upgrade, InstallOutcome.Success, name: "演示软件"),
        });

        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(InstallHistoryCsv.Header, lines[0]);
        Assert.Equal("时间,动作,软件,包ID,原版本,新版本,结果,说明", lines[0]);
        Assert.Contains("升级", lines[1], StringComparison.Ordinal);
        Assert.Contains("成功", lines[1], StringComparison.Ordinal);
        Assert.Contains("演示软件", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("普通文本", "普通文本")]
    [InlineData("含,逗号", "\"含,逗号\"")]
    [InlineData("含\"引号\"", "\"含\"\"引号\"\"\"")]
    [InlineData("含\n换行", "\"含\n换行\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Csv_Escape_FollowsRfc4180(string? raw, string expected) =>
        Assert.Equal(expected, InstallHistoryCsv.Escape(raw));

    [Fact]
    public void Csv_DetailWithNewline_IsQuotedSoFieldStaysSingleColumn()
    {
        // 说明里带换行的记录必须被双引号包裹，否则在 Excel 里整表错位
        string csv = InstallHistoryCsv.Build(new[]
        {
            Entry("a.b", detail: "第一行\n第二行"),
        });

        Assert.Contains("\"第一行\n第二行\"", csv, StringComparison.Ordinal);
    }
}
