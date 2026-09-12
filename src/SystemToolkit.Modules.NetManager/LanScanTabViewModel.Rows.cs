using SystemToolkit.Core.Network.LanScan;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 行合成半区（NET-6 拆文件纪律，主半区 <c>LanScanTabViewModel.cs</c> 守 500 行上限）：
/// 搜索过滤重建、排序权重、本轮设备×基线×事件的三源行合成。
/// </summary>
public partial class LanScanTabViewModel
{
    private void RebuildRows()
    {
        string filter = _filter.Trim().ToLowerInvariant();
        List<LanDeviceRow> built = BuildAllRows();
        Rows.Clear();
        foreach (LanDeviceRow row in built
                     .OrderBy(static r => KindWeight(r.Kind))
                     .ThenBy(static r => r.Ip, StringComparer.Ordinal))
        {
            if (filter.Length == 0 || row.SearchBlob.Contains(filter, StringComparison.Ordinal))
            {
                Rows.Add(row);
            }
        }
    }

    /// <summary>排序权重：冲突对 → 变更 → 新设备 → 在线 → 无 MAC → 离线（问题置顶，示意图 ①）。</summary>
    private static int KindWeight(LanRowKind kind) => kind switch
    {
        LanRowKind.Conflict => 0,
        LanRowKind.ConflictPeer => 1,
        LanRowKind.Changed => 2,
        LanRowKind.New => 3,
        LanRowKind.Online => 4,
        LanRowKind.NoMac => 5,
        _ => 6,
    };

    /// <summary>行合成：本轮设备（冲突/变更/新 → 徽标）+ 冲突对偶行 + 基线内未应答离线行。</summary>
    private List<LanDeviceRow> BuildAllRows()
    {
        List<LanDeviceRow> rows = [];
        if (_lastResult is not LanScanResult result)
        {
            return rows;
        }

        var conflictIps = result.ConflictedIps.ToHashSet(StringComparer.Ordinal);
        var changedBy = result.Events
            .Where(static e => e.Type == LanEventType.BindingChanged && e.OldMac is not null)
            .GroupBy(static e => e.Ip, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        var newIps = result.Events
            .Where(static e => e.Type == LanEventType.NewDevice)
            .Select(static e => e.Ip)
            .ToHashSet(StringComparer.Ordinal);

        foreach (LanDevice device in result.Devices)
        {
            LanRowKind kind = device.Mac.Length == 0 ? LanRowKind.NoMac
                : conflictIps.Contains(device.Ip) ? LanRowKind.Conflict
                : changedBy.ContainsKey(device.Ip) ? LanRowKind.Changed
                : newIps.Contains(device.Ip) ? LanRowKind.New
                : LanRowKind.Online;
            rows.Add(new LanDeviceRow(device, kind,
                changedBy.TryGetValue(device.Ip, out LanEvent? ch) ? ch.OldMac : null));

            // 冲突对偶行：事件里记录的上一应答 MAC（成对展示，示意图 ① 两行同 IP）
            LanEvent? peerSource = result.Events.FirstOrDefault(e =>
                e.Type == LanEventType.Conflict && e.Ip == device.Ip && e.OldMac is not null);
            if (peerSource is not null)
            {
                LanDevice peer = device with { Mac = peerSource.OldMac!, Hostname = null, Os = null, Vendor = OuiTable.Lookup(peerSource.OldMac!) };
                rows.Add(new LanDeviceRow(peer, LanRowKind.ConflictPeer, $"对偶应答 {device.Mac}"));
            }
        }

        foreach (LanBaselineEntry entry in _scan.PeekBaseline()?.Entries ?? [])
        {
            bool online = result.Devices.Any(d => string.Equals(d.Ip, entry.Ip, StringComparison.Ordinal));
            if (!online && !conflictIps.Contains(entry.Ip))
            {
                rows.Add(new LanDeviceRow(
                    new LanDevice(entry.Ip, entry.Mac, entry.Hostname, entry.Vendor,
                        entry.FirstSeen, entry.LastSeen, entry.Os),
                    LanRowKind.Offline));
            }
        }

        return rows;
    }

}
