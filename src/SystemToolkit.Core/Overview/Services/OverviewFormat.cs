using SystemToolkit.Core.Overview.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>
/// 概览数据的纯格式化 / 过滤函数（不依赖 WMI 与注册表，可独立单测）。
/// </summary>
public static class OverviewFormat
{
    /// <summary>字节数格式化为可读文本（B/KB/MB/GB/TB/PB）——委托 FormatUtil 单一实现（审查 L2）。</summary>
    public static string Bytes(ulong bytes)
    {
        // clamp 防 ulong→long 溢出（>8 EB 不现实）
        return FormatUtil.FormatSize((long)Math.Min(bytes, long.MaxValue));
    }

    /// <summary>分辨率文本（宽 × 高[ · 刷新率 Hz]）——GPU 与显示器详情卡共用（审查 L8）。
    /// 形参 long：Hardware.Info 的分辨率/刷新率为 uint，隐式转换。</summary>
    public static string Resolution(long width, long height, long refreshRate)
    {
        string res = $"{width} × {height}";
        return refreshRate > 0 ? $"{res} · {refreshRate} Hz" : res;
    }

    /// <summary>存储统计卡副标题：按介质分组聚合容量的「类型+容量」汇总（2026-09-05 用户需求）。
    /// 单一介质 → "SSD 1 TB"；混合 → "SSD 476.9 GB + HDD 931.5 GB"；介质未知的前缀省略（只出容量）。
    /// 全部未知/空 → null（调用方回退旧行为）。容量取同介质各盘之和。</summary>
    internal static string? StorageSummary(IReadOnlyList<(bool? IsSsd, ulong SizeBytes)> drives)
    {
        if (drives.Count == 0)
        {
            return null;
        }

        ulong ssd = 0, hdd = 0, other = 0;
        bool hasSsd = false, hasHdd = false, hasOther = false;
        foreach ((bool? isSsd, ulong sizeBytes) in drives)
        {
            if (isSsd == true)
            {
                ssd += sizeBytes;
                hasSsd = true;
            }
            else if (isSsd == false)
            {
                hdd += sizeBytes;
                hasHdd = true;
            }
            else
            {
                other += sizeBytes;
                hasOther = true;
            }
        }

        var parts = new List<string>(3);
        if (hasSsd)
        {
            parts.Add($"SSD {Bytes(ssd)}");
        }

        if (hasHdd)
        {
            parts.Add($"HDD {Bytes(hdd)}");
        }

        if (hasOther)
        {
            parts.Add(Bytes(other));
        }

        return parts.Count == 0 ? null : string.Join(" + ", parts);
    }

    /// <summary>
    /// 从注册表 Uninstall 子键值构造已安装程序条目；命中系统组件 / MSI 子条目 /
    /// Windows 更新 / 无显示名等场景返回 null（应过滤）。
    /// </summary>
    public static InstalledProgram? BuildInstalledProgram(
        string? displayName,
        string? version,
        string? publisher,
        string? installDate,
        long? sizeBytes,
        int? systemComponent,
        string? parentKeyName,
        string? releaseType)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }
        if (systemComponent is 1)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(parentKeyName))
        {
            return null;
        }
        if (releaseType is "Update" or "Security Update" or "Hotfix" or "Tool")
        {
            return null;
        }

        string name = displayName.Trim();
        string? ver = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        string? pub = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim();
        return new InstalledProgram(
            name,
            ver,
            pub,
            FormatInstallDate(installDate),
            sizeBytes is > 0 ? Bytes((ulong)sizeBytes.Value) : null);
    }

    private static string? FormatInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        string s = raw.Trim();
        // 注册表 InstallDate 常见格式为 yyyyMMdd（DWORD），转为可读形式
        if (s.Length == 8 && int.TryParse(s, out _))
        {
            return $"{s[..4]}-{s[4..6]}-{s[6..8]}";
        }
        return s;
    }

    /// <summary>百分占比（used / total），total 为 0 时返回 null。</summary>
    public static string? Percent(ulong used, ulong total)
    {
        if (total == 0)
        {
            return null;
        }
        return $"{(int)Math.Round((double)used * 100.0 / total)}%";
    }

    /// <summary>运行时长格式化：“3 天 5 小时 12 分钟”。</summary>
    public static string Uptime(TimeSpan uptime)
    {
        var parts = new List<string>();
        if (uptime.Days > 0)
        {
            parts.Add($"{uptime.Days} 天");
        }
        if (uptime.Hours > 0)
        {
            parts.Add($"{uptime.Hours} 小时");
        }
        if (uptime.Minutes > 0)
        {
            parts.Add($"{uptime.Minutes} 分钟");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : "不足 1 分钟";
    }

    /// <summary>去掉名称开头的 “Microsoft ” 前缀（用于操作系统 Caption 美化）。</summary>
    public static string TrimMicrosoftPrefix(string name)
    {
        string s = name.Trim();
        const string prefix = "Microsoft ";
        return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? s[prefix.Length..].Trim()
            : s;
    }

    /// <summary>
    /// 清洗 CPU 型号名：去掉 WMI 原文里的商标标记 ——
    /// “12th Gen Intel(R) Core(TM) i7-12700H” → “12th Gen Intel Core i7-12700H”。
    /// </summary>
    public static string CleanCpuName(string name)
    {
        string cleaned = System.Text.RegularExpressions.Regex
            .Replace(name, @"\((R|TM|C)\)", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Replace('©', ' ');
        return string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 把多条内存插槽聚合成一行摘要（如 “DDR5 · 2 × 16 GB · 4800 MHz”）。
    /// 容量不同按 “16 GB + 8 GB” 展示；速度不一按 “4800/5600 MHz”。无可聚合信息返回 null。
    /// 【2026-09-02】内存详情卡改为「总容量/类型/频率/条数」分行展示后无生产调用点；
    /// 仅测试覆盖，保留以备复用（与电池方法同款处理）。
    /// </summary>
    [Obsolete("内存详情卡已改为分行展示（2026-09-02），该方法仅作为可复用保留项存在。", error: false)]
    public static string? MemorySticksSummary(
        string? memType, IReadOnlyList<ulong> capacities, IReadOnlyList<int> speeds)
    {
        if (capacities.Count == 0)
        {
            return null;
        }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(memType))
        {
            parts.Add(memType);
        }
        bool sameSize = capacities.All(c => c == capacities[0]);
        parts.Add(sameSize
            ? $"{capacities.Count} × {Bytes(capacities[0])}"
            : string.Join(" + ", capacities.Select(Bytes)));
        var validSpeeds = speeds.Where(s => s > 0).Distinct().OrderBy(s => s).ToList();
        if (validSpeeds.Count == 1)
        {
            parts.Add($"{validSpeeds[0]} MHz");
        }
        else if (validSpeeds.Count > 1)
        {
            parts.Add(string.Join("/", validSpeeds) + " MHz");
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>网络速度（bit/s）格式化为可读文本；0 或负值（未知，如 -1）返回 null。</summary>
    public static string? BitsPerSecond(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return null;
        }
        double value = bitsPerSecond;
        string[] units = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    /// <summary>WMI Win32_PhysicalMemory MemoryType 值映射为内存类型名；未知返回 null。</summary>
    public static string? MemoryTypeName(int memoryType)
    {
        return memoryType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            _ => null,
        };
    }

    /// <summary>
    /// 从显卡名称推断厂商（Hardware.Info 的 VideoController 无 AdapterCompatibility 字段）。
    /// 未识别返回 null（不猜）。
    /// </summary>
    public static string? GpuVendor(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
        {
            return null;
        }
        string n = gpuName;
        if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }
        if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc", StringComparison.OrdinalIgnoreCase) || n.Contains("Iris", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }
        return null;
    }

    /// <summary>
    /// WMI 缓存容量（KB 单位）格式化为可读文本；0/负值返回 null。
    /// （Win32_Processor 的 L2/L3CacheSize 以 KB 计。）
    /// </summary>
    public static string? CacheSize(uint? cacheKb)
    {
        if (cacheKb is not uint kb || kb == 0)
        {
            return null;
        }
        return kb >= 1024 * 1024
            ? $"{kb / (1024d * 1024d):0.#} GB"
            : $"{kb / 1024d:0.#} MB";
    }

    /// <summary>
    /// 迷你趋势图（Sparkline）样本追加：环形缓冲语义——超过容量时丢最旧样本。
    /// 纯函数便于单测；cap &lt;= 0 视为不保留（返回原样）。
    /// </summary>
    public static IReadOnlyList<double> AppendSample(IReadOnlyList<double>? samples, double value, int cap)
    {
        if (cap <= 0)
        {
            return samples ?? Array.Empty<double>();
        }
        var list = new List<double>(samples ?? Array.Empty<double>()) { value };
        if (list.Count > cap)
        {
            list.RemoveRange(0, list.Count - cap);
        }
        return list;
    }

    // ============================================================
    // 电池：格式化工具保留，但概览页已不再采集电池（2026-08-30 按用户要求
    // 移除电池统计卡与详情卡）。这两组方法目前是「死代码」——没有生产调用点，
    // 只有 OverviewFormatTests 覆盖。保留的原因：电池信息大概率要加回来，
    // 届时直接复用即可，不必重新推导 WMI 字段与文案映射。
    // 若要彻底清理，请一并删除 OverviewFormatTests 中的对应用例。
    // 【P3-9】编译期警告——生产代码若意外重新接入会得到清晰提示。
    // ============================================================

    /// <summary>电池健康度百分比（FullChargeCapacity / DesignCapacity）；任一为 0 返回 null。
    /// 【P3-9】概览页电池卡片已移除（2026-08-30），当前无生产调用点；仅测试覆盖，保留以备复用。</summary>
    [Obsolete("概览页电池详情卡已按用户要求移除（2026-08-30），该方法仅作为可复用保留项存在；" +
        "仅在重新接入电池 WMI 采集与详情卡 UI 时恢复调用，否则应避免在生产代码中引用。", error: false)]
    public static string? BatteryHealth(ulong fullChargeCapacity, ulong designCapacity)
    {
        if (fullChargeCapacity == 0 || designCapacity == 0)
        {
            return null;
        }
        return $"{(int)Math.Round((double)fullChargeCapacity * 100.0 / designCapacity)}%";
    }

    /// <summary>
    /// 解析 WMI 日期原文为 yyyy-MM-dd（如 <c>20241205000000.000000+000</c> → <c>2024-12-05</c>）。
    /// 非 WMI 格式或无法解析返回 null——原始调试文本不应上 UI。
    /// </summary>
    public static string? FormatWmiDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        string s = raw.Trim();
        // WMI DateTime 前 8 位为 yyyyMMdd，其后是时分秒微秒与时区偏移
        if (s.Length >= 8 && long.TryParse(s[..8], out _))
        {
            return $"{s[..4]}-{s[4..6]}-{s[6..8]}";
        }
        return null;
    }

    /// <summary>
    /// 将 WMI 电池状态英文长句映射为简短中文（如 "The system has access to AC…" → "使用外接电源"）。
    /// 未知名与空值返回 null——原始英文描述不应直接上 UI。
    /// 【P3-9】概览页电池卡片已移除（2026-08-30），当前无生产调用点；仅测试覆盖，保留以备复用。
    /// </summary>
    [Obsolete("概览页电池详情卡已按用户要求移除（2026-08-30），该方法仅作为可复用保留项存在；" +
        "仅在重新接入电池 WMI 采集与详情卡 UI 时恢复调用，否则应避免在生产代码中引用。", error: false)]
    public static string? BatteryStatusText(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }
        string d = description.Trim();
        if (d.Contains("access to AC", StringComparison.OrdinalIgnoreCase))
        {
            return "使用外接电源";
        }
        if (d.StartsWith("The battery is discharging", StringComparison.OrdinalIgnoreCase))
        {
            return "使用电池中";
        }
        if (d.StartsWith("The battery is charging", StringComparison.OrdinalIgnoreCase))
        {
            return "充电中";
        }
        if (d.StartsWith("The battery is fully charged", StringComparison.OrdinalIgnoreCase))
        {
            return "已充满";
        }
        return null;
    }
}
