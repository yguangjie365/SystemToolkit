using SystemToolkit.Core.Overview.Services;
using LibreHardwareMonitor.Hardware;

namespace SystemToolkit.Tests;

/// <summary>
/// 存储健康摘要判据（B7c）。
/// <para>
/// 全部用例都是**离线构造读数**：因为存储/SMART 传感器只有在提权且 LHM 驱动加载时才拿得到，
/// 拿运行环境当夹具会让用例在 CI/非提权下静默失效。用例覆盖分组、非存储读数剔除、
/// 温度取最热（含阈值传感器排除）、无 SMART 属性时的诚实表达、稳定排序。
/// </para>
/// </summary>
public class StorageHealthBuilderTests
{
    [Fact]
    public void Build_NullOrEmpty_ReturnsEmptySnapshot()
    {
        Assert.Empty(StorageHealthBuilder.Build(null).Devices);
        Assert.Empty(StorageHealthBuilder.Build([]).Devices);
        Assert.False(StorageHealthBuilder.Build([]).HasAnyAttributes);
    }

    [Fact]
    public void Build_GroupsByDevice_PreservingFirstSeenOrder()
    {
        IReadOnlyList<SensorReading> readings =
        [
            Attr("Disk B", "Data", "Power On Hours", 1200),
            Attr("Disk A", "Data", "Data Written", 500),
            Attr("Disk B", "Level", "Percentage Used", 3),
        ];

        StorageHealthSnapshot snapshot = StorageHealthBuilder.Build(readings);

        Assert.Equal(new[] { "Disk B", "Disk A" }, snapshot.Devices.Select(d => d.DeviceName).ToArray());
        Assert.Equal(2, snapshot.Devices[0].Attributes.Count);
        Assert.Single(snapshot.Devices[1].Attributes);
    }

    [Fact]
    public void Build_IgnoresNonStorageReadings()
    {
        IReadOnlyList<SensorReading> readings =
        [
            new SensorReading("CPU Package", nameof(HardwareType.Cpu), "CPU Package", "Temperature", 55f, "°C"),
            new SensorReading("NVIDIA", nameof(HardwareType.GpuNvidia), "GPU Core", "Temperature", 60f, "°C"),
            new SensorReading("DIMM #0", nameof(HardwareType.Memory), "DIMM", "Temperature", 40f, "°C"),
            Attr("Disk A", "Data", "Data Written", 500),
        ];

        StorageHealthSnapshot snapshot = StorageHealthBuilder.Build(readings);

        Assert.Single(snapshot.Devices);
        Assert.Equal("Disk A", snapshot.Devices[0].DeviceName);
    }

    [Fact]
    public void Build_PerDeviceTemperature_IsHottestAmongDriveReadings()
    {
        IReadOnlyList<SensorReading> readings =
        [
            Temp("Disk A", "Composite Temperature", 41.5f),
            Temp("Disk A", "Temperature Sensor 1", 55.25f),
            Temp("Disk A", "Temperature Sensor 2", 33f),
            Temp("Disk B", "Composite Temperature", 30f),
        ];

        StorageHealthSnapshot snapshot = StorageHealthBuilder.Build(readings);

        Assert.Equal(55.25f, snapshot.Devices[0].TemperatureC);
        Assert.Equal(30f, snapshot.Devices[1].TemperatureC);
    }

    [Fact]
    public void Build_PerDeviceTemperature_IgnoresThresholdSensors()
    {
        // 真机实测 NVMe 会在同一节点混入 Warning/Critical Temperature 与 Resolution：都不算读数
        IReadOnlyList<SensorReading> readings =
        [
            Temp("Disk A", "Composite Temperature", 40f),
            Temp("Disk A", "Warning Temperature", 90f),
            Temp("Disk A", "Critical Temperature", 95f),
            Temp("Disk A", "Temperature Sensor Resolution", 1f),
        ];

        StorageHealthSnapshot snapshot = StorageHealthBuilder.Build(readings);

        Assert.Equal(40f, snapshot.Devices[0].TemperatureC);
    }

    [Fact]
    public void Build_DeviceWithOnlyTemperature_HasNoAttributes_ButKeepsTemperature()
    {
        // 🔴 关键诚实性用例：没有 SMART 属性 ≠ 健康。界面必须据 HasAttributes=false 说明"本盘未提供"。
        IReadOnlyList<SensorReading> readings = [Temp("Disk A", "Composite Temperature", 35f)];

        StorageHealthSnapshot snapshot = StorageHealthBuilder.Build(readings);

        StorageDeviceHealth device = snapshot.Devices[0];
        Assert.Equal(35f, device.TemperatureC);
        Assert.Empty(device.Attributes);
        Assert.False(device.HasAttributes);
        Assert.False(snapshot.HasAnyAttributes);
    }

    [Fact]
    public void Build_Attributes_SortedByTypeThenName()
    {
        IReadOnlyList<SensorReading> readings =
        [
            Attr("Disk A", "Level", "Percentage Used", 3),
            Attr("Disk A", "Data", "Power On Hours", 1200),
            Attr("Disk A", "Data", "Data Written", 500),
            Attr("Disk A", "Level", "Available Spare", 100),
        ];

        StorageDeviceHealth device = StorageHealthBuilder.Build(readings).Devices[0];

        Assert.Equal(
            new[] { "Data Written", "Power On Hours", "Available Spare", "Percentage Used" },
            device.Attributes.Select(a => a.Name).ToArray());
        Assert.True(device.HasAttributes);
    }

    [Fact]
    public void Build_TemperatureIsNotRepeatedInAttributes()
    {
        IReadOnlyList<SensorReading> readings =
        [
            Temp("Disk A", "Composite Temperature", 35f),
            Attr("Disk A", "Data", "Power On Count", 42),
        ];

        StorageDeviceHealth device = StorageHealthBuilder.Build(readings).Devices[0];

        Assert.Single(device.Attributes);
        Assert.Equal("Power On Count", device.Attributes[0].Name);
    }

    [Fact]
    public void Build_AttributesWithoutAnyTemperature_ReportsNullTemperature()
    {
        IReadOnlyList<SensorReading> readings = [Attr("Disk A", "Data", "Data Written", 500)];

        StorageDeviceHealth device = StorageHealthBuilder.Build(readings).Devices[0];

        Assert.Null(device.TemperatureC);
        Assert.True(device.HasAttributes);
    }

    private static SensorReading Temp(string device, string name, float value) =>
        new(device, nameof(HardwareType.Storage), name, nameof(SensorType.Temperature), value, "°C");

    private static SensorReading Attr(string device, string sensorType, string name, float value) =>
        new(device, nameof(HardwareType.Storage), name, sensorType, value, string.Empty);
}
