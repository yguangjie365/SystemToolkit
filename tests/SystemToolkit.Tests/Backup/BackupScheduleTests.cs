using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 定时备份判定测试（批次二；设计 §8 验收：错过补做一次 + 同一天不重复）。
/// </summary>
public class BackupScheduleTests
{
    private static BackupRule MakeRule(string dailyTime = "03:00", string lastRunDate = "", bool enabled = true, bool schedule = true)
        => new()
        {
            RuleName = "定时规则",
            SourcePaths = ["C:\\data"],
            Enabled = enabled,
            EnableSchedule = schedule,
            DailyTime = dailyTime,
            LastRunDate = lastRunDate,
        };

    private static readonly DateTime Now = new(2026, 9, 6, 15, 0, 0);

    [Fact]
    public void IsDue_AfterDailyTime_Overdue()
    {
        Assert.True(BackupSchedule.IsDue(MakeRule(), Now)); // 15:00 > 03:00，今天未执行
    }

    [Fact]
    public void IsDue_BeforeDailyTime_NotDue()
    {
        var now = new DateTime(2026, 9, 6, 1, 0, 0);
        Assert.False(BackupSchedule.IsDue(MakeRule(), now)); // 01:00 < 03:00
    }

    [Fact]
    public void IsDue_RanToday_NotDueAgain()
    {
        // 同一天不重复（验收要点）
        Assert.False(BackupSchedule.IsDue(MakeRule(lastRunDate: "2026-09-06"), Now));
    }

    [Fact]
    public void IsDue_MissedDays_MakesUpOnce()
    {
        // 错过两天：LastRunDate 停在三天前 → 到点即补做（补做一次由 MarkRun 记账保证）
        Assert.True(BackupSchedule.IsDue(MakeRule(lastRunDate: "2026-09-04"), Now));
        BackupRule rule = MakeRule(lastRunDate: "2026-09-04");
        rule.LastRunDate = BackupSchedule.MarkRunDate(Now);
        Assert.False(BackupSchedule.IsDue(rule, Now)); // 补做后当天不再执行
    }

    [Theory]
    [InlineData(false, true)]  // 规则停用
    [InlineData(true, false)]  // 未开定时
    public void IsDue_GatesDisabled(bool enabled, bool schedule)
    {
        Assert.False(BackupSchedule.IsDue(MakeRule(enabled: enabled, schedule: schedule), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("3点")]
    [InlineData("25:00")]
    public void IsDue_InvalidDailyTime_NeverDue(string dailyTime)
    {
        Assert.False(BackupSchedule.IsDue(MakeRule(dailyTime: dailyTime), Now));
    }
}
