using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>带命名的温度读数（一条温度读数 + 其归属/来源标识；内存条与硬盘温度用）。</summary>
public sealed record NamedTemperature(string DeviceName, string SensorName, float TempC);

/// <summary>
/// 物理盘介质判定原始信号（MSFT_PhysicalDisk 三列，未做语义加工）。
/// 语义判定见 <c>OverviewService.ResolveMediaType</c>（多信号链：BusType NVMe → SpindleSpeed → MediaType → 型号启发式）。
/// </summary>
/// <param name="IsSsdFromMediaType">MediaType 列语义化：3=SSD(true) / 4=HDD(false) / 其余 null。⚠️ RST/VMD 平台会误报 4。</param>
/// <param name="BusType">总线类型：17=NVMe、11=SATA、8=RAID（Intel RST/VMD 平台 NVMe 常呈现为此）。</param>
/// <param name="SpindleSpeed">转速 RPM：0=无旋转轴（固态），&gt;0=机械盘，0xFFFFFFFF=未知。</param>
internal readonly record struct DiskMediaInfo(bool? IsSsdFromMediaType, uint? BusType, uint? SpindleSpeed);

/// <summary>
/// 单条传感器读数（NexBox monitor 同款数据形状：硬件名 + 类型 + 传感器名 + 类型 + 值 + 单位）。
/// 用于「传感器详情」弹窗与硬件信息报告导出。
/// </summary>
public sealed class SensorReading
{
    /// <summary>初始化一条传感器读数（六要素齐全，Value 已过滤 NaN/Infinity）。</summary>
    public SensorReading(string hardware, string hardwareType, string name, string sensorType, float value, string unit)
    {
        Hardware = hardware;
        HardwareType = hardwareType;
        Name = name;
        SensorType = sensorType;
        Value = value;
        Unit = unit;
    }

    /// <summary>所属硬件名（如 "Intel Core i7-12700H"、"NVIDIA GeForce RTX 3060"）。</summary>
    public string Hardware { get; }

    /// <summary>所属硬件类别（CPU / GpuNvidia / Storage …）。</summary>
    public string HardwareType { get; }

    /// <summary>传感器名（如 "Core #1"、"GPU Core"）。</summary>
    public string Name { get; }

    /// <summary>传感器类型（Temperature / Load / Power …）。</summary>
    public string SensorType { get; }

    /// <summary>读数（已过滤 NaN/Infinity）。</summary>
    public float Value { get; }

    /// <summary>单位（NexBox 同款映射：Temperature→°C、Load→%、Power→W …）。</summary>
    public string Unit { get; }
}

/// <summary>
/// 一次传感器探测的快照（全部可空：单项拿不到不影响其它项）。
/// CPU 温度依赖 LHM 的 WinRing0 内核驱动读 MSR —— 需管理员权限；
/// 非提权运行时 CPU 温度为 null（GPU 温度走 NVAPI/NVML 通常仍可读）。
/// </summary>
public sealed class SensorSnapshot
{
    /// <summary>CPU 温度（°C）；非提权运行时 LHM 读不到 MSR，为 null。</summary>
    public float? CpuTempC { get; set; }

    /// <summary>CPU 负载百分比（0-100，LHM Load 传感器优先 Total，兜底最大值）。</summary>
    public int? CpuLoadPercent { get; set; }

    /// <summary>GPU 温度（°C）；走 NVAPI/NVML 通常可读，拿不到为 null。</summary>
    public float? GpuTempC { get; set; }

    /// <summary>GPU 负载百分比（0-100）。</summary>
    public int? GpuLoadPercent { get; set; }

    /// <summary>存储已用空间百分比（取所有盘最大值）。</summary>
    public int? StorageUsedPercent { get; set; }

    /// <summary>内存条温度（DDR5 TSOD 读数，°C，按条）。需要 IsMemoryEnabled 且提权运行；拿不到为 null。
    /// 真机实测（2026-09-05）：与 AIDA64 的 DIMM 温度一致。</summary>
    public List<NamedTemperature>? MemoryTemps { get; set; }

    /// <summary>硬盘温度（°C，按盘；NVMe 含 Composite 与部件温度）。拿不到为 null。</summary>
    public List<NamedTemperature>? StorageTemps { get; set; }

    /// <summary>全部传感器读数（传感器详情弹窗 / 报告导出用；探测失败为 null）。</summary>
    public List<SensorReading>? Sensors { get; set; }
}

/// <summary>
/// LibreHardwareMonitorLib 传感器探测（NexBox monitor 同款方案）。
/// Open/Update 较慢（首次加载驱动约 1-3 秒），必须在后台线程调用；
/// 任何异常都折叠为返回 null——传感器属于增强信息，拿不到不阻断概览页。
/// 注意：驱动加载失败（非管理员/PawnIO 未安装/被安全软件拦截）时 Open() <b>不抛异常</b>，
/// 只是传感器全部为空——这类静默失效靠 <see cref="BuildDiagnostics"/> 留痕。
/// 鲁棒性借鉴 NexBox monitor（sidecar）的三点做法并落到进程内：
/// ① Open() 限时等待（驱动挂死不让采集整体卡死）；② NaN/Infinity 过滤；
/// ③ 递归更新 + 遍历子硬件、单硬件异常隔离。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareSensorProbe
{
    /// <summary>Open() 超时兜底（NexBox monitor 同款 15 秒）。</summary>
    internal const int OpenTimeoutMs = 15000;

    // 模块日志接入：驱动静默失效此前完全不可见，用户无从知道「为什么没温度」
    private readonly ILogger _logger;

    /// <summary>注入模块日志（驱动静默失效的留痕通道）；缺省为空实现。</summary>
    public HardwareSensorProbe(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>探测一次传感器快照；完全失败返回 null。</summary>
    public SensorSnapshot? Probe()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsMemoryEnabled = true, // DIMM TSOD 温度（2026-09-05：内存条温度需求，真机实测 LHM 0.9.6 支持 DDR5）
            IsMotherboardEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
            IsPsuEnabled = false,
        };
        if (!TryOpenWithTimeout(computer))
        {
            return null;
        }

        try
        {
            var snapshot = new SensorSnapshot();
            var readings = new List<SensorReading>();
            bool cpuSeen = false, gpuSeen = false, storageSeen = false, memorySeen = false;

            // 单个硬件的传感器枚举也包住：Update 成功不代表 Sensors 遍历安全
            void CollectFrom(IHardware node)
            {
                try
                {
                    switch (node.HardwareType)
                    {
                        case HardwareType.Cpu:
                            cpuSeen = true;
                            snapshot.CpuTempC ??= BestTemperature(node);
                            snapshot.CpuLoadPercent ??= BestLoad(node, preferTotal: true);
                            break;

                        case HardwareType.Memory:
                            memorySeen = true;
                            snapshot.MemoryTemps ??= new List<NamedTemperature>();
                            foreach (ISensor sensor in node.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Temperature
                                    && IsDimmReading(sensor.Name)
                                    && Sanitize(sensor.Value) is float v)
                                {
                                    snapshot.MemoryTemps.Add(new NamedTemperature(node.Name, sensor.Name, v));
                                }
                            }

                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                        case HardwareType.GpuIntel:
                            gpuSeen = true;
                            snapshot.GpuTempC ??= BestTemperature(node);
                            snapshot.GpuLoadPercent ??= BestLoad(node, preferTotal: false);
                            break;

                        case HardwareType.Storage:
                            storageSeen = true;
                            // LHM 每块盘给 "Used Space" 负载传感器；多盘取最大值（最紧张的一块）。
                            // 2026-09-02 起统计卡改为 DriveInfo 单一来源（与副文案同源，见
                            // OverviewService.CollectHardware），此处仅留作诊断与报告。
                            snapshot.StorageTemps ??= new List<NamedTemperature>();
                            foreach (ISensor sensor in node.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Temperature
                                    && IsDriveTemperatureReading(sensor.Name)
                                    && Sanitize(sensor.Value) is float v)
                                {
                                    snapshot.StorageTemps.Add(new NamedTemperature(node.Name, sensor.Name, v));
                                }

                                if (sensor.SensorType == SensorType.Load
                                    && sensor.Name.Contains("Used Space", StringComparison.OrdinalIgnoreCase)
                                    && Sanitize(sensor.Value) is float used)
                                {
                                    snapshot.StorageUsedPercent = Math.Max(snapshot.StorageUsedPercent ?? 0, (int)Math.Round(used));
                                }
                            }

                            break;
                    }

                    // 全量传感器清单（传感器详情弹窗 / 报告导出），NaN/Infinity 已过滤；
                    // 阈值型条目（High/Low/Critical Limit、Warning/Critical Temperature）不属于读数，排除
                    string typeName = node.HardwareType.ToString();
                    foreach (ISensor sensor in node.Sensors)
                    {
                        if (IsThresholdSensorName(sensor.Name))
                        {
                            continue;
                        }

                        if (Sanitize(sensor.Value) is float value)
                        {
                            readings.Add(new SensorReading(
                                node.Name, typeName,
                                string.IsNullOrWhiteSpace(sensor.Name) ? "未知传感器" : sensor.Name,
                                sensor.SensorType.ToString(), value, GetUnit(sensor.SensorType)));
                        }
                    }
                }
                catch
                {
                    // 单个硬件的传感器收集失败不影响其余硬件
                }

                // NexBox monitor 同款：子硬件递归遍历（部分传感器挂在子硬件节点上）
                foreach (IHardware sub in node.SubHardware)
                {
                    CollectFrom(sub);
                }
            }

            foreach (IHardware hardware in computer.Hardware)
            {
                SafeUpdate(hardware);
                CollectFrom(hardware);
            }

            snapshot.Sensors = readings;

            // 静默失效诊断：见 HardwareSensorProbeTests 的判定用例
            foreach (string hint in BuildDiagnostics(cpuSeen, gpuSeen, storageSeen, memorySeen, snapshot))
            {
                _logger.Warn(hint);
            }

            // 常驻打点：温度缺失时（驱动静默失效/无对应传感器）无需复现即可定位。
            // 管理员状态显式落盘——"以管理员运行 Run.bat"≠"子进程继承了提权 token"，用事实说话
            bool elevated = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            (int hvciRaw, int blocklistRaw) = ReadKernelPolicyRaw();
            _logger.Info(
                $"传感器探测完成：管理员={elevated}, " +
                $"策略注册表(HVCI={hvciRaw},阻止列表={blocklistRaw}), " +
                $"CPU可见={cpuSeen}(温度={snapshot.CpuTempC?.ToString("0") ?? "无"}), " +
                $"GPU可见={gpuSeen}(温度={snapshot.GpuTempC?.ToString("0") ?? "无"}), " +
                $"内存可见={memorySeen}(条温={snapshot.MemoryTemps?.Count ?? 0}), " +
                $"盘温={snapshot.StorageTemps?.Count ?? 0} 条, " +
                $"存储可见={storageSeen}, 读数={readings.Count} 条");
            return snapshot;
        }
        finally
        {
            try
            {
                computer.Close();
            }
            catch
            {
                // 关闭失败（驱动卸载异常）不影响已取得的快照
            }
        }
    }

    /// <summary>
    /// Open() 限时等待（NexBox monitor 同款方法：独立线程 + 15 秒兜底）。
    /// WinRing0/PawnIO 驱动在部分硬件上会无限期挂起，同步调用会把整个概览采集拖死
    /// （表现为页面永久转圈）。超时/失败一律返回 false，调用方回退到无传感器形态。
    /// </summary>
    private bool TryOpenWithTimeout(Computer computer)
    {
        var openTask = Task.Run(computer.Open);
        if (!openTask.Wait(OpenTimeoutMs))
        {
            _logger.Warn($"传感器驱动初始化超过 {OpenTimeoutMs / 1000} 秒未完成"
                + "（WinRing0/PawnIO 与本机硬件不兼容或被安全软件拦截），"
                + "本次跳过温度/负载/磁盘占用采集，概览卡片走回退形态");
            // Open 线程最终完成时补一次 Close 释放驱动句柄；再失败也无危害。
            // 审查 2026-09-04（P2）：Open 永久挂起（K-001 场景）时该任务本身也会挂住——
            // 给清理动作设 5s 上限，避免每次冷启动累积一个阻塞线程
            _ = Task.Run(async () =>
            {
                try
                {
                    await openTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    computer.Close();
                }
                catch
                {
                    // 超时后的清理尽力而为
                }
            });
            return false;
        }

        try
        {
            openTask.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("传感器驱动初始化失败（温度/负载卡片将显示回退形态）：" + ex.Message);
            try
            {
                computer.Close(); // 快速失败路径：避免部分初始化的 Computer 未释放句柄（审查 2026-09-08）
            }
            catch
            {
                // 部分初始化的 Computer 可能无句柄可释放，忽略
            }
            return false;
        }
    }

    /// <summary>递归刷新一块硬件及其全部子硬件；单节点失败只跳过该节点。</summary>
    private static void SafeUpdate(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // 单个硬件更新失败不影响其余硬件
        }

        foreach (IHardware sub in hardware.SubHardware)
        {
            SafeUpdate(sub);
        }
    }

    /// <summary>
    /// NaN/Infinity 过滤（NexBox monitor 同款方法）：LHM 个别传感器会给出
    /// NaN/±Inf，原样透传会把 "NaN °C" 渲染到统计卡。非法浮点一律视为无值。
    /// 纯函数、与 LHM 对象解耦，便于直接单测。
    /// </summary>
    internal static float? Sanitize(float? value)
        => value is float v && float.IsFinite(v) ? v : null;

    /// <summary>
    /// 把「硬件已启用但目标数据为空」翻译成可行动的日志提示（静默失效留痕，
    /// 2026-08-30 排查「处理器卡无温度」时确立：GPU 走 NVAPI 不经 WinRing0，
    /// 故 GPU 正常 ≠ 驱动正常，必须按数据源分别诊断）。
    /// 纯函数、与 LHM 对象解耦，便于直接单测。
    /// </summary>
    /// <summary>
    /// 检测会静默拦截 WinRing0 的内核安全策略（按嫌疑顺序）：
    /// ① HVCI 内存完整性；② Microsoft 易受攻击驱动阻止列表（不依赖 HVCI 单独生效，Win11 22H2+ 默认强制）。
    /// 命中返回描述；都未开启返回 null（则走"管理员/杀软"通用提示）。
    /// </summary>
    private static string? DetectKernelPolicyBlock()
    {
        (int hvciRaw, int blocklistRaw) = ReadKernelPolicyRaw();
        if (hvciRaw == 1)
        {
            return "内核隔离（内存完整性 / HVCI）";
        }

        if (blocklistRaw == 1)
        {
            return "Microsoft 易受攻击驱动阻止列表（Win11 22H2+ 默认开启，不依赖内核隔离单独生效）";
        }

        return null;
    }

    /// <summary>读取内核策略原始注册表值（-1 = 键不存在/读取失败），供日志对账——开关状态以注册表为准。</summary>
    internal static (int Hvci, int Blocklist) ReadKernelPolicyRaw()
    {
        int hvci = -1, blocklist = -1;
        try
        {
            using Microsoft.Win32.RegistryKey? hvciKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            if (hvciKey?.GetValue("Enabled") is int v)
            {
                hvci = v;
            }
        }
        catch
        {
            // 采集边界：读不到记 -1
        }

        try
        {
            using Microsoft.Win32.RegistryKey? ciKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CI\Config");
            if (ciKey?.GetValue("VulnerableDriverBlocklistEnable") is int b)
            {
                blocklist = b;
            }
        }
        catch
        {
        }

        return (hvci, blocklist);
    }

    internal static IReadOnlyList<string> BuildDiagnostics(
        bool cpuSeen, bool gpuSeen, bool storageSeen, bool memorySeen, SensorSnapshot snapshot)
    {
        var hints = new List<string>();
        if (!cpuSeen)
        {
            hints.Add("未识别到 CPU 硬件（LibreHardwareMonitor），处理器温度不可用");
        }
        else if (snapshot.CpuTempC is null)
        {
            // 2026-09-05 复诊（K-001）：LHM 0.9.6 起内置 WinRing0 已移除，CPU 温度经 PawnIO 内核驱动读取
            // （真机复现：管理员运行 + HVCI/阻止列表全关 + 未装 PawnIO → 温度节点整体缺席；装上后齐全）。
            // 旧文案"管理员/安全策略拦截 WinRing0"对 0.9.6 已误导。
            string policyNote = DetectKernelPolicyBlock() is { } policy
                ? $"（另检测到 {policy} 开启——仅在使用旧版自带驱动的方案时相关）"
                : "";
            hints.Add("CPU 硬件已识别但温度传感器全部为空——0.9.6 起 CPU 温度经 PawnIO 内核驱动读取："
                + "①确认已安装 PawnIO（github.com/namazso/PawnIO）；②以管理员身份运行本应用；"
                + "或接受\"未检测\"。" + policyNote);
        }
        if (!gpuSeen && snapshot.GpuTempC is null)
        {
            hints.Add("未识别到 GPU 硬件（LibreHardwareMonitor），显卡温度/负载不可用");
        }
        if (storageSeen && snapshot.StorageUsedPercent is null)
        {
            hints.Add("LHM 磁盘占用传感器不可用，存储卡将走 DriveInfo 兜底");
        }
        if (memorySeen && (snapshot.MemoryTemps is null || snapshot.MemoryTemps.Count == 0))
        {
            hints.Add("内存硬件已识别但 DIMM 温度不可用（需提权运行且内存条带 TSOD 温感；无 TSOD 的条子无此项）");
        }
        return hints;
    }

    /// <summary>取一块硬件上最可信的温度。展示口径（2026-09-05 用户反馈修正）：
    /// 优先 LHM 现成的「Core Average」全核平均——12 代混合架构上瞬时 Core Max 会随单核
    /// 冲高瞬间跳到 90°C+（40 秒实测 60↔72 波动、截图时刻 94），单发采样把它当"处理器温度"
    /// 会让卡片来回跳红，与 AIDA64 等参照工具的稳定读数也对不上；无 Average 节点的平台
    /// 回退 Package/Core Max/Tctl（整片温度），再回退逐核最大值。</summary>
    private static float? BestTemperature(IHardware hardware)
    {
        var sensors = new List<(string Name, float Value)>();
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && Sanitize(sensor.Value) is float v)
            {
                sensors.Add((sensor.Name, v));
            }
        }

        return SelectTemperature(sensors);
    }

    /// <summary>温度选值纯函数（与 LHM 对象解耦，便于直接单测）。
    /// 优先级：Core Average → Package/Core Max/Tctl/GPU Core（整片）→ 其余 Core/Temperature 节点最大值。</summary>
    internal static float? SelectTemperature(IReadOnlyList<(string Name, float Value)> sensors)
    {
        float? average = null, package = null, maxCore = null;
        foreach ((string name, float value) in sensors)
        {
            if (name.Contains("Core Average", StringComparison.OrdinalIgnoreCase))
            {
                average ??= value;
            }
            else if (name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Core Max", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
            {
                package ??= value;
            }
            else if (name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Temperature", StringComparison.OrdinalIgnoreCase))
            {
                maxCore = maxCore is null ? value : Math.Max(maxCore.Value, value);
            }
        }

        return average ?? package ?? maxCore;
    }

    /// <summary>
    /// 取一块硬件上的负载百分比：GPU 优先 "GPU Core / GPU Package"；CPU 优先 "Total"
    /// （LHM 的 CPU 负载有逐核值与 Total 值，整片负载应取 Total 而非逐核最大值）。
    /// 无优先项时取任意 Load 传感器最大值。
    /// </summary>
    private static int? BestLoad(IHardware hardware, bool preferTotal)
    {
        float? preferred = null;
        float? maxOther = null;
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Load || Sanitize(sensor.Value) is not float v)
            {
                continue;
            }
            bool isPreferred = preferTotal
                ? sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)
                : sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)
                  || sensor.Name.Contains("GPU Package", StringComparison.OrdinalIgnoreCase);
            if (isPreferred)
            {
                preferred ??= v;
            }
            else
            {
                maxOther = maxOther is null ? v : Math.Max(maxOther.Value, v);
            }
        }
        float? best = preferred ?? maxOther;
        return best is null ? null : (int)Math.Round(best.Value);
    }

    /// <summary>内存 TSOD 节点判定：仅 DIMM #N 实际读数——同节点混有 High/Low/Critical Limit、
    /// Sensor Resolution 等阈值/元数据型"温度"传感器（真机实测），必须排除。</summary>
    internal static bool IsDimmReading(string sensorName) =>
        sensorName.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase);

    /// <summary>硬盘实际温度读数判定：排除 Warning/Critical/Limit 阈值型节点（真机实测 NVMe 同节点混有）。</summary>
    internal static bool IsDriveTemperatureReading(string sensorName) =>
        !IsThresholdSensorName(sensorName);

    /// <summary>跨盘取徽章用最高温度：每盘优先 Composite Temperature（NVMe 标准复合温度），
    /// 缺失时回退该盘其余读数的最大值（部件温度）；再跨盘取最大。
    /// 输入应为已过 IsDriveTemperatureReading 过滤的读数。</summary>
    public static float? SelectMaxDriveTemperature(IReadOnlyList<NamedTemperature> temps)
    {
        float? max = null;
        foreach (IGrouping<string, NamedTemperature> drive in temps.GroupBy(t => t.DeviceName))
        {
            float? driveBest = drive.Where(t => t.SensorName == "Composite Temperature")
                    .Select(t => (float?)t.TempC)
                    .FirstOrDefault()
                ?? drive.Max(t => (float?)t.TempC);
            if (driveBest is { } v && (max is null || v > max))
            {
                max = v;
            }
        }

        return max;
    }

    /// <summary>阈值/元数据型传感器名判定（全量读数清单排除用）。</summary>
    internal static bool IsThresholdSensorName(string sensorName) =>
        sensorName.Contains("Limit", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Warning", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Critical", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Resolution", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 传感器类型 → 显示单位（NexBox monitor 同款映射表）。
    /// 纯函数、与 LHM 对象解耦，便于直接单测。
    /// </summary>
    internal static string GetUnit(SensorType type)
    {
        return type switch
        {
            SensorType.Voltage => "V",
            SensorType.Clock => "MHz",
            SensorType.Temperature => "°C",
            SensorType.Load => "%",
            SensorType.Control => "%",
            SensorType.Level => "%",
            SensorType.Humidity => "%",
            SensorType.Fan => "RPM",
            SensorType.Flow => "L/h",
            SensorType.Power => "W",
            SensorType.Data => "GB",
            SensorType.SmallData => "MB",
            SensorType.Throughput => "B/s",
            SensorType.Current => "A",
            SensorType.Energy => "mWh",
            SensorType.Noise => "dBA",
            SensorType.Frequency => "Hz",
            _ => "",
        };
    }
}
