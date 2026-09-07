using SystemToolkit.Core.Overview.Models;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>一次采集的完整结果：硬件统计卡、硬件详情卡、系统卡片、已安装程序列表。</summary>
public sealed class OverviewData
{
    /// <summary>硬件统计卡（顶部一排大数字：处理器占用 / 内存占用 / 存储总量）。</summary>
    public List<OverviewItem> HardwareStats { get; init; } = new();

    /// <summary>硬件详情卡（型号/厂商/频率等一行一条）。</summary>
    public List<OverviewItem> Hardware { get; init; } = new();

    /// <summary>系统卡片（操作系统版本/启动时间/安全状态等）。</summary>
    public List<OverviewItem> System { get; init; } = new();

    /// <summary>已安装程序列表。</summary>
    public List<InstalledProgram> InstalledPrograms { get; init; } = new();

    /// <summary>
    /// 传感器快照（详情弹窗 / 报告导出用）。null = 本次采集未拿到传感器数据
    /// （驱动不可用等）。随磁盘缓存一并持久化，启动秒显时传感器为缓存时点值。
    /// </summary>
    public SensorSnapshot? Sensors { get; set; }

    /// <summary>
    /// 存储统计卡副标题：按介质聚合的「类型+容量」汇总（如 "SSD 1 TB"、"SSD 476.9 GB + HDD 931.5 GB"；
    /// 2026-09-05 用户需求，替代原"首块盘型号"）。介质解析失败为 null（调用方回退旧行为）。
    /// </summary>
    public string? StorageSummary { get; set; }
}
