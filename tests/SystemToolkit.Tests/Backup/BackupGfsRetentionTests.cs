using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// GFS（日 / 周 / 月）快照保留判据（B5b-②）。
/// <para>
/// 🔴 这是本模块**风险最高**的一批（判据错了就是真删用户快照），所以判据被做成**纯函数**
/// （<c>SnapshotManager.SelectGfsDeletion</c>）：不碰磁盘、时间由调用方注入，
/// 因此能精确断言"该删哪几个目录"，也能做反向验证。
/// </para>
/// <para>
/// 用例重点钉四条：① **最新一份永不删**（哪怕配额全 0）；② 同一周期只留最新一份；
/// ③ 日 / 周 / 月取**并集**（靠任一维度活下来就不删）；④ 目录名解析不出时间时**不删**。
/// </para>
/// </summary>
public class BackupGfsRetentionTests
{
    private static (string Dir, DateTime Time) Entry(string dir, int year, int month, int day, int hour) =>
        (dir, new DateTime(year, month, day, hour, 0, 0));

    [Fact]
    public void SelectGfsDeletion_EmptyInput_ReturnsEmpty() =>
        Assert.Empty(SnapshotManager.SelectGfsDeletion([], GfsRetention.Default));

    [Fact]
    public void SelectGfsDeletion_KeepsOnlyNewestOfSameDay()
    {
        (string Dir, DateTime Time)[] entries =
        [
            Entry("newest", 2026, 9, 13, 18),
            Entry("noon", 2026, 9, 13, 12),
            Entry("morning", 2026, 9, 13, 9),
        ];

        IReadOnlyList<string> doomed = SnapshotManager.SelectGfsDeletion(entries, new GfsRetention(1, 0, 0));

        Assert.Equal(["noon", "morning"], doomed);
    }

    [Fact]
    public void SelectGfsDeletion_AlwaysKeepsNewest_EvenWhenAllQuotasAreZero()
    {
        // 🔴 红线：三项配额全 0 时的语义是"只留最新一份"，**不是"删光"**
        (string Dir, DateTime Time)[] entries =
        [
            Entry("newest", 2026, 9, 13, 18),
            Entry("older", 2026, 9, 1, 9),
        ];

        IReadOnlyList<string> doomed = SnapshotManager.SelectGfsDeletion(entries, new GfsRetention(0, 0, 0));

        Assert.DoesNotContain("newest", doomed);
        Assert.Contains("older", doomed);
    }

    [Fact]
    public void SelectGfsDeletion_IsUnionOfDayWeekMonth()
    {
        // Daily=1 / Weekly=2 / Monthly=1；A 与 B 同日在同一 ISO 周同一月；C 同周但另一天；D 只另属一周
        (string Dir, DateTime Time)[] entries =
        [
            Entry("A", 2026, 9, 13, 18), // 最新：日 / 周 / 月 都命中
            Entry("B", 2026, 9, 13, 9),  // 同日更旧：三个维度都已经被占
            Entry("C", 2026, 9, 8, 18),  // 另一天但仍在同一 ISO 周与同一月
            Entry("D", 2026, 8, 20, 18), // 更早的另一周、另一月：靠"周配额"活下来
        ];

        IReadOnlyList<string> doomed = SnapshotManager.SelectGfsDeletion(entries, new GfsRetention(1, 2, 1));

        Assert.DoesNotContain("A", doomed);
        Assert.DoesNotContain("D", doomed); // ← 并集的意义：日/月都没轮到它，但周轮到
        Assert.Contains("B", doomed);
        Assert.Contains("C", doomed);
    }

    [Fact]
    public void SelectGfsDeletion_WeeklyQuota_KeepsNewestPerWeek()
    {
        (string Dir, DateTime Time)[] entries =
        [
            Entry("w2-new", 2026, 9, 8, 18),
            Entry("w2-old", 2026, 9, 7, 18),  // 同一 ISO 周（周一）
            Entry("w1", 2026, 9, 1, 18),      // 上一周
        ];

        IReadOnlyList<string> doomed = SnapshotManager.SelectGfsDeletion(entries, new GfsRetention(0, 1, 0));

        Assert.DoesNotContain("w2-new", doomed);
        Assert.Contains("w2-old", doomed);
        Assert.Contains("w1", doomed);
    }

    [Fact]
    public void SelectGfsDeletion_MonthlyQuota_KeepsNewestPerMonth()
    {
        (string Dir, DateTime Time)[] entries =
        [
            Entry("sep-new", 2026, 9, 13, 18),
            Entry("sep-old", 2026, 9, 1, 9),
            Entry("aug", 2026, 8, 31, 23),
        ];

        IReadOnlyList<string> doomed = SnapshotManager.SelectGfsDeletion(entries, new GfsRetention(0, 0, 1));

        Assert.DoesNotContain("sep-new", doomed);
        Assert.Contains("sep-old", doomed);
        Assert.Contains("aug", doomed);
    }

    [Fact]
    public void GfsRetention_ClampsOutOfRangePeriods()
    {
        var negative = new GfsRetention(-5, -1, -100);
        Assert.Equal(0, negative.PeriodSum);
        Assert.True(negative.IsEmpty);

        var huge = new GfsRetention(int.MaxValue, 1000, 7);
        Assert.Equal(GfsRetention.MaxPeriods, huge.Daily);
        Assert.Equal(GfsRetention.MaxPeriods, huge.Weekly);
        Assert.Equal(7, huge.Monthly);
        Assert.False(huge.IsEmpty);
    }

    [Fact]
    public void GfsRetention_Default_IsSevenFourSix()
    {
        Assert.Equal(7, GfsRetention.Default.Daily);
        Assert.Equal(4, GfsRetention.Default.Weekly);
        Assert.Equal(6, GfsRetention.Default.Monthly);
    }

    [Theory]
    [InlineData("20260913_180000_ab12cd", true)] // 新格式：时间戳 + 随机后缀
    [InlineData("20260913_180000_2", true)]      // 旧格式：时间戳 + 序号
    [InlineData("20260913_180000", true)]        // 最旧格式：纯时间戳
    [InlineData("not-a-timestamp", false)]
    [InlineData("20260913", false)]              // 太短
    [InlineData("2026-09-13_180000", false)]     // 分隔符不对
    public void TryParseSnapshotTime_HandlesAllNamingFormats(string name, bool expected)
    {
        Assert.Equal(expected, SnapshotManager.TryParseSnapshotTime(name, out _));
    }

    [Fact]
    public void TryParseSnapshotTime_ReadsFullPath_AndExactValue()
    {
        Assert.True(SnapshotManager.TryParseSnapshotTime(
            @"D:\Backup\Rule_x\snapshots\20260913_180000_ab12cd",
            out DateTime time));
        Assert.Equal(new DateTime(2026, 9, 13, 18, 0, 0), time);
    }
}
