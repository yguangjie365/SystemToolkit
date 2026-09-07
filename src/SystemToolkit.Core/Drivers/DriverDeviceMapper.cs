using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// 驱动包 ↔ 设备关联映射 v2（注册表全量方案，2026-09-04 替换 WMI）：
/// HKLM\SYSTEM\CurrentControlSet\Control\Class\{类GUID}\xxxx 子键即"已安装驱动实例"，
/// 每键含 DriverDesc（设备名）与 InfPath（INF 发布名/原始名）——
/// 覆盖完整（WMI Win32_PnPSignedDriver 对扩展驱动/部分设备大量漏配，用户实测反馈）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverDeviceMapper
{
    /// <summary>设备绑定明细（备份 devices.json / 恢复匹配用）。</summary>
    public sealed record DeviceBinding(string DeviceName, string? HardwareId);

    /// <summary>
    /// 采集设备绑定明细：inf 名 → (设备名, HardwareId=MatchingDeviceId) 列表。
    /// 与 BuildMap 共用 <see cref="EnumerateInstances"/> 遍历核心（审查 L3 去重），额外保留 HardwareId。
    /// 失败返回空映射，绝不抛出。
    /// </summary>
    public static Dictionary<string, List<DeviceBinding>> CollectDeviceBindings()
    {
        try
        {
            var map = new Dictionary<string, List<DeviceBinding>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string inf, string desc, string? hwId) in EnumerateInstances())
            {
                if (!map.TryGetValue(inf, out List<DeviceBinding>? list))
                {
                    map[inf] = list = new List<DeviceBinding>();
                }

                if (!list.Any(b => b.DeviceName == desc))
                {
                    list.Add(new DeviceBinding(desc, hwId));
                }
            }

            return map;
        }
        catch
        {
            return new Dictionary<string, List<DeviceBinding>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 从 (InfName, DeviceName) 原始对聚合映射：同 INF 多设备保持顺序去重。
    /// 抽成纯函数便于守卫测试（注册表遍历部分单独包裹）。
    /// </summary>
    public static Dictionary<string, List<string>> Accumulate(IEnumerable<(string? Inf, string? Device)> pairs)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string? inf, string? device) in pairs)
        {
            if (string.IsNullOrWhiteSpace(inf) || string.IsNullOrWhiteSpace(device))
            {
                continue;
            }

            if (!map.TryGetValue(inf, out List<string>? names))
            {
                map[inf] = names = new List<string>();
            }

            if (!names.Contains(device))
            {
                names.Add(device);
            }
        }

        return map;
    }

    /// <summary>全量遍历 Control\Class 子键（约数千键，亚秒级）；失败返回空映射（列显示"—"），绝不抛出。</summary>
    public static Dictionary<string, List<string>> BuildMap()
    {
        try
        {
            return Accumulate(EnumerateInstances().Select(t => (Inf: (string?)t.Inf, Device: (string?)t.Desc)));
        }
        catch
        {
            // 注册表不可读：设备关联列降级为"—"，不影响其它数据
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>共享遍历核心（审查 L3 去重）：全量遍历 Control\Class 实例键，
    /// 产出 (InfPath, DriverDesc, MatchingDeviceId)。单键读取失败跳过；根不可读返回空序列。</summary>
    private static IEnumerable<(string Inf, string Desc, string? HardwareId)> EnumerateInstances()
    {
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
        if (root is null)
        {
            yield break;
        }

        foreach (string classKey in root.GetSubKeyNames())
        {
            using RegistryKey? instances = root.OpenSubKey(classKey);
            if (instances is null)
            {
                continue;
            }

            foreach (string instance in instances.GetSubKeyNames())
            {
                // 迭代器限制：yield 不得位于带 catch 的 try 内——值读取放 try，yield 在外（CS1626）
                string? inf;
                string? desc;
                string? hwId;
                try
                {
                    using RegistryKey? key = instances.OpenSubKey(instance);
                    if (key is null)
                    {
                        continue;
                    }

                    inf = key.GetValue("InfPath") as string;
                    desc = key.GetValue("DriverDesc") as string;
                    hwId = key.GetValue("MatchingDeviceId") as string;
                }
                catch
                {
                    // 单个实例键读取失败不影响其余
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(inf) && !string.IsNullOrWhiteSpace(desc))
                {
                    yield return (inf, desc, hwId);
                }
            }
        }
    }
}
