using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>单块存储设备的健康读数摘要。</summary>
/// <param name="DeviceName">设备名（磁盘型号，来自 LHM 的硬件节点名）。</param>
/// <param name="TemperatureC">该盘最高温度（多温度传感器时取最热一处）；无温度读数时为 null。</param>
/// <param name="Attributes">除温度外的读数（SMART 属性 / 通电时间 / 写入量等），已按「类型 → 名称」排序。</param>
public sealed record StorageDeviceHealth(
    string DeviceName,
    float? TemperatureC,
    IReadOnlyList<SensorReading> Attributes)
{
    /// <summary>
    /// 本盘是否给出了 SMART 类属性（除温度外的读数）。
    /// 🔴 false 时界面必须**如实说明"本盘未提供"**，不得显示成"健康"——没有数据不等于一切正常。
    /// </summary>
    public bool HasAttributes => Attributes.Count > 0;
}

/// <summary>全部存储设备的健康摘要。</summary>
/// <param name="Devices">各存储设备（保持传感器清单里的出现顺序）。</param>
public sealed record StorageHealthSnapshot(IReadOnlyList<StorageDeviceHealth> Devices)
{
    /// <summary>是否有任意一块盘给出了 SMART 类属性（用于整卡文案：全部未提供时说明原因）。</summary>
    public bool HasAnyAttributes => Devices.Any(d => d.HasAttributes);
}

/// <summary>
/// 把传感器读数里的**存储设备**部分整理成健康摘要（供「磁盘健康」卡 / 详情弹窗 / 报告导出使用）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **为什么本类不自己读 SMART**：本仓已引入 LibreHardwareMonitor（MPL-2.0）并通过
/// <see cref="HardwareSensorProbe"/> 采集了**全部**读数，其中就包含存储设备的 SMART 属性
/// （实测扫程序集常量可见 <c>Smart Attributes:</c>、<c>ID, Description, Value, Threshold</c>、
/// <c>Data Written</c>、<c>Power On Count</c>、<c>Power On Hours</c>、<c>Composite Temperature</c> ——
/// 见变更记录）。自己再写一套 IOCTL/提权通道属于**重复实现**，且会多出一条未经真机验证的底层路径。
/// </para>
/// <para>
/// ⚠️ **已知不确定（如实声明）**：**提权运行时 LHM 到底给出哪些具体属性名，本环境未能实测**
/// （测试进程非提权，LHM 的内核驱动不加载，存储传感器一条都拿不到）。因此本类的属性筛选
/// **刻意不写白名单**：对未知名字**宽容收纳**（按类型与名称排序），宁可多列，不可漏报、更不可编造。
/// 等真机验收看到实际属性名后，再按需补"健康判定"。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StorageHealthBuilder
{
    /// <summary>整理存储设备健康摘要；输入为空返回空摘要（绝不抛出）。</summary>
    /// <param name="readings">传感器读数全集（<see cref="SensorSnapshot.Sensors"/>）。</param>
    public static StorageHealthSnapshot Build(IReadOnlyList<SensorReading>? readings)
    {
        if (readings is null || readings.Count == 0)
        {
            return new StorageHealthSnapshot(Array.Empty<StorageDeviceHealth>());
        }

        var byDevice = new Dictionary<string, List<SensorReading>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (SensorReading reading in readings)
        {
            if (!string.Equals(reading.HardwareType, nameof(HardwareType.Storage), StringComparison.Ordinal))
            {
                continue;
            }

            if (!byDevice.TryGetValue(reading.Hardware, out List<SensorReading>? list))
            {
                list = new List<SensorReading>();
                byDevice[reading.Hardware] = list;
                order.Add(reading.Hardware);
            }

            list.Add(reading);
        }

        var devices = new List<StorageDeviceHealth>(order.Count);
        foreach (string device in order)
        {
            List<SensorReading> all = byDevice[device];
            devices.Add(new StorageDeviceHealth(device, MaxTemperature(all), SelectAttributes(all)));
        }

        return new StorageHealthSnapshot(devices);
    }

    /// <summary>
    /// 盘温取该盘全部温度读数的最大值（多传感器时报告最热的一处）。
    /// 「是不是盘温读数」复用既有判据 <see cref="HardwareSensorProbe.IsDriveTemperatureReading"/>，
    /// **不另写一套**（同一判据两处各写是本仓踩过的坑）。
    /// </summary>
    /// <param name="deviceReadings">同一设备下的全部读数。</param>
    internal static float? MaxTemperature(IReadOnlyList<SensorReading> deviceReadings)
    {
        float? max = null;
        foreach (SensorReading reading in deviceReadings)
        {
            if (!string.Equals(reading.SensorType, nameof(SensorType.Temperature), StringComparison.Ordinal))
            {
                continue;
            }

            if (!HardwareSensorProbe.IsDriveTemperatureReading(reading.Name))
            {
                continue;
            }

            if (max is not { } current || reading.Value > current)
            {
                max = reading.Value;
            }
        }

        return max;
    }

    /// <summary>
    /// 除温度外的读数（SMART 属性 / 通电时间 / 写入量等）。按「类型 → 名称」排序：
    /// **稳定序**，既便于断言，也让界面刷新时行序不跳动。
    /// </summary>
    /// <param name="deviceReadings">同一设备下的全部读数。</param>
    internal static IReadOnlyList<SensorReading> SelectAttributes(IReadOnlyList<SensorReading> deviceReadings) =>
        deviceReadings
            .Where(r => !string.Equals(r.SensorType, nameof(SensorType.Temperature), StringComparison.Ordinal))
            .OrderBy(r => r.SensorType, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
}
