namespace SystemToolkit.Core.Network.LanScan;

/// <summary>一轮比对的结构化输出（纯函数产物，落盘与升级判定由 <see cref="LanScanService"/> 负责）。</summary>
/// <param name="Events">本轮新事件（新→旧顺序）。</param>
/// <param name="Entries">合并本轮观测后的新基线绑定表（已按 LastSeen 截断到容量上限）。</param>
/// <param name="ConflictedIps">已按「连续两轮绑定不一致」定论的冲突 IP。</param>
/// <param name="NeedsRecheck">首轮观测到的绑定变化候选（无上轮可比）——服务层需即时双探复核。</param>
public sealed record LanDiffOutcome(
    IReadOnlyList<LanEvent> Events,
    IReadOnlyList<LanBaselineEntry> Entries,
    IReadOnlyList<string> ConflictedIps,
    IReadOnlyList<LanDevice> NeedsRecheck);

/// <summary>
/// 基线比对引擎（NET-6 判定核心，零 IO 零系统调用）：
/// <list type="bullet">
/// <item><description>基线外的 IP → <see cref="LanEventType.NewDevice"/>。</description></item>
/// <item><description>同 IP 相对基线换 MAC → 有上轮数据时：与上轮不一致=Conflict（两轮复核天然达成）、
/// 与上轮一致=BindingChanged（新绑定已稳定）；无上轮数据（重启后首轮）→ NeedsRecheck 候选。</description></item>
/// <item><description>上轮在线本轮消失 → <see cref="LanEventType.DeviceGone"/>（软事件；重启后首轮不比，防休眠设备刷屏）。</description></item>
/// </list>
/// 换 MAC 的基线条目 FirstSeen 重置为新绑定代际；消失条目保留不删（回归免重报，容量超限按 LastSeen 最旧淘汰）。
/// </summary>
public static class LanDiffEngine
{
    /// <summary>基线条目容量上限（超出按 LastSeen 最旧淘汰；家用/办公 /24 远达不到）。</summary>
    public const int MaxEntries = 2000;

    /// <summary>比对基线/上轮/本轮，产出事件、新基线条目、冲突清单与首轮复核候选（不写盘、不判定复核）。</summary>
    public static LanDiffOutcome Diff(
        LanBaseline? baseline,
        IReadOnlyList<LanDevice> current,
        IReadOnlyDictionary<string, string>? previousRound,
        DateTimeOffset now)
    {
        var old = (baseline?.Entries ?? [])
            .GroupBy(static e => e.Ip, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        HashSet<string> seenIps = new(StringComparer.Ordinal);

        List<LanEvent> events = [];
        List<string> conflicts = [];
        List<LanDevice> recheck = [];
        Dictionary<string, LanBaselineEntry> merged = new(StringComparer.Ordinal);

        foreach (LanDevice device in current)
        {
            seenIps.Add(device.Ip);
            if (!old.TryGetValue(device.Ip, out LanBaselineEntry? prior))
            {
                events.Add(new LanEvent(LanEventType.NewDevice, device.Ip, null, device.Mac,
                    $"新设备上线 {device.Ip}（{device.Mac}{Suffix(device)}）", now));
                merged[device.Ip] = ToEntry(device, firstSeen: now);
                continue;
            }

            if (string.Equals(prior.Mac, device.Mac, StringComparison.Ordinal))
            {
                merged[device.Ip] = prior with
                {
                    LastSeen = now,
                    Hostname = device.Hostname ?? prior.Hostname,
                    Vendor = device.Vendor ?? prior.Vendor,
                };
                continue;
            }

            // 绑定变化：三种定性（Conflict / BindingChanged / 待复核）
            string? lastMac = previousRound is not null && previousRound.TryGetValue(device.Ip, out string? m) ? m : null;
            if (lastMac is not null && !string.Equals(lastMac, device.Mac, StringComparison.Ordinal))
            {
                conflicts.Add(device.Ip);
                events.Add(new LanEvent(LanEventType.Conflict, device.Ip, lastMac, device.Mac,
                    $"IP 冲突：{device.Ip} 连续两轮应答不同 MAC（上轮 {lastMac} → 本轮 {device.Mac}）", now));
                merged[device.Ip] = ToEntry(device, firstSeen: now);
            }
            else if (lastMac is not null)
            {
                events.Add(new LanEvent(LanEventType.BindingChanged, device.Ip, prior.Mac, device.Mac,
                    $"绑定变更：{device.Ip} 由 {prior.Mac} 改为 {device.Mac}{Suffix(device)}", now));
                merged[device.Ip] = ToEntry(device, firstSeen: now);
            }
            else
            {
                recheck.Add(device); // 首轮无可比上轮：交给服务层即时双探
                merged[device.Ip] = ToEntry(device, firstSeen: prior.FirstSeen); // 暂不改代际，复核后由服务层修正
            }
        }

        // 消失设备：仅当上轮在场（本轮真的掉线）才报，避免重启后把基线里所有离线设备刷进事件流
        if (previousRound is not null)
        {
            foreach (string goneIp in previousRound.Keys)
            {
                if (!seenIps.Contains(goneIp) && old.TryGetValue(goneIp, out LanBaselineEntry? entry))
                {
                    events.Add(new LanEvent(LanEventType.DeviceGone, goneIp, entry.Mac, null,
                        $"设备离线：{goneIp}（{entry.Mac}）", now));
                    merged[goneIp] = entry; // 保留待回归
                }
            }
        }

        // 基线里从未出现在任何轮次的老条目原样保留（休眠设备）
        foreach (KeyValuePair<string, LanBaselineEntry> pair in old)
        {
            if (!merged.ContainsKey(pair.Key))
            {
                merged[pair.Key] = pair.Value;
            }
        }

        var entries = merged.Values
            .OrderByDescending(static e => e.LastSeen)
            .Take(MaxEntries)
            .ToList();
        events.Reverse(); // 新→旧

        return new LanDiffOutcome(events, entries, conflicts, recheck);
    }

    /// <summary>复核稳定后的基线修正：确认新绑定成立（双探一致），按「新代际」记 FirstSeen。</summary>
    public static LanBaselineEntry ConfirmRecheck(LanDevice device, DateTimeOffset now) =>
        ToEntry(device, firstSeen: now);

    private static LanBaselineEntry ToEntry(LanDevice device, DateTimeOffset firstSeen) =>
        new(device.Ip, device.Mac, device.Hostname, device.Vendor, firstSeen, device.LastSeen);

    private static string Suffix(LanDevice device) =>
        device.Hostname is null && device.Vendor is null ? "" : $"：{device.Hostname ?? device.Vendor}";
}
