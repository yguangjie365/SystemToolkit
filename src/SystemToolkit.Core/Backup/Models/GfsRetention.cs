namespace SystemToolkit.Core.Backup.Models;

/// <summary>
/// GFS（Grandfather-Father-Son）快照保留配额：日 / 周 / 月各保留多少个周期。
/// **每个周期只保留该周期内最新的一份**（它最完整、也最接近当前）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这份策略是**可选的**：规则或全局配置里没有它（null）时，维持原来的「固定条数」行为，
/// 于是旧 <c>rules.json</c> 读进来行为**完全不变**——这就是计划要求的"回退到固定 N 的兼容读法"。
/// </para>
/// <para>
/// ⚠️ GFS 会**主动删除**超出时间纵深的旧快照，是本模块风险最高的动作之一：
/// 因此判据是纯函数（<c>SnapshotManager.SelectGfsDeletion</c>，可离线断言），
/// 且**最新一份永不进删除列表**；删除失败立即停止，不继续往下删。
/// </para>
/// </remarks>
public sealed record GfsRetention
{
    /// <summary>默认配额：7 日 / 4 周 / 6 月。</summary>
    public static GfsRetention Default { get; } = new(7, 4, 6);

    /// <summary>任一周期配额的上限（超过按上限算，防止配置成天文数字导致快照无限累积）。</summary>
    public const int MaxPeriods = 100;

    /// <summary>初始化配额（非法值一律钳制到 0..<see cref="MaxPeriods"/>）。</summary>
    /// <param name="daily">保留最近多少个「日」（每份一天）。</param>
    /// <param name="weekly">保留最近多少个「周」（ISO 周，周一为周首）。</param>
    /// <param name="monthly">保留最近多少个「月」。</param>
    public GfsRetention(int daily = 7, int weekly = 4, int monthly = 6)
    {
        Daily = ClampPeriods(daily);
        Weekly = ClampPeriods(weekly);
        Monthly = ClampPeriods(monthly);
    }

    /// <summary>保留的日数。</summary>
    public int Daily { get; }

    /// <summary>保留的周数（ISO 周）。</summary>
    public int Weekly { get; }

    /// <summary>保留的月数。</summary>
    public int Monthly { get; }

    /// <summary>
    /// 三项配额之和。注意它是**保留份数的上界**（同一个快照可能同时命中日 / 周 / 月，实际保留会少于此值）。
    /// </summary>
    public int PeriodSum => Daily + Weekly + Monthly;

    /// <summary>
    /// 是否一项都没配。此时保留策略退化为「只留最新一份」——这是**有意的**兜底，
    /// 不是"删光所有快照"。
    /// </summary>
    public bool IsEmpty => PeriodSum == 0;

    /// <summary>是否与另一份配额等价（用于变更日志/界面提示）。</summary>
    /// <param name="other">对照配额。</param>
    public bool SameAs(GfsRetention? other) =>
        other is not null && Daily == other.Daily && Weekly == other.Weekly && Monthly == other.Monthly;

    private static int ClampPeriods(int value) => Math.Clamp(value, 0, MaxPeriods);
}
