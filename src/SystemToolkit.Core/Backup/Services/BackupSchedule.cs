using SystemToolkit.Core.Backup.Models;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>
/// 定时备份判定（批次二；自旧工程 ScheduledBackupHost.IsDue 纯判定搬移并按规则粒度化）：
/// 每日 DailyTime 到点即到期；错过后（关机/未开机）下次运行补做一次；
/// LastRunDate（yyyy-MM-dd）== 今天 ⇒ 不重复执行。
/// </summary>
public static class BackupSchedule
{
    /// <summary>判定该规则此刻是否应执行定时备份（调用方负责 Enabled 与 EnableSchedule 前提）。</summary>
    /// <param name="rule">目标规则。</param>
    /// <param name="now">当前本地时间（注入以便测试）。</param>
    /// <returns>true = 到期应执行（含错过补做）；false = 未到点或今日已执行。</returns>
    public static bool IsDue(BackupRule rule, DateTime now)
    {
        if (!rule.Enabled || !rule.EnableSchedule)
        {
            return false;
        }

        if (!TimeSpan.TryParseExact(rule.DailyTime, @"hh\:mm", null, out TimeSpan daily))
        {
            return false;
        }

        // 今天已执行过（LastRunDate == 今天）⇒ 不重复；否则到点（now >= 今日 DailyTime）即补做
        if (string.Equals(rule.LastRunDate, now.ToString("yyyy-MM-dd"), StringComparison.Ordinal))
        {
            return false;
        }

        return now.TimeOfDay >= daily;
    }

    /// <summary>执行完成后的 LastRunDate 记账值（本地当天）。</summary>
    public static string MarkRunDate(DateTime now) => now.ToString("yyyy-MM-dd");

    /// <summary>
    /// 每日时间是否合法（<c>HH:mm</c>，00:00–23:59）。
    /// 🔴 2026-09-08：UI 此前只校验 <c>\d{2}:\d{2}</c> 格式而不校验范围——
    /// "25:00" 能存进规则，但 <see cref="IsDue"/> 解析失败 → 定时<b>永不执行且无任何提示</b>。
    /// 校验口径集中于此，UI 与本类的判定共用同一事实来源。
    /// </summary>
    public static bool IsValidDailyTime(string? dailyTime)
        => !string.IsNullOrWhiteSpace(dailyTime)
           && TimeSpan.TryParseExact(dailyTime.Trim(), @"hh\:mm",
               System.Globalization.CultureInfo.InvariantCulture, out TimeSpan t)
           && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
}
