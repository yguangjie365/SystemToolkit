using System.Net.NetworkInformation;

namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 网卡可见性判定（纯函数，跨域共用）：本仓两个消费方——
/// ① Overview 本机概览（结构化展示，只看已连接物理卡）；
/// ② NetManager 网络设置（适配器管理下拉，需保留禁用状态的物理卡以便启用）。
/// 黑名单来自实机实证（2026-09-02，Killer WLAN + Hyper-V + 蓝牙机型）：
/// Win32/BCL 枚举会混入过滤驱动子接口（QoS Packet Scheduler / WFP Native MAC Layer /
/// Native WiFi Filter，与物理卡同类型同速率）、Wi-Fi Direct 虚拟适配器（本地连接* N）、
/// 内核调试器、蓝牙 PAN（BTLAN）、Hyper-V vEthernet 等——全部按名称拦截。
/// </summary>
public static class NetAdapterFilter
{
    private static readonly string[] Blacklist =
    {
        // 虚拟化 / 隧道 / 回环
        "virtual", "vethernet", "tap-", "tunnel", "loopback", "vmware", "virtualbox",
        "hyper-v", "pseudo", "vpn",
        // 绑定在物理网卡上的过滤驱动子接口（与物理卡同类型、同速率、状态 Up）
        "qos packet scheduler", "packet scheduler", "wifi filter", "native wifi",
        "wfp", "windows filter", "npcap", "wintun", "lightweight filter",
        // 2026-09-10 实机反馈（NetManager 网卡列表「一堆不知什么东西」）：
        // 第三方安全软件注入的 NDIS 过滤驱动接口，名称形如
        // 「LAN-Huorong NDIS Filter Driver-0000」「WLAN-Huorong NDIS Filter Driver-0000」
        // （火绒实测；同类还有 360/卡巴等 NDIS Filter Driver 变体）——描述与真网卡不同，
        // 故「描述+MAC」去重合并不掉，必须在名称层拦。
        "ndis filter", "filter driver", "ndis light weight",
        // 外设 / 调试 / 热点
        "bluetooth", "btlan", "wan miniport", "kernel debug", "内核调试器",
        "wi-fi direct", "hosted network", "本地连接*", "microsoft wi-fi",
        "openvpn", "wireguard", "zerotier", "tailscale",
    };

    /// <summary>
    /// 名称黑名单命中即垃圾条目（<paramref name="description"/> 与连接名 <paramref name="name"/>
    /// 应分别传入；BCL/WMI 两侧的垃圾特征有的在描述里、有的在连接名里）。
    /// </summary>
    /// <remarks>
    /// 2026-09-07 采纳外部评审：改用 <see cref="ReadOnlySpan{T}"/> + OrdinalIgnoreCase
    /// 逐项匹配，去掉字符串拼接与 <c>ToLowerInvariant()</c> 的两次堆分配（零分配）。
    /// 语义与旧实现完全一致（黑名单项本身都写成小写，忽略大小写比较即可）。
    /// </remarks>
    public static bool IsJunkAdapter(ReadOnlySpan<char> description, ReadOnlySpan<char> name = default)
    {
        foreach (string pattern in Blacklist)
        {
            if (description.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || (!name.IsEmpty && name.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 断开接口的幽灵实例判定（2026-09-07 实机实证：Killer AX1690i 机型驱动多次升级后，
    /// 残留的历史接口以「WLAN 2 / WLAN 4 / WLAN 5」出现——设备管理器与 Driver Genius
    /// 均只有 1 块物理卡。残留特征：断开 + 速率未知 + 无真实 IPv4（无地址或仅
    /// APIPA 169.254/16 自动私有地址）；且 MAC 互不相同，同 MAC 去重（NetworkInfoService
    /// 的 GroupBy）拦不住它们）。
    /// 判据：已连接(Up)必真实；断开的接口须「速率已知（&gt;0，实证：物理 LAN 断开仍报
    /// 1 Gbps）」或「持有非 APIPA 的 IPv4（配过静态/真实地址）」才算真实存在，二者皆无
    /// 即幽灵。局限：全新未配置且驱动未报速率的真实断开卡会被隐藏——无配置的断开卡
    /// 本无管理价值，接入后立即出现。
    /// </summary>
    /// <param name="isUp">接口是否已连接（Up）——已连接必然真实存在。</param>
    /// <param name="speedBitsPerSecond">
    /// 速率（bit/s）；BCL 对断开或未协商的接口常报 -1（未知）。&gt;0 视为真实硬件链路能力。
    /// </param>
    /// <param name="ipv4Addresses">接口持有的 IPv4 地址集合（IPv6 等异构地址不参与判定）。</param>
    /// <param name="connectionName">
    /// 连接名（<c>NetworkInterface.Name</c>，如 "WLAN" / "WLAN 2"）。用于最后一道逃生舱：
    /// 不带 Windows 自动编号后缀（"WLAN 2"）的干净名，即便完全断链也可能是被<b>手动禁用</b>
    /// 的真实物理卡，必须保留——否则用户在 NetManager 里无法再启用它（误杀代价远高于漏网幽灵）。
    /// </param>
    /// <param name="macAddressBytes">
    /// MAC 地址字节数（真实以太网 / Wi-Fi 卡必有 6 字节 MAC；纯软件抽象层、部分虚拟
    /// 小端口为 0）——0 即判定幽灵，与「无速率 + 无真实 IP」同效。
    /// </param>
    public static bool IsGhostAdapter(
        bool isUp,
        long speedBitsPerSecond,
        IReadOnlyCollection<System.Net.IPAddress> ipv4Addresses,
        string connectionName,
        int macAddressBytes = 6)
    {
        // 无 MAC = 软件层抽象接口，不可能是物理网卡（2026-09-07 采纳外部建议方案二的判据，
        // 与本项目既有黑名单/去重互补；WMI PhysicalAdapter 方案因架构原因不采纳，见 NetworkInfoService 注释）
        if (macAddressBytes == 0)
        {
            return true;
        }

        if (isUp)
        {
            return false;
        }

        if (speedBitsPerSecond > 0)
        {
            return false;
        }

        foreach (System.Net.IPAddress address in ipv4Addresses)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                continue; // 非 IPv4（防御：入参虽按 IPv4 过滤，仍不把异构地址当证据）
            }

            bool isApipa = bytes[0] == 169 && bytes[1] == 254;
            if (!isApipa)
            {
                return false; // 持有真实 IPv4（静态配置/历史真实地址）→ 真实接口
            }
        }

        // 逃生舱（2026-09-07 采纳外部评审）：走到这里已是「断开 + 无速率 + 无真实 IPv4」。
        // 手动禁用的真实物理卡正是这副模样（驱动停摆 → BCL 拿不到速率/IP），而驱动残留的
        // 幽灵连接名带 Windows 自动编号后缀（实机：WLAN 2 / WLAN 4 / WLAN 5）——
        // 名称干净的按真实物理卡保留，否则用户将永远无法在 NetManager 中重新启用它。
        // 局限：残留幽灵若恰好占用无后缀名称（如主卡改名后残留 "WLAN"）会漏网，
        // 但代价（多显示一条）远低于误杀禁用物理卡。
        if (!HasAutoNumberSuffix(connectionName))
        {
            return false;
        }

        return true; // 断开 + 无速率 + 无真实 IPv4 + 带编号后缀 = 幽灵
    }

    /// <summary>
    /// 连接名是否带 Windows 自动编号后缀（"WLAN 2" / "无线 3"）：末尾以空格 + 纯数字结尾。
    /// 同名连接对象重复出现时由系统自动追加，是驱动残留实例最稳定的特征。
    /// </summary>
    private static bool HasAutoNumberSuffix(string connectionName)
    {
        int space = connectionName.LastIndexOf(' ');
        if (space < 0 || space == connectionName.Length - 1)
        {
            return false;
        }

        for (int i = space + 1; i < connectionName.Length; i++)
        {
            if (!char.IsAsciiDigit(connectionName[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Overview 展示口径：只保留<b>已连接</b>的物理以太网 / Wi-Fi 适配器
    /// （离线、只收、非物理类型一律不可见）。
    /// </summary>
    public static bool IsUserFacingAdapter(NetworkInterfaceType type, bool isUp, bool receiveOnly, string description, string name = "")
    {
        if (!isUp || receiveOnly)
        {
            return false;
        }
        if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
        {
            return false;
        }
        return !IsJunkAdapter(description, name);
    }
}
