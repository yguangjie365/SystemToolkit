using System.Management;
using System.Runtime.Versioning;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>一次快速采样结果：内存占用百分比与磁盘活动百分比（null = 本次拿不到）。</summary>
public sealed record QuickSample(int? RamUsedPercent, int? DiskActivePercent);

/// <summary>
/// 内存 / 磁盘的轻量实时采样，供概览页 2 秒刷新使用。
/// 与 <see cref="LiveUsageSampler"/>（CPU/GPU）互补：<see cref="OverviewService.Collect"/> 是
/// 秒级耗时的全量采集，不适合每 2 秒执行；本类只跑两条 WMI formatted perf 查询
/// （内存 AvailableMBytes / 磁盘 %DiskTime "_Total"），单次几十毫秒。
/// 总物理内存经 WMI Win32_ComputerSystem 取一次后缓存（开机期内不变）。
/// 采样失败折叠为 null 分量，绝不抛出——与 LiveUsageSampler 同一设计约定。
/// </summary>
/// <remarks>
/// 复用纪律说明（2026-09-04）：旧工程采样器仅覆盖 CPU/GPU（为趋势图设计）；
/// 概览页 2s 卡片需要内存占用与磁盘活动，属新增数据源而非重写，故新增本类。
/// [SupportedOSPlatform] 用法与 LiveUsageSampler 同款：System.Management 带 windows
/// 平台标注，消费方为 net10.0-windows 模块层，不产生 CA1416。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class QuickPulseSampler
{
    private ulong? _totalPhysicalBytes;

    /// <summary>采样一次内存占用 / 磁盘活动；应在后台线程调用（UI 轮询经 Task.Run）。</summary>
    /// <param name="includeDisk">
    /// 是否采样磁盘活动。概览页 2s 刷新仅用内存（磁盘活动率展示已按用户 2026-09-04 反馈停用，
    /// 审查 P2：此前每 2s 白跑一条 WMI 查询），传 false 免去该查询开销。
    /// </param>
    public async Task<QuickSample?> SampleAsync(bool includeDisk = true)
    {
        int? ram = await Task.Run(SampleRamPercent).ConfigureAwait(false);
        if (!includeDisk)
        {
            return ram is null ? null : new QuickSample(ram, null);
        }

        int? disk = await Task.Run(SampleDiskActivePercent).ConfigureAwait(false);
        return ram is null && disk is null ? null : new QuickSample(ram, disk);
    }

    /// <summary>内存占用百分比；失败返回 null。</summary>
    private int? SampleRamPercent()
    {
        try
        {
            ulong? total = _totalPhysicalBytes ??= QueryTotalPhysicalBytes();
            if (total is not { } totalBytes || totalBytes == 0)
            {
                return null;
            }

            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT AvailableMBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                object? availableMbRaw = mo["AvailableMBytes"];
                if (availableMbRaw is null)
                {
                    return null;
                }

                ulong availableBytes = Convert.ToUInt64(availableMbRaw) * 1024UL * 1024UL;
                if (availableBytes >= totalBytes)
                {
                    return 0;
                }

                return (int)Math.Round(100.0 * (totalBytes - availableBytes) / totalBytes);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>系统盘（_Total 物理盘）活动百分比；失败返回 null。</summary>
    private int? SampleDiskActivePercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT PercentDiskTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                object? raw = mo["PercentDiskTime"];
                if (raw is null)
                {
                    return null;
                }

                int value = Convert.ToInt32(raw);
                // %DiskTime 在多队列 SSD 上可能超 100，钳制到 0-100
                return Math.Clamp(value, 0, 100);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>总物理内存（字节），开机期内不变，取一次缓存。</summary>
    private static ulong? QueryTotalPhysicalBytes()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                return Convert.ToUInt64(mo["TotalPhysicalMemory"]);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
