namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// 局域网设备条目（NET-6）：一轮扫描视角下单个 IP 的应答快照。
/// <para>
/// 🔴 设计约束：纯数据、可 JSON 序列化、零 UI 依赖（02 分层）。
/// <see cref="Mac"/> 统一大写冒号分隔（<c>AA:BB:CC:DD:EE:FF</c>），比较前先归一。
/// </para>
/// </summary>
/// <param name="Ip">IPv4 地址（点分十进制）。</param>
/// <param name="Mac">归一化 MAC。</param>
/// <param name="Hostname">反查主机名；失败为 null（显示层降级"—"）。</param>
/// <param name="Vendor">OUI 厂商标识；未命中为 null。</param>
/// <param name="FirstSeen">该 IP↔MAC 绑定首次发现时间（来自基线）。</param>
/// <param name="LastSeen">本轮确认在线的时间。</param>
/// <param name="Os">TTL 推断的操作系统类别（带「(推断)」后缀；无 ICMP 应答为 null）。</param>
public sealed record LanDevice(
    string Ip,
    string Mac,
    string? Hostname,
    string? Vendor,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? Os = null);

/// <summary>基线比对事件类型（NET-6 判定三事件 + 离线软事件）。</summary>
public enum LanEventType
{
    /// <summary>基线外的新 IP 或新 IP↔MAC 绑定。</summary>
    NewDevice = 0,

    /// <summary>同一 IP 的 MAC 相对基线变化——"谁改了 IP/顶号"的直接证据。</summary>
    BindingChanged = 1,

    /// <summary>同一 IP 在一轮内两次探测应答了不同 MAC——IP 冲突进行时（红色告警）。</summary>
    Conflict = 2,

    /// <summary>基线内设备本轮未应答（软事件：只入历史流，不算告警）。</summary>
    DeviceGone = 3,
}

/// <summary>
/// 一条基线比对/复核事件。<see cref="OldMac"/>/<see cref="NewMac"/> 仅
/// <see cref="LanEventType.BindingChanged"/> 与 <see cref="LanEventType.Conflict"/> 有值。
/// </summary>
/// <param name="Type">事件类型。</param>
/// <param name="Ip">关联 IP。</param>
/// <param name="OldMac">旧绑定 MAC（可空）。</param>
/// <param name="NewMac">新观测 MAC（可空）。</param>
/// <param name="Detail">人读摘要（进事件流展示）。</param>
/// <param name="At">事件时间。</param>
public sealed record LanEvent(
    LanEventType Type,
    string Ip,
    string? OldMac,
    string? NewMac,
    string Detail,
    DateTimeOffset At);

/// <summary>基线里的单条绑定（持久化形态）。</summary>
/// <param name="Ip">IPv4。</param>
/// <param name="Mac">归一化 MAC。</param>
/// <param name="Hostname">最近一次反查到的主机名。</param>
/// <param name="Vendor">OUI 厂商。</param>
/// <param name="FirstSeen">绑定首次发现。</param>
/// <param name="LastSeen">绑定最后确认在线。</param>
/// <param name="Os">最近一次 TTL 推断的 OS 类别。</param>
public sealed record LanBaselineEntry(
    string Ip,
    string Mac,
    string? Hostname,
    string? Vendor,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? Os = null);

/// <summary>
/// 局域网基线快照（AtomicFile 落盘形态）：绑定表 + 事件历史环（保留最近 N 条）。
/// </summary>
/// <param name="Version">格式版本（当前 1），为未来迁移留手。</param>
/// <param name="UpdatedAt">基线生成时间。</param>
/// <param name="Entries">IP↔MAC 绑定表（每 IP 一条；冲突场景以"最后观测"为准）。</param>
/// <param name="Events">事件历史（新→旧）。</param>
public sealed record LanBaseline(
    int Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<LanBaselineEntry> Entries,
    IReadOnlyList<LanEvent> Events);

/// <summary>
/// 一轮扫描的完整结果。
/// </summary>
/// <param name="Devices">本轮在线设备（含事件徽章所需的时间信息）。</param>
/// <param name="Events">本轮产生的事件（New/BindingChanged/Conflict/DeviceGone，新→旧）。</param>
/// <param name="ConflictedIps">处于冲突状态的 IP 集合（UI 横幅与行徽章共用）。</param>
/// <param name="ScannedAt">扫描完成时间。</param>
/// <param name="Truncated">子网规模超上限被截断（如 /16 只扫前 N 段）时为 true。</param>
/// <param name="WasCancelled">取消中止：本轮不提交（基线与跨轮态均未更新），已发现数量仅入日志留痕。</param>
public sealed record LanScanResult(
    IReadOnlyList<LanDevice> Devices,
    IReadOnlyList<LanEvent> Events,
    IReadOnlyList<string> ConflictedIps,
    DateTimeOffset ScannedAt,
    bool Truncated,
    bool WasCancelled);
