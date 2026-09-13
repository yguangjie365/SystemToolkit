using System.Text;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// OUI（MAC 前三字节 → 厂商）查询表，**两层结构**：
/// <list type="number">
/// <item><b>精选层</b>（<see cref="Curated"/>）——人工核实条目，**优先级高于官方层**。
/// 存在的两个理由：① 官方名是英文长串（中位 21 字符、最长 93），直接铺到 UI 列会溢出；
/// ② 中文名官方表根本给不出（"小米 Xiaomi" vs "Beijing Xiaomi Mobile Software Co., Ltd"）。
/// 新增条目仍须"核实来源 + 单测锁定"，**禁止凭记忆加**。</item>
/// <item><b>官方层</b>——IEEE 官方 OUI 注册表（MA-L）全量快照，嵌入资源 <c>oui-ieee.csv</c>，
/// 首次查询惰性解析。**只读、原样采用**：不做厂商名裁剪或改写（改写会产出"看起来像查错了"的名字）。</item>
/// </list>
/// <para>
/// 2026-09-13（落地计划 B3-①）从"仅精选表"升级为"两层"：原先未命中一律 <c>null</c>（覆盖率极低，
/// 实为 24 条），现覆盖 IEEE 全量约 4 万条。数据取自 IEEE 官方公开注册表（**属数据源更换，非第三方代码**），
/// 生成器与来源档案（URL/日期/体积/SHA-256/许可判断）见 <c>Tools/oui-gen/</c>，登记见 ADR-002 §5.3。
/// </para>
/// <para>
/// <b>负载实测</b>（B3-① 认领时用一次性探针测得）：解析 40,130 条 ≈ <b>11 ms</b>（读文件 8 ms + 入表 3 ms），
/// 托管分配 ≈ 5.4 MB；20 万次查找 ≈ 9 ms。故**不做裁剪**、不做预生成 .cs（后者源码约 1.98 MB，会拖慢编译并撑大程序集）。
/// </para>
/// </summary>
public static class OuiTable
{
    private const string ResourceName = "oui-ieee.csv";

    /// <summary>官方层容量提示（仅作字典初始容量，不参与正确性）。</summary>
    private const int ExpectedIeeeEntries = 40133;

    /// <summary>
    /// 官方层：首次查询时解析嵌入资源。条目数由 <see cref="IeeeEntryCount"/> 暴露，
    /// 构建期由 `LanScanTests.Oui_IeeeLayer_IsEmbeddedAndLoaded` 断言 ≥ 4 万——
    /// 即"资源没打进去"这种打包回归**在测试里就红**，不靠运行期静默降级暴露。
    /// </summary>
    private static readonly Lazy<Dictionary<string, string>> Ieee =
        new(LoadIeeeLayer, isThreadSafe: true);

    /// <summary>
    /// 精选层（**命中优先于官方层**）。数据来源：公开 IEEE OUI 清单中的常见存量设备条目；
    /// 2026-09-12 追加的三条经 maclookup.app 对 IEEE 注册表逐条核实，由
    /// `LanScanTests.Oui_VerifiedUserLanEntries_AreLocked` 锁死——新增条目必须同流程。
    /// </summary>
    private static readonly Dictionary<string, string> Curated = new(StringComparer.Ordinal)
    {
        // 虚拟化 / 容器（注意：Hyper-V/VMware/VirtualBox 均非随机位形态，注册 OUI 可辨识）
        ["00:0C:29"] = "VMware",
        ["00:05:69"] = "VMware",
        ["00:50:56"] = "VMware",
        ["08:00:27"] = "VirtualBox",
        ["00:15:5D"] = "Microsoft Hyper-V / WSL2",
        ["00:03:FF"] = "Microsoft 虚拟网卡",
        // 网卡芯片厂商（桌面机常见）
        ["00:E0:4C"] = "Realtek",
        // 网络设备 / 安防
        ["00:40:96"] = "Cisco",
        ["00:1A:1E"] = "Aruba (HPE)",
        ["00:E0:FC"] = "Huawei",
        ["C4:2F:90"] = "海康威视 Hikvision",
        ["04:18:D6"] = "Ubiquiti",
        // 历史 PC 厂商 OUI 段（存量设备）
        ["00:11:85"] = "Hewlett-Packard",
        ["00:15:60"] = "Hewlett-Packard",
        ["00:17:A4"] = "Hewlett-Packard",
        ["00:19:B9"] = "Hewlett-Packard",
        ["00:21:5A"] = "Hewlett-Packard",
        // 嵌入式 / IoT / 其他高置信条目
        ["B8:27:EB"] = "Raspberry Pi",
        ["DC:A6:32"] = "Raspberry Pi",
        ["00:17:88"] = "Philips (Hue)",
        ["00:1A:11"] = "Google",
        // 用户网段实需扩充（2026-09-12）：逐条经 maclookup.app 对 IEEE 注册表核实后入表
        ["68:AB:BC"] = "小米 Xiaomi",
        ["08:84:FB"] = "荣耀 Honor",
        ["10:82:3D"] = "锐捷 Ruijie",
    };

    /// <summary>
    /// 官方层已解析的条目数（<b>会触发惰性加载</b>）。供测试断言"资源确实打进程序集了"。
    /// </summary>
    public static int IeeeEntryCount => Ieee.Value.Count;

    /// <summary>
    /// MAC → 厂商标识。优先级：**精选层** → **IEEE 官方层** → locally-administered 位
    /// （随机私有 MAC / 容器虚拟网卡）→「随机/虚拟 MAC」→ 否则 <c>null</c>（UI 降级显示「—」）。
    /// </summary>
    /// <param name="normalizedMac">已归一化的 MAC（<see cref="LanMac.Normalize"/> 的冒号大写形态）。</param>
    public static string? Lookup(string normalizedMac)
    {
        if (string.IsNullOrEmpty(normalizedMac))
        {
            return null;
        }

        if (normalizedMac.Length >= 8)
        {
            string prefix = normalizedMac[..8];
            if (Curated.TryGetValue(prefix, out string? curatedName))
            {
                return curatedName;
            }

            // 官方层键不含分隔符（"AABBCC"）
            string ieeeKey = prefix.Replace(":", string.Empty, StringComparison.Ordinal);
            if (Ieee.Value.TryGetValue(ieeeKey, out string? ieeeName))
            {
                return ieeeName;
            }
        }

        return LanMac.IsLocallyAdministered(normalizedMac) ? "随机/虚拟 MAC" : null;
    }

    /// <summary>
    /// 解析嵌入的官方表。资源缺失时返回**空表**而非抛出：扫描页不该因为打包问题崩掉，
    /// 而"资源没打进去"由 <see cref="IeeeEntryCount"/> 的构建期断言兜住（不静默）。
    /// </summary>
    private static Dictionary<string, string> LoadIeeeLayer()
    {
        var map = new Dictionary<string, string>(ExpectedIeeeEntries, StringComparer.Ordinal);
        using Stream? stream = typeof(OuiTable).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return map;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            int separator = line.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }

            // IEEE 表内有 2 个前缀重复（080030×3、0001C8×2）→ 取**首条**：
            // 不猜"哪个才对"，只保证行为与文件顺序一致且可复现（由单测锁死）。
            map.TryAdd(line[..separator], line[(separator + 1)..]);
        }

        return map;
    }
}
