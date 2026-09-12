namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// 精选内置 OUI 表（MAC 前三字节 → 厂商）。
/// <para>
/// 🟡 定位为展示用途的「常见条目速查表」，不是 IEEE 全量注册表：只收录人工高置信条目，
/// 未命中一律返回 null（UI 降级显示"—"）。扩展走"追加字典条目 + 单测"即可，禁止引入
/// 来路不明的大表（许可证红线）。数据来源：公开 IEEE OUI 清单中的常见存量设备条目。
/// </para>
/// </summary>
public static class OuiTable
{
    private static readonly Dictionary<string, string> Oui = new(StringComparer.Ordinal)
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
    };

    /// <summary>
    /// MAC → 厂商标识。优先级：OUI 命中 → 厂商名；locally-administered 位（随机私有 MAC /
    /// 容器虚拟网卡）→「随机/虚拟 MAC」；否则 null（未识别）。
    /// </summary>
    public static string? Lookup(string normalizedMac)
    {
        if (string.IsNullOrEmpty(normalizedMac))
        {
            return null;
        }

        if (normalizedMac.Length >= 8
            && Oui.TryGetValue(normalizedMac[..8], out string? vendor))
        {
            return vendor;
        }

        return LanMac.IsLocallyAdministered(normalizedMac) ? "随机/虚拟 MAC" : null;
    }
}
