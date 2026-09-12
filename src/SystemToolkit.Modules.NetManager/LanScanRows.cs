using SystemToolkit.Core.Network.LanScan;

namespace SystemToolkit.Modules.NetManager;

/// <summary>设备表行状态（徽标双通道：颜色 + 文字，色盲友好——示意图标注 4️⃣）。</summary>
public enum LanRowKind
{
    /// <summary>在线且与基线一致。</summary>
    Online = 0,

    /// <summary>冲突 IP 的当前应答方（红底行）。</summary>
    Conflict = 1,

    /// <summary>冲突 IP 的另一应答 MAC（成对展示，红底行）。</summary>
    ConflictPeer = 2,

    /// <summary>相对基线换 MAC（橙徽标，附旧值）。</summary>
    Changed = 3,

    /// <summary>基线外新绑定（蓝徽标）。</summary>
    New = 4,

    /// <summary>基线内本轮未应答（灰）。</summary>
    Offline = 5,

    /// <summary>应答但邻居表未学到 MAC（灰，跨网段/防火墙形态）。</summary>
    NoMac = 6,
}

/// <summary>局域网设备表行视图模型（NET-6 示意图 ① 表格列契约）。</summary>
public sealed class LanDeviceRow
{
    public LanDeviceRow(LanDevice device, LanRowKind kind, string? oldMacNote = null)
    {
        Device = device;
        Kind = kind;
        OldMacNote = oldMacNote;
    }

    public LanDevice Device { get; }

    public LanRowKind Kind { get; }

    /// <summary>绑定变更/冲突对偶行的「← 原 XX:XX…」注记。</summary>
    public string? OldMacNote { get; }

    public string Ip => Device.Ip;

    public string Mac => Device.Mac.Length > 0 ? Device.Mac : "—";

    public string Vendor => Device.Vendor ?? "—";

    public string Hostname => Device.Hostname ?? "（未反查到名称）";

    /// <summary>OS 推断列（TTL 分类，带「(推断)」后缀；无 ICMP 应答为"—"）。</summary>
    public string OsText => Device.Os ?? "—";

    public string FirstSeenText => Device.FirstSeen.ToString("MM-dd HH:mm");

    public string LastSeenText => Device.LastSeen.ToString("HH:mm:ss");

    public string KindText => Kind switch
    {
        LanRowKind.Conflict => "冲突",
        LanRowKind.ConflictPeer => "冲突·另一应答",
        LanRowKind.Changed => "绑定变更",
        LanRowKind.New => "新设备",
        LanRowKind.Offline => "离线",
        LanRowKind.NoMac => "未学到 MAC",
        _ => "在线",
    };

    public bool IsConflictRow => Kind is LanRowKind.Conflict or LanRowKind.ConflictPeer;

    /// <summary>可搜索文本（预计算：IP/MAC/主机名/厂商 小写拼接）。</summary>
    public string SearchBlob =>
        $"{Device.Ip} {Device.Mac} {Device.Hostname} {Device.Vendor}".ToLowerInvariant();
}

/// <summary>事件流行（时间 + 人读摘要，冲突红色）。</summary>
public sealed class LanEventRow
{
    public LanEventRow(LanEvent evt) => Event = evt;

    public LanEvent Event { get; }

    public string TimeText => Event.At.ToString("MM-dd HH:mm");

    public string Text => Event.Detail;

    public bool IsConflict => Event.Type == LanEventType.Conflict;

    public string Glyph => Event.Type switch
    {
        LanEventType.Conflict => "⚠",
        LanEventType.BindingChanged => "⇄",
        LanEventType.NewDevice => "●",
        _ => "○",
    };
}

