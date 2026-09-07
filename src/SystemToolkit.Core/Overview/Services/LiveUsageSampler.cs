using System.Management;
using System.Runtime.Versioning;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>一次实时占用采样结果（null 分量 = 该项本次拿不到）。</summary>
public sealed record UsageSample(int? CpuPercent, int? GpuPercent);

/// <summary>
/// CPU / GPU 占用的轻量实时采样（概览页 Sparkline 趋势图数据源）。
/// 与全量采集（OverviewService.Collect，秒级耗时）不同：本类只跑两条
/// WMI formatted perf 查询，单次约几十毫秒，可按秒级轮询。
/// - CPU：Win32_PerfFormattedData_PerfOS_Processor 的 "_Total" 实例；
/// - GPU：Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine 的
///   UtilizationPercentage 取最大值（实例按进程 × 引擎类型切分，直接求和会超 100）。
/// 采样失败（计数器不可用/系统不支持）折叠为 null 分量，绝不抛出。
/// </summary>
/// <remarks>
/// 平台标注：System.Management 带 windows 平台标注，而 Core 为跨平台 TFM（net10.0），
/// 本类须标注（调用方为 net10.0-windows 模块层，不产生 CA1416）——
/// 与 OverviewService 同款处理，Release「警告即错误」必需。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LiveUsageSampler
{
    /// <summary>
    /// 采样一次 CPU/GPU 占用；内部异常全部折叠。应在后台线程调用（UI 轮询经 Task.Run）。
    /// </summary>
    public async Task<UsageSample?> SampleAsync()
    {
        int? cpu = await Task.Run(SampleCpuPercent).ConfigureAwait(false);
        int? gpu = await Task.Run(SampleGpuPercent).ConfigureAwait(false);
        return cpu is null && gpu is null ? null : new UsageSample(cpu, gpu);
    }

    /// <summary>CPU 总占用（formatted perf 的 "_Total" 实例）；失败返回 null。</summary>
    private static int? SampleCpuPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name=\"_Total\"");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    object? raw = item.Properties["PercentProcessorTime"].Value;
                    if (uint.TryParse(raw?.ToString(), out uint pct))
                    {
                        return (int)Math.Min(pct, 100);
                    }
                }
            }
        }
        catch
        {
            // 计数器不可用（权限/精简系统）时静默降级，趋势图暂停推进
        }
        return null;
    }

    /// <summary>GPU 占用（GPU Engine 计数器按实例取最大值）；不可用返回 null。</summary>
    private static int? SampleGpuPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            int max = -1;
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    object? raw = item.Properties["UtilizationPercentage"].Value;
                    if (uint.TryParse(raw?.ToString(), out uint pct))
                    {
                        max = Math.Max(max, (int)Math.Min(pct, 100));
                    }
                }
            }
            return max >= 0 ? max : null;
        }
        catch
        {
            // GPU 计数器在部分驱动/系统上不存在，属正常降级
            return null;
        }
    }
}
