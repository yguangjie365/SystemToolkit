using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Hardware.Info;
using SystemToolkit.Core.Overview.Models;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>
/// 硬件信息采集（OverviewService 的硬件半部）：
/// 顶部统计卡（大数字 + 进度条） + 键值行详情卡。全部走 TryRefresh 分区容错。
/// </summary>
public sealed partial class OverviewService
{
    private void CollectHardware(OverviewData data, SensorSnapshot? sensors)
    {
        // ============ 顶部统计卡：大数字 + 进度条（使用率/占用/容量） ============

        // ---- 处理器：统计卡（使用率 + 温度注解） + 详情卡（型号 / 温度 / 核心线程 / 主频 / 三缓 / 架构） ----
        TryRefresh(() => _hw.RefreshCPUList());

        CPU? cpu = _hw.CpuList.FirstOrDefault();
        if (cpu is not null)
        {
            string cpuName = string.IsNullOrWhiteSpace(cpu.Name) ? "未知处理器" : OverviewFormat.CleanCpuName(cpu.Name);
            // 使用率：WMI formatted perf 优先，缺失时回退 LHM CPU Load（"Total"），
            // 两者都没有才算拿不到（统计卡回退温度顶位形态）
            // ⚠️ 已知取舍（审查 L11）：WMI 计数器不可用与空闲 0% 在源头无法区分（Hardware.Info
            // 以 0 填充缺失值），空闲 CPU 的 0% 会被当无数据而回退传感器值；如需根治须换采集源
            // 或自行维护"已取到值"标志，此处暂固化取舍。
            int? cpuPercent = cpu.PercentProcessorTime > 0 ? (int?)cpu.PercentProcessorTime : sensors?.CpuLoadPercent;
            string? coreText = cpu.NumberOfCores > 0 || cpu.NumberOfLogicalProcessors > 0
                ? $"{cpu.NumberOfCores} 核 {cpu.NumberOfLogicalProcessors} 线程"
                : null;
            string? clockText = cpu.MaxClockSpeed > 0 ? $"{cpu.MaxClockSpeed} MHz" : null;
            string? cacheText = OverviewFormat.CacheSize(cpu.L3CacheSize);
            float? cpuTemp = sensors?.CpuTempC;
            string? tempText = cpuTemp is null ? null : $"{cpuTemp:0} °C";

            if (cpuPercent is not null || tempText is not null)
            {
                // 统计卡大数字优先使用率，无使用率数据时用温度顶位（保证 CPU 统计卡始终有内容）
                string bigValue = cpuPercent is not null ? $"{cpuPercent}%" : tempText!;
                // 副文案温度优先（2026-08-29 用户指定），不再与核心线程拼接；
                // 温度依赖 WinRing0 驱动（需管理员权限），取不到时回退核心线程避免空白。
                // 核心线程的完整展示保留在下方详情卡「核心 / 线程」行
                string? note = cpuTemp is not null ? $"温度 {tempText}" : coreText;
                data.HardwareStats.Add(new OverviewItem("\uE950", "处理器", bigValue,
                    note, percent: cpuPercent,
                    percentLabel: cpuPercent is null ? null : "实时使用率"));
            }

            var cpuRows = new List<OverviewRow> { new("型号", cpuName) };
            if (tempText is not null)
            {
                cpuRows.Add(new OverviewRow("温度", tempText));
            }
            if (coreText is not null)
            {
                cpuRows.Add(new OverviewRow("核心 / 线程", coreText));
            }
            if (clockText is not null)
            {
                cpuRows.Add(new OverviewRow("主频", clockText));
            }
            if (cacheText is not null)
            {
                cpuRows.Add(new OverviewRow("三缓", cacheText));
            }
            cpuRows.Add(new OverviewRow("架构", RuntimeInformation.ProcessArchitecture.ToString()));
            data.Hardware.Add(new OverviewItem("\uE950", "处理器", rows: cpuRows));
        }

        // ---- 内存：统计卡（占用率） + 详情卡（总容量 / 插槽摘要） ----
        TryRefresh(() => _hw.RefreshMemoryStatus());
        TryRefresh(() => _hw.RefreshMemoryList());

        ulong totalRam = _hw.MemoryStatus?.TotalPhysical ?? 0;
        ulong availableRam = _hw.MemoryStatus?.AvailablePhysical ?? 0;
        var sticks = _hw.MemoryList
            .Where(m => m.Capacity > 0)
            .ToList();
        int? memUsedPercent = null;
        if (totalRam > 0)
        {
            ulong usedRam = totalRam > availableRam ? totalRam - availableRam : 0;
            memUsedPercent = OverviewFormat.Percent(usedRam, totalRam) is { } p ? int.Parse(p.TrimEnd('%')) : null;
            if (memUsedPercent is not null)
            {
                // 副文案必须与左侧大数字同口径：「已用/总量」。
                // 原先写「可用/总量」，排在 59% 下面会被读成已用率（13.1/31.8≈41% ≠ 59%）自相矛盾
                //（2026-08-30 截图审计）
                data.HardwareStats.Add(new OverviewItem("\uE964", "内存", $"{memUsedPercent}%",
                    $"已用 {OverviewFormat.Bytes(usedRam)} / {OverviewFormat.Bytes(totalRam)}",
                    percent: memUsedPercent, percentLabel: "内存占用"));
            }
        }

        // ---- 显卡：统计卡（负载/温度，需传感器数据） + 每块一张详情卡（型号 / 温度 / 显存 / 分辨率 / 驱动） ----
        TryRefresh(() => _hw.RefreshVideoControllerList());

        int? gpuLoad = sensors?.GpuLoadPercent;
        float? gpuTemp = sensors?.GpuTempC;
        if (gpuLoad is not null || gpuTemp is not null)
        {
            // 参考图布局：GPU 统计卡紧跟 CPU 之后（第二位），有负载画进度条，无负载用温度顶位
            string bigValue = gpuLoad is not null ? $"{gpuLoad}%" : $"{gpuTemp:0} °C";
            // 审查 O8（2026-09-10）：CPU/内存卡都可能缺席，Insert(1) 会越界——按现有卡数插到第 1 位之后
            data.HardwareStats.Insert(Math.Min(1, data.HardwareStats.Count), new OverviewItem("\uE95D", "显卡", bigValue,
                gpuTemp is null ? null : $"温度 {gpuTemp:0} °C",
                percent: gpuLoad, percentLabel: gpuLoad is null ? null : "显卡负载"));
        }

        var gpus = _hw.VideoControllerList.Where(g => !string.IsNullOrWhiteSpace(g.Name)).ToList();
        for (int i = 0; i < gpus.Count; i++)
        {
            VideoController gpu = gpus[i];
            var gpuRows = new List<OverviewRow> { new("型号", gpu.Name!.Trim()) };
            // 厂商：Hardware.Info 无 AdapterCompatibility 字段，从名称推断（推断不出不显示）
            string? vendor = OverviewFormat.GpuVendor(gpu.Name);
            if (vendor is not null)
            {
                gpuRows.Add(new OverviewRow("厂商", vendor));
            }
            if (i == 0 && gpuTemp is not null)
            {
                gpuRows.Add(new OverviewRow("温度", $"{gpuTemp:0} °C"));
            }
            if (i == 0 && gpuLoad is not null)
            {
                gpuRows.Add(new OverviewRow("负载", $"{gpuLoad}%"));
            }
            if (gpu.AdapterRAM > 0)
            {
                gpuRows.Add(new OverviewRow("显存", OverviewFormat.Bytes(gpu.AdapterRAM)));
            }
            if (gpu.CurrentHorizontalResolution > 0 && gpu.CurrentVerticalResolution > 0)
            {
                gpuRows.Add(new OverviewRow("分辨率",
                    OverviewFormat.Resolution(gpu.CurrentHorizontalResolution, gpu.CurrentVerticalResolution, gpu.CurrentRefreshRate)));
            }
            if (!string.IsNullOrWhiteSpace(gpu.DriverVersion))
            {
                gpuRows.Add(new OverviewRow("驱动版本", gpu.DriverVersion.Trim()));
            }
            data.Hardware.Add(new OverviewItem("\uE95D", "显卡", rows: gpuRows));
        }

        // ---- 存储：统计卡（使用率 + 进度条，与处理器/内存/显卡同构） + 详情卡（每块盘一行） ----
        TryRefresh(() => _hw.RefreshDriveList());

        var drives = _hw.DriveList.Where(d => d.Size > 0).ToList();
        if (drives.Count > 0)
        {
            long totalBytes = drives.Sum(d => (long)d.Size);
            // 统计卡单一来源 = DriveInfo 汇总本地固定分区：大数字（占用率）/ 副文案
            // （已用/总量）/ 进度条三处同源，不再混用 LHM Used Space（口径是多盘最大值，
            // 与合计占用率不是一回事——同源纪律见 2026-08-30 内存卡审计）。
            int? storageUsed = null;
            string storageSub = $"{drives.Count} 块磁盘";
            try
            {
                var vols = DriveInfo.GetDrives()
                    .Where(v => v.DriveType == DriveType.Fixed && v.IsReady)
                    .ToList();
                long volTotal = vols.Sum(v => v.TotalSize);
                long volFree = vols.Sum(v => v.AvailableFreeSpace);
                if (volTotal > 0)
                {
                    long volUsed = volTotal - volFree;
                    storageUsed = OverviewFormat.Percent((ulong)volUsed, (ulong)volTotal) is { } p
                        ? int.Parse(p.TrimEnd('%'))
                        : null;
                    storageSub = $"已用 {OverviewFormat.Bytes((ulong)volUsed)} / {OverviewFormat.Bytes((ulong)volTotal)}";
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("磁盘占用统计失败（DriveInfo 不可用），已回退容量展示：" + ex.Message);
            }

            // 进度条标签必须在兜底之后计算：此前定格在兜底前，DriveInfo 路径算出了
            // 占用率但 label 仍为 null，进度条整组隐藏（2026-08-30 用户实测反馈）
            string? storageLabel = storageUsed is null ? null : "磁盘占用";

            // 类型+容量汇总（2026-09-05 用户需求：统计卡副标题由"首块盘型号"改为"SSD 1 TB" /
            // "SSD … + HDD …"形态）。介质来源：MSFT_PhysicalDisk（权威）→ 型号名启发式 → 未知。
            Dictionary<string, DiskMediaInfo> mediaByDisk = QueryPhysicalDiskMediaTypes();
            data.StorageSummary = OverviewFormat.StorageSummary(
                drives.Select(d => (
                    IsSsd: ResolveMediaType(mediaByDisk.GetValueOrDefault(d.Index.ToString()), d.Model),
                    SizeBytes: (ulong)d.Size
                )).ToList());

            data.HardwareStats.Add(new OverviewItem("\uE958", "存储",
                storageUsed is not null ? $"{storageUsed}%" : OverviewFormat.Bytes((ulong)totalBytes),
                storageSub,
                percent: storageUsed, percentLabel: storageLabel));

            var driveRows = drives.Select((d, i) =>
            {
                string model = string.IsNullOrWhiteSpace(d.Model) ? "" : d.Model.Trim();
                string media = string.IsNullOrWhiteSpace(d.MediaType) ? "" : d.MediaType.Trim();
                string head = string.IsNullOrWhiteSpace(model) ? media : model;
                return new OverviewRow($"存储 {i + 1}",
                    string.IsNullOrWhiteSpace(head)
                        ? OverviewFormat.Bytes(d.Size)
                        : $"{head}（{OverviewFormat.Bytes(d.Size)}）");
            }).ToList();
            data.Hardware.Add(new OverviewItem("\uE958", "存储", rows: driveRows));
        }

        // ---- 内存详情卡放在存储卡之后（与统计行顺序呼应：处理器/内存/存储） ----
        // 行结构对齐参考设计（2026-09-02）：总容量 / 类型 / 频率 / 条数，各自独立成行
        if (totalRam > 0 || sticks.Count > 0)
        {
            var memRows = new List<OverviewRow>();
            if (totalRam > 0)
            {
                memRows.Add(new OverviewRow("总容量", OverviewFormat.Bytes(totalRam)));
            }
            if (sticks.Count > 0)
            {
                string? memType = sticks
                    .Select(m => OverviewFormat.MemoryTypeName((int)m.MemoryType))
                    .FirstOrDefault(t => t is not null);
                if (memType is not null)
                {
                    memRows.Add(new OverviewRow("类型", memType));
                }
                var validSpeeds = sticks.Select(m => (int)m.Speed).Where(s => s > 0).Distinct().OrderBy(s => s).ToList();
                if (validSpeeds.Count == 1)
                {
                    memRows.Add(new OverviewRow("频率", $"{validSpeeds[0]} MHz"));
                }
                else if (validSpeeds.Count > 1)
                {
                    memRows.Add(new OverviewRow("频率", string.Join("/", validSpeeds) + " MHz"));
                }
                memRows.Add(new OverviewRow("条数", sticks.Count.ToString()));
            }
            if (memRows.Count > 0)
            {
                data.Hardware.Add(new OverviewItem("\uE964", "内存", rows: memRows));
            }
        }

        // ---- 主板 / BIOS：纯键值详情卡 ----
        TryRefresh(() => _hw.RefreshMotherboardList());

        Motherboard? board = _hw.MotherboardList.FirstOrDefault();
        if (board is not null)
        {
            var boardRows = new List<OverviewRow>
            {
                new("型号", string.IsNullOrWhiteSpace(board.Product) ? "未知" : board.Product.Trim()),
            };
            if (!string.IsNullOrWhiteSpace(board.Manufacturer))
            {
                boardRows.Add(new OverviewRow("制造商", board.Manufacturer.Trim()));
            }
            data.Hardware.Add(new OverviewItem("\uE9F5", "主板", rows: boardRows));
        }

        TryRefresh(() => _hw.RefreshBIOSList());

        BIOS? bios = _hw.BiosList.FirstOrDefault();
        if (bios is not null)
        {
            var biosRows = new List<OverviewRow>
            {
                new("版本", string.IsNullOrWhiteSpace(bios.Version) ? "未知" : bios.Version.Trim()),
            };
            if (!string.IsNullOrWhiteSpace(bios.Manufacturer))
            {
                biosRows.Add(new OverviewRow("制造商", bios.Manufacturer.Trim()));
            }
            // WMI 日期原文形如 20241205000000.000000+000，需清洗为可读格式，无法解析则不显示
            string? biosDate = OverviewFormat.FormatWmiDate(bios.ReleaseDate);
            if (biosDate is not null)
            {
                biosRows.Add(new OverviewRow("日期", biosDate));
            }
            data.Hardware.Add(new OverviewItem("\uE943", "BIOS", rows: biosRows));
        }

        // ---- 网络：每块活跃物理网卡一张详情卡（型号 / 连接名称 / 适配器类型 / 链路速度） ----
        // 2026-09-02 重构：此前「一块卡每网卡一行」且过滤不严（vEthernet (Default Switch)
        // 这类不粘 virtual 关键字的虚拟适配器漏网），网卡卡出现一堆干扰行；
        // 现在过滤收紧（见 IsUserFacingAdapter）+ 每卡结构化行（对齐参考设计）
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsActivePhysicalAdapter)
                .GroupBy(n => n.Description.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .ToList();

            foreach (NetworkInterface n in adapters)
            {
                string model = string.IsNullOrWhiteSpace(n.Description) ? n.Name : n.Description.Trim();
                string kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "以太网";
                var rows = new List<OverviewRow> { new("型号", model) };
                rows.Add(new OverviewRow("连接名称", string.IsNullOrWhiteSpace(n.Name) ? "未知" : n.Name));
                rows.Add(new OverviewRow("适配器类型", kind));
                string? speed = OverviewFormat.BitsPerSecond(n.Speed);
                rows.Add(new OverviewRow("链路速度", speed ?? "未知"));
                data.Hardware.Add(new OverviewItem("\uE968", "网卡", rows: rows));
            }
        }
        catch (Exception ex)
        {
            // 审查 2026-09-04（P2）：留痕，不再静默
            _logger.Warn("概览网卡信息采集失败：" + ex.Message);
        }

        // ---- 声卡：每设备一张卡（型号 / 制造商），过滤虚拟音频设备（Nahimic / NVIDIA Virtual 等） ----
        TryRefresh(() => _hw.RefreshSoundDeviceList());

        foreach (SoundDevice snd in _hw.SoundDeviceList.Where(s => IsUserFacingSoundDevice(s.Name)))
        {
            var rows = new List<OverviewRow> { new("型号", snd.Name!.Trim()) };
            if (!string.IsNullOrWhiteSpace(snd.Manufacturer))
            {
                rows.Add(new OverviewRow("制造商", snd.Manufacturer!.Trim()));
            }
            data.Hardware.Add(new OverviewItem("\uE767", "声卡", rows: rows));
        }

        // ---- 显示器：每屏一张卡（型号 / 制造商）；分辨率/刷新率取显示输出，
        //      仅给第一块屏（多屏时输出与显示器的对应关系不可靠，宁缺毋滥） ----
        TryRefresh(() => _hw.RefreshMonitorList());

        var monitors = _hw.MonitorList
            .Where(m => !string.IsNullOrWhiteSpace(m.MonitorType)
                        || !string.IsNullOrWhiteSpace(m.UserFriendlyName)
                        || !string.IsNullOrWhiteSpace(m.MonitorManufacturer))
            .ToList();
        for (int i = 0; i < monitors.Count; i++)
        {
            Hardware.Info.Monitor mon = monitors[i];
            string model = !string.IsNullOrWhiteSpace(mon.UserFriendlyName) ? mon.UserFriendlyName!.Trim()
                : !string.IsNullOrWhiteSpace(mon.MonitorType) ? mon.MonitorType!.Trim()
                : "未知显示器";
            var rows = new List<OverviewRow> { new("型号", model) };
            if (!string.IsNullOrWhiteSpace(mon.MonitorManufacturer))
            {
                rows.Add(new OverviewRow("制造商", mon.MonitorManufacturer!.Trim()));
            }
            if (i == 0)
            {
                VideoController? video = _hw.VideoControllerList.FirstOrDefault(v => v.CurrentHorizontalResolution > 0);
                if (video is not null && video.CurrentVerticalResolution > 0)
                {
                    rows.Add(new OverviewRow("分辨率",
                        OverviewFormat.Resolution(video.CurrentHorizontalResolution, video.CurrentVerticalResolution, video.CurrentRefreshRate)));
                }
            }
            data.Hardware.Add(new OverviewItem("\uE7F4", "显示器", rows: rows));
        }
    }
    /// <summary>物理盘介质信息（MSFT_PhysicalDisk，ROOT\Microsoft\Windows\Storage）。
    /// 键 = 磁盘序号字符串（DeviceId，与 Hardware.Info Drive.Index 对应）。
    /// ⚠️ Intel RST/VMD 平台实测（2026-09-05，i7-12700H 双 NVMe）：MediaType 一律误报为 4（HDD）、
    /// BusType=17（NVMe）、SpindleSpeed=0——判定必须走 <see cref="ResolveMediaType"/> 多信号链，不可单信 MediaType。
    /// 查询失败返回空字典，调用方走型号启发式回退。</summary>
    private Dictionary<string, DiskMediaInfo> QueryPhysicalDiskMediaTypes()
    {
        var result = new Dictionary<string, DiskMediaInfo>(StringComparer.Ordinal);
        try
        {
            using System.Management.ManagementObjectSearcher searcher = new(
                @"ROOT\Microsoft\Windows\Storage",
                "SELECT DeviceId, MediaType, BusType, SpindleSpeed FROM MSFT_PhysicalDisk");
            foreach (System.Management.ManagementBaseObject disk in searcher.Get())
            {
                string? key = disk["DeviceId"]?.ToString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                result[key] = new DiskMediaInfo(
                    IsSsdFromMediaType: ParseMediaType(disk["MediaType"]),
                    BusType: ParseUint(disk["BusType"]),
                    SpindleSpeed: ParseUint(disk["SpindleSpeed"]));
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("物理盘介质类型查询失败（存储汇总将回退型号启发式）：" + ex.Message);
        }

        return result;
    }

    /// <summary>MediaType 取值兼容：数值枚举（3=SSD 4=HDD）与字符串两种 WMI 提供方形态。</summary>
    private static bool? ParseMediaType(object? raw)
    {
        try
        {
            return Convert.ToUInt32(raw, System.Globalization.CultureInfo.InvariantCulture) switch
            {
                3 => true,
                4 => false,
                _ => null,
            };
        }
        catch
        {
            return raw?.ToString() switch
            {
                "SSD" => true,
                "HDD" => false,
                _ => null,
            };
        }
    }

    /// <summary>WMI 数值列宽容解析（对象可能装箱成 uint/ushort/string 等形态）；失败 null。</summary>
    private static uint? ParseUint(object? raw)
    {
        try
        {
            return Convert.ToUInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>介质类型多信号判定（按可靠性排序，RST/VMD 误报实测修正）：
    /// ① BusType=NVMe(17) → SSD（NVMe 必为固态）；
    /// ② SpindleSpeed 已知（非 0xFFFFFFFF 未知哨兵）→ 0=SSD / &gt;0=HDD；
    /// ③ MediaType 3=SSD / 4=HDD（仅当①②不可用时兜底）；
    /// ④ 型号名启发式（NVMe/SSD/HDD 关键词）；⑤ null。</summary>
    internal static bool? ResolveMediaType(DiskMediaInfo info, string model)
    {
        if (info.BusType is 17)
        {
            return true;
        }

        if (info.SpindleSpeed is { } rpm && rpm != 0xFFFFFFFF)
        {
            return rpm == 0;
        }

        if (info.IsSsdFromMediaType is { } byMedia)
        {
            return byMedia;
        }

        if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
            || model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return model.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? false : null;
    }
}
