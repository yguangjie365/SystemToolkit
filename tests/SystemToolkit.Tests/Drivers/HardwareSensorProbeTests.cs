using SystemToolkit.Core.Overview.Services;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 传感器温度选值口径守卫（2026-09-05 用户反馈：瞬时 Core Max 单发 94°C 被当"处理器温度"，
/// 与 AIDA64 封装温度对拍"不准"）。节点名/取值来自真机探针实测（i7-12700H + LHM 0.9.6 + PawnIO）。
/// </summary>
public class HardwareSensorProbeTests
{
    // 真机探针实测的 LHM 0.9.6 节点名（i7-12700H，混合架构）
    private static readonly (string Name, float Value)[] AdlRealNodes =
    {
        ("Core Max", 68),
        ("Core Average", 60.4f),
        ("P-Core #1", 64),
        ("P-Core #2", 62),
        ("P-Core #3", 68),
        ("E-Core #1", 57),
        ("E-Core #8", 57),
        ("CPU Package", 68),
        ("P-Core #1 Distance to TjMax", 36),
        ("E-Core #1 Distance to TjMax", 43),
    };

    [Fact]
    public void SelectTemperature_PrefersCoreAverage_BeforeInstantaneousSpikes()
    {
        // 真机节点：瞬时 Core Max/Package 会到 90+，Average 稳定——展示口径取 Average
        (string Name, float Value)[] sensors = AdlRealNodes.Append(("Core Max", 94f)).ToArray();

        float? selected = HardwareSensorProbe.SelectTemperature(sensors);

        Assert.Equal(60.4f, selected);
    }

    [Fact]
    public void SelectTemperature_NoAverageNode_FallsBackToPackageOrCoreMax()
    {
        (string Name, float Value)[] sensors = { ("Core Max", 68f), ("P-Core #1", 64f), ("E-Core #1", 57f) };

        Assert.Equal(68f, HardwareSensorProbe.SelectTemperature(sensors));

        (string Name, float Value)[] packageOnly = { ("CPU Package", 65f), ("P-Core #1", 70f) };
        Assert.Equal(65f, HardwareSensorProbe.SelectTemperature(packageOnly));
    }

    [Fact]
    public void SelectTemperature_NoWholePackageNode_FallsBackToMaxCore()
    {
        (string Name, float Value)[] sensors = { ("P-Core #1", 64f), ("P-Core #2", 71f), ("E-Core #1", 57f) };

        Assert.Equal(71f, HardwareSensorProbe.SelectTemperature(sensors));
    }

    [Fact]
    public void SelectTemperature_GpuNode_GpuCorePreferred_HotSpotExcluded()
    {
        (string Name, float Value)[] gpu = { ("GPU Core", 62f), ("GPU Hot Spot", 67.2f) };

        Assert.Equal(62f, HardwareSensorProbe.SelectTemperature(gpu));
    }

    [Fact]
    public void SelectTemperature_TctlNode_AmdFallbackWorks()
    {
        (string Name, float Value)[] amd = { ("Tctl", 74f), ("Core #1", 70f) };

        Assert.Equal(74f, HardwareSensorProbe.SelectTemperature(amd));
    }

    [Fact]
    public void BuildDiagnostics_CpuVisibleButTempMissing_HintsPawnIo()
    {
        SensorSnapshot snapshot = new() { Sensors = new List<SensorReading>() };

        IReadOnlyList<string> hints = HardwareSensorProbe.BuildDiagnostics(
            cpuSeen: true, gpuSeen: true, storageSeen: true, memorySeen: true, snapshot);

        // storageSeen=true 但无占用读数时会有第二条提示——这里只断言温度缺失的提示指向 PawnIO
        Assert.Contains(hints, h => h.Contains("PawnIO"));
    }

    [Fact]
    public void BuildDiagnostics_MemoryVisibleButDimmTempMissing_HintsElevationAndTsod()
    {
        SensorSnapshot snapshot = new() { MemoryTemps = new List<NamedTemperature>() };

        IReadOnlyList<string> hints = HardwareSensorProbe.BuildDiagnostics(
            cpuSeen: true, gpuSeen: true, storageSeen: true, memorySeen: true, snapshot);

        Assert.Contains(hints, h => h.Contains("DIMM 温度不可用"));
    }

    [Fact]
    public void BuildDiagnostics_CpuNotDetected_HintsUnavailable()
    {
        SensorSnapshot snapshot = new();

        IReadOnlyList<string> hints = HardwareSensorProbe.BuildDiagnostics(
            cpuSeen: false, gpuSeen: false, storageSeen: false, memorySeen: false, snapshot);

        Assert.Contains(hints, h => h.Contains("未识别到 CPU 硬件"));
    }

    [Theory]
    [InlineData("DIMM #0", true)]
    [InlineData("DIMM #2", true)]
    [InlineData("Thermal Sensor High Limit", false)]
    [InlineData("Temperature Sensor Resolution", false)]
    [InlineData("Thermal Sensor Critical High Limit", false)]
    public void IsDimmReading_OnlyActualDimmReadings_ExcludesThresholdAndMetadata(string name, bool expected)
    {
        Assert.Equal(expected, HardwareSensorProbe.IsDimmReading(name));
    }

    [Theory]
    [InlineData("Composite Temperature", true)]
    [InlineData("Temperature #1", true)]
    [InlineData("Temperature #2", true)]
    [InlineData("Warning Temperature", false)]
    [InlineData("Critical Temperature", false)]
    public void IsDriveTemperatureReading_ExcludesThresholdNodes_KeepsActualReadings(string name, bool expected)
    {
        Assert.Equal(expected, HardwareSensorProbe.IsDriveTemperatureReading(name));
    }

    [Fact]
    public void SelectMaxDriveTemperature_TwoDrives_TakesHighestComposite()
    {
        // 真机读数（2026-09-05 探针）：MZVL 复合 49 / 990 PRO 复合 47
        var temps = new List<NamedTemperature>
        {
            new("SAMSUNG MZVL2512HCJQ-00BL2", "Composite Temperature", 49),
            new("SAMSUNG MZVL2512HCJQ-00BL2", "Temperature #1", 48.9f),
            new("SAMSUNG MZVL2512HCJQ-00BL2", "Temperature #2", 53.9f),
            new("Samsung SSD 990 PRO 1TB", "Composite Temperature", 47),
            new("Samsung SSD 990 PRO 1TB", "Temperature #2", 50.9f),
        };

        Assert.Equal(49f, HardwareSensorProbe.SelectMaxDriveTemperature(temps));
    }

    [Fact]
    public void SelectMaxDriveTemperature_NoComposite_FallsBackToDriveMaxReading()
    {
        var temps = new List<NamedTemperature>
        {
            new("SATA 盘", "Temperature", 41f),
            new("SAMSUNG 990 PRO", "Temperature #2", 50.9f),
        };

        // SATA 盘 41 vs 990 PRO 部件 50.9 → 跨盘最高 50.9
        Assert.Equal(50.9f, HardwareSensorProbe.SelectMaxDriveTemperature(temps));
    }

    [Fact]
    public void SelectMaxDriveTemperature_EmptyList_ReturnsNull()
    {
        Assert.Null(HardwareSensorProbe.SelectMaxDriveTemperature([]));
    }

    // ---------------- 介质判定多信号链（2026-09-05 RST/VMD 误报实测修正） ----------------

    [Fact]
    public void ResolveMediaType_RstVmdPlatformNvmeMisreportedAsHdd_ClassifiedSsdByNvmeBus()
    {
        // 真机实测值：双 NVMe 均 MediaType=4(HDD 误报) / BusType=17(NVMe) / SpindleSpeed=0
        DiskMediaInfo info = new(IsSsdFromMediaType: false, BusType: 17, SpindleSpeed: 0);
        Assert.True(OverviewService.ResolveMediaType(info, "SAMSUNG MZVL2512HCJQ-00BL2"));
    }

    [Fact]
    public void ResolveMediaType_SataMechanicalWithRpm_ClassifiedHdd()
    {
        DiskMediaInfo info = new(IsSsdFromMediaType: null, BusType: 11, SpindleSpeed: 7200);
        Assert.False(OverviewService.ResolveMediaType(info, "WDC WD20EZAZ"));
    }

    [Fact]
    public void ResolveMediaType_SataSsdZeroRpm_ClassifiedSsd()
    {
        DiskMediaInfo info = new(IsSsdFromMediaType: true, BusType: 11, SpindleSpeed: 0);
        Assert.True(OverviewService.ResolveMediaType(info, "Samsung SSD 870 EVO"));
    }

    [Fact]
    public void ResolveMediaType_MediaUnknown_RotationalHintDecides()
    {
        Assert.False(OverviewService.ResolveMediaType(new DiskMediaInfo(null, 11, 7200), "Some Unknown Disk"));
        Assert.True(OverviewService.ResolveMediaType(new DiskMediaInfo(null, 11, 0), "Some Unknown Disk"));
    }

    [Fact]
    public void ResolveMediaType_AllSignalsUnknown_ModelHeuristicFallback()
    {
        DiskMediaInfo unknown = new(null, null, null);
        Assert.True(OverviewService.ResolveMediaType(unknown, "Samsung SSD 990 PRO 1TB"));
        Assert.Null(OverviewService.ResolveMediaType(unknown, "SAMSUNG MZVL2512HCJQ-00BL2"));
    }

    [Fact]
    public void ResolveMediaType_SpindleSpeedUnknownSentinel_SkipsRpmSignal()
    {
        DiskMediaInfo sentinel = new(IsSsdFromMediaType: true, BusType: 11, SpindleSpeed: 0xFFFFFFFF);
        Assert.True(OverviewService.ResolveMediaType(sentinel, "Some Disk"));
    }
}
