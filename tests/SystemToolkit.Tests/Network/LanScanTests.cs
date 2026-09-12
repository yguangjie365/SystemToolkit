using System.Runtime.InteropServices;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Network.LanScan;

namespace SystemToolkit.Tests;

/// <summary>
/// NET-6 局域网扫描域测试：子网枚举、MAC 归一、OUI、邻居表行解析（手工构造表内存）、
/// 基线比对（新设备/绑定变更/两轮冲突/离线）、基线持久化三态、扫描服务编排（假探针，全程不触真网）。
/// </summary>
public class LanScanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(8));

    private static LanDevice Dev(string ip, string mac, DateTimeOffset? seen = null) =>
        new(ip, mac, null, null, seen ?? T0, seen ?? T0);

    // ═══════════════ LanSubnet ═══════════════

    [Fact]
    public void Subnet_Cidr24_EnumeratesHostRange()
    {
        Assert.True(LanSubnet.TryBuild("192.168.9.50", 24, 1024, out LanSubnetPlan? plan));
        Assert.Equal("192.168.9.0/24", plan!.NetworkLabel);
        Assert.Equal(254, plan.Hosts.Count);
        Assert.Equal("192.168.9.1", plan.Hosts[0]);
        Assert.Equal("192.168.9.254", plan.Hosts[^1]);
        Assert.DoesNotContain("192.168.9.0", plan.Hosts);
        Assert.DoesNotContain("192.168.9.255", plan.Hosts);
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void Subnet_OverLimit_TruncatesExplicitly()
    {
        Assert.True(LanSubnet.TryBuild("10.20.0.7", 16, 1024, out LanSubnetPlan? plan));
        Assert.Equal(1024, plan!.Hosts.Count);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void Subnet_SmallLimit_RespectsIt()
    {
        Assert.True(LanSubnet.TryBuild("192.168.9.50", 24, 10, out LanSubnetPlan? plan));
        Assert.Equal(10, plan!.Hosts.Count);
        Assert.True(plan.Truncated);
    }

    [Theory]
    [InlineData("192.168.9.50/24", 254)]
    [InlineData("192.168.9.50/32", 1)]
    [InlineData("192.168.9.50", 1)] // 无掩码段按 /32 兜底（只扫自身）
    public void Subnet_FromAdapterEntry(string entry, int expected)
    {
        Assert.True(LanSubnet.TryBuildFromAdapterEntry(entry, 1024, out LanSubnetPlan? plan));
        Assert.Equal(expected, plan!.Hosts.Count);
    }

    [Theory]
    [InlineData("999.1.1.1/24")]
    [InlineData("")]
    public void Subnet_Malformed_ReturnsFalse(string entry) =>
        Assert.False(LanSubnet.TryBuildFromAdapterEntry(entry, 1024, out _));

    // ═══════════════ LanMac / OuiTable ═══════════════

    [Theory]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("aabb.ccdd.eeff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF")]
    [InlineData("001B21CD34EF", "00:1B:21:CD:34:EF")]
    public void Mac_Normalize_AcceptsCommonShapes(string raw, string expected) =>
        Assert.Equal(expected, LanMac.Normalize(raw));

    [Theory]
    [InlineData("aa:bb:cc")]
    [InlineData("xyz")]
    [InlineData("aa:bb-cc:dd:ee:ff")]
    [InlineData("")]
    public void Mac_Normalize_RejectsMalformed(string raw) =>
        Assert.Null(LanMac.Normalize(raw));

    [Fact]
    public void Mac_LocallyAdministered_Flag()
    {
        Assert.True(LanMac.IsLocallyAdministered("02:AA:BB:CC:DD:EE"));
        Assert.False(LanMac.IsLocallyAdministered("40:AA:BB:CC:DD:EE"));
    }

    [Fact]
    public void Oui_Hit_Miss_AndRandomMac()
    {
        Assert.Equal("VMware", OuiTable.Lookup("00:0C:29:AA:BB:CC"));
        Assert.Equal("随机/虚拟 MAC", OuiTable.Lookup("02:AA:BB:CC:DD:EE"));
        Assert.Null(OuiTable.Lookup("40:AA:BB:CC:DD:EE"));
        Assert.Null(OuiTable.Lookup(""));
    }

    // ═══════════════ 邻居表行解析（官方布局回归锁） ═══════════════

    [Fact]
    public void Probe_ParseTable_ReadsVerifiedOffsets_AndSkipsBadRows()
    {
        IntPtr table = Marshal.AllocHGlobal(LanNeighborProbe.TableHeaderSize + 3 * LanNeighborProbe.RowSize);
        try
        {
            Marshal.WriteInt32(table, LanNeighborProbe.TableEntriesOffset, 3);

            IntPtr row0 = IntPtr.Add(table, LanNeighborProbe.TableHeaderSize);
            Marshal.WriteInt16(row0, LanNeighborProbe.RowAddressOffset, LanNeighborProbe.AfInet);
            byte[] ip = [192, 168, 7, 8];
            for (int i = 0; i < 4; i++)
            {
                Marshal.WriteByte(row0, LanNeighborProbe.RowAddressOffset + LanNeighborProbe.SockAddrIpOffset + i, ip[i]);
            }

            Marshal.WriteInt32(row0, LanNeighborProbe.RowIfIndexOffset, 42);
            byte[] mac = [0x00, 0x0C, 0x29, 0xAA, 0xBB, 0xCC];
            for (int i = 0; i < 6; i++)
            {
                Marshal.WriteByte(row0, LanNeighborProbe.RowPhysAddrOffset + i, mac[i]);
            }

            Marshal.WriteInt32(row0, LanNeighborProbe.RowPhysLenOffset, 6);
            Marshal.WriteInt32(row0, LanNeighborProbe.RowStateOffset, 5);

            IntPtr row1 = IntPtr.Add(table, LanNeighborProbe.TableHeaderSize + LanNeighborProbe.RowSize);
            Marshal.WriteInt16(row1, LanNeighborProbe.RowAddressOffset, 10); // 非 AF_INET：整行跳过

            // row2：合法族 + len 6 但 MAC 全零（实机 .99 未解析占位行）→ 必须挡掉
            IntPtr row2 = IntPtr.Add(table, LanNeighborProbe.TableHeaderSize + 2 * LanNeighborProbe.RowSize);
            Marshal.WriteInt16(row2, LanNeighborProbe.RowAddressOffset, LanNeighborProbe.AfInet);
            Marshal.WriteByte(row2, LanNeighborProbe.RowAddressOffset + LanNeighborProbe.SockAddrIpOffset, 192);
            for (int i = 0; i < 6; i++)
            {
                Marshal.WriteByte(row2, LanNeighborProbe.RowPhysAddrOffset + i, 0); // 显式清零（AllocHGlobal 是脏内存）
            }
            Marshal.WriteInt32(row2, LanNeighborProbe.RowPhysLenOffset, 6);
            Marshal.WriteInt32(row2, LanNeighborProbe.RowStateOffset, 0);

            List<NeighborEntry> rows = LanNeighborProbe.ParseTable(table);
            NeighborEntry only = Assert.Single(rows);
            Assert.Equal("192.168.7.8", only.Ip);
            Assert.Equal("00:0C:29:AA:BB:CC", only.Mac);
            Assert.Equal(42, only.IfIndex);
            Assert.Equal(5, only.State);
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", 0x0100007F)]
    [InlineData("192.168.9.50", 0x3209A8C0)]
    public void Probe_IpToNetworkOrderUlong(string text, uint expected)
    {
        Assert.True(LanNeighborProbe.TryToNetworkOrderUInt(text, out uint actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Probe_IpConversion_RejectsNonIpv4()
    {
        Assert.False(LanNeighborProbe.TryToNetworkOrderUInt("::1", out _));
        Assert.False(LanNeighborProbe.TryToNetworkOrderUInt("nope", out _));
    }

    // ═══════════════ LanDiffEngine ═══════════════

    [Fact]
    public void Diff_EmptyBaseline_EmitsNewDevice()
    {
        LanDiffOutcome diff = LanDiffEngine.Diff(null, [Dev("10.0.0.5", "AA:00:00:00:00:01")], null, T0);
        LanEvent only = Assert.Single(diff.Events);
        Assert.Equal(LanEventType.NewDevice, only.Type);
        Assert.Single(diff.Entries);
        Assert.Empty(diff.NeedsRecheck);
    }

    [Fact]
    public void Diff_StableBinding_NoEvent()
    {
        LanBaseline baseline = Baseline(("10.0.0.5", "AA:00:00:00:00:01"));
        LanDiffOutcome diff = LanDiffEngine.Diff(
            baseline, [Dev("10.0.0.5", "AA:00:00:00:00:01")],
            new Dictionary<string, string> { ["10.0.0.5"] = "AA:00:00:00:00:01" }, T0);
        Assert.Empty(diff.Events);
        Assert.Equal(T0, diff.Entries.Single().LastSeen);
    }

    [Fact]
    public void Diff_MacChangedVsPreviousRound_Conflict()
    {
        LanBaseline baseline = Baseline(("10.0.0.5", "AA:00:00:00:00:01"));
        LanDiffOutcome diff = LanDiffEngine.Diff(
            baseline, [Dev("10.0.0.5", "BB:00:00:00:00:02")],
            new Dictionary<string, string> { ["10.0.0.5"] = "AA:00:00:00:00:01" }, T0);
        Assert.Equal(LanEventType.Conflict, Assert.Single(diff.Events).Type);
        Assert.Contains("10.0.0.5", diff.ConflictedIps);
    }

    [Fact]
    public void Diff_MacChangedButStableAcrossRounds_BindingChanged()
    {
        LanBaseline baseline = Baseline(("10.0.0.5", "AA:00:00:00:00:01"));
        LanDiffOutcome diff = LanDiffEngine.Diff(
            baseline, [Dev("10.0.0.5", "BB:00:00:00:00:02")],
            new Dictionary<string, string> { ["10.0.0.5"] = "BB:00:00:00:00:02" }, T0);
        Assert.Equal(LanEventType.BindingChanged, Assert.Single(diff.Events).Type);
        LanBaselineEntry entry = diff.Entries.Single(e => e.Ip == "10.0.0.5");
        Assert.Equal("BB:00:00:00:00:02", entry.Mac);
        Assert.Equal(T0, entry.FirstSeen); // 新绑定代际
    }

    [Fact]
    public void Diff_NoPreviousRound_CandidateForRecheck()
    {
        LanBaseline baseline = Baseline(("10.0.0.5", "AA:00:00:00:00:01"));
        LanDiffOutcome diff = LanDiffEngine.Diff(
            baseline, [Dev("10.0.0.5", "BB:00:00:00:00:02")], null, T0);
        Assert.Empty(diff.Events);
        Assert.Equal("10.0.0.5", Assert.Single(diff.NeedsRecheck).Ip);
    }

    [Fact]
    public void Diff_Gone_ReportedOnlyWhenWasOnlineLastRound()
    {
        LanBaseline baseline = Baseline(("10.0.0.5", "AA:00:00:00:00:01"));

        LanDiffOutcome withPrev = LanDiffEngine.Diff(
            baseline, [], new Dictionary<string, string> { ["10.0.0.5"] = "AA:00:00:00:00:01" }, T0);
        Assert.Equal(LanEventType.DeviceGone, Assert.Single(withPrev.Events).Type);
        Assert.Single(withPrev.Entries); // 条目保留待回归

        LanDiffOutcome firstRound = LanDiffEngine.Diff(baseline, [], null, T0);
        Assert.Empty(firstRound.Events); // 重启后首轮不刷屏
    }

    // ═══════════════ LanBaselineStore ═══════════════

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"st-lanscan-{Guid.NewGuid():N}.json");

    [Fact]
    public void Store_Missing_Save_Load_RoundTrip_AndReset()
    {
        string path = TempPath();
        try
        {
            var store = new LanBaselineStore(path);
            Assert.Equal(LanBaselineLoadStatus.Missing, store.Load().State);

            var baseline = new LanBaseline(
                LanBaselineStore.FormatVersion, T0,
                [new LanBaselineEntry("10.0.0.5", "AA:00:00:00:00:01", "PC-A", "VMware", T0, T0)],
                [new LanEvent(LanEventType.Conflict, "10.0.0.5", "AA:00:00:00:00:01", "BB:00:00:00:00:02", "x", T0)]);
            store.Save(baseline.Entries, baseline.Events, baseline.UpdatedAt);

            LanBaselineLoad loaded = store.Load();
            Assert.Equal(LanBaselineLoadStatus.Ok, loaded.State);
            Assert.Equal(baseline.Entries, loaded.Data!.Entries);
            Assert.Equal(baseline.Events, loaded.Data.Events);

            store.Reset();
            Assert.Equal(LanBaselineLoadStatus.Missing, store.Load().State);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Store_CorruptedOrBadVersion_ReportsCorrupted()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{not-json");
            Assert.Equal(LanBaselineLoadStatus.Corrupted, new LanBaselineStore(path).Load().State);

            File.WriteAllText(path, """{"Version":99,"UpdatedAt":"2026-09-12T10:00:00+08:00","Entries":[],"Events":[]}""");
            Assert.Equal(LanBaselineLoadStatus.Corrupted, new LanBaselineStore(path).Load().State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_EventRing_TruncatesOldest()
    {
        string path = TempPath();
        try
        {
            var store = new LanBaselineStore(path);
            LanEvent[] events = Enumerable.Range(0, LanBaselineStore.MaxEvents + 50)
                .Select(static i => new LanEvent(LanEventType.NewDevice, $"10.0.0.{i % 250}", null, null, i.ToString(), T0))
                .ToArray(); // 新→旧
            store.Save([], events, T0);

            LanBaselineLoad loaded = store.Load();
            Assert.Equal(LanBaselineStore.MaxEvents, loaded.Data!.Events.Count);
            Assert.Equal("0", loaded.Data.Events[0].Detail); // 最新在前
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ═══════════════ LanScanService（假探针全链路） ═══════════════

    private static LanSubnetPlan TestPlan() =>
        LanSubnet.TryBuild("192.168.9.50", 24, 1024, out LanSubnetPlan? plan) ? plan! : throw new InvalidOperationException();

    private static async Task<LanScanResult> RunScan(LanScanService service) =>
        await service.ScanAsync(TestPlan(), progress: null, ct: default);

    [Fact]
    public async Task Scan_NewStore_FirstRound_EmitsNewDevices_AndPersistsBaseline()
    {
        string path = TempPath();
        try
        {
            FakeProbe probe = new FakeProbe()
                .Alive("192.168.9.10", "00:0C:29:00:00:10")
                .Alive("192.168.9.11", "40:11:22:33:44:55");
            var service = new LanScanService(probe, new LanBaselineStore(path), hostnameResolver: static _ => Task.FromResult<string?>(null));

            LanScanResult round1 = await RunScan(service);
            Assert.False(round1.WasCancelled);
            Assert.Equal(2, round1.Devices.Count);
            Assert.Equal(2, round1.Events.Count(e => e.Type == LanEventType.NewDevice));
            Assert.Equal("VMware", round1.Devices.Single(d => d.Ip == "192.168.9.10").Vendor);
            Assert.Null(round1.Devices.Single(d => d.Ip == "192.168.9.11").Vendor);
            Assert.NotNull(service.PeekBaseline());

            LanScanResult round2 = await RunScan(service);
            Assert.Empty(round2.Events); // 稳定轮：无新设备、无变更、无离线
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Scan_CrossRoundMacFlip_Conflict_ThenSettles()
    {
        string path = TempPath();
        try
        {
            FakeProbe probe = new FakeProbe().Alive("192.168.9.10", "AA:00:00:00:00:10");
            var service = new LanScanService(probe, new LanBaselineStore(path), hostnameResolver: static _ => Task.FromResult<string?>(null));

            await RunScan(service); // 基线：…:10
            probe.SetMac("192.168.9.10", "BB:00:00:00:00:10");
            LanScanResult flip = await RunScan(service);
            Assert.Contains("192.168.9.10", flip.ConflictedIps); // 连续两轮不一致 = 冲突
            Assert.Equal(LanEventType.Conflict, flip.Events.Single().Type);

            LanScanResult same = await RunScan(service); // BB 稳定：基线已更新为 BB
            Assert.DoesNotContain("192.168.9.10", same.ConflictedIps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Scan_FirstRoundBindingChange_UnstableRecheck_Conflict_StableRecheck_BindingChanged()
    {
        string stablePath = TempPath();
        string flipPath = TempPath();
        try
        {
            // 基线预置旧绑定；服务重启（无跨轮态）→ 观测到新 MAC → 即时双探
            var seed = new LanBaselineStore(stablePath);
            seed.Save(
                [new LanBaselineEntry("192.168.9.10", "AA:00:00:00:00:10", null, null, T0, T0)], [], T0);
            FakeProbe stableProbe = new FakeProbe().Alive("192.168.9.10", "BB:00:00:00:00:10");
            LanScanResult stable = await new LanScanService(
                stableProbe, new LanBaselineStore(stablePath),
                hostnameResolver: static _ => Task.FromResult<string?>(null)).ScanAsync(TestPlan());
            LanEvent stableEvent = Assert.Single(stable.Events);
            Assert.Equal(LanEventType.BindingChanged, stableEvent.Type);
            Assert.Empty(stable.ConflictedIps);

            var flipSeed = new LanBaselineStore(flipPath);
            flipSeed.Save(
                [new LanBaselineEntry("192.168.9.10", "AA:00:00:00:00:10", null, null, T0, T0)], [], T0);
            FakeProbe flipProbe = new FakeProbe().Alive("192.168.9.10", "BB:00:00:00:00:10", "CC:00:00:00:00:10");
            LanScanResult flip = await new LanScanService(
                flipProbe, new LanBaselineStore(flipPath),
                hostnameResolver: static _ => Task.FromResult<string?>(null)).ScanAsync(TestPlan());
            Assert.Contains("192.168.9.10", flip.ConflictedIps);
            Assert.Equal(LanEventType.Conflict, Assert.Single(flip.Events).Type);
        }
        finally
        {
            File.Delete(stablePath);
            File.Delete(flipPath);
        }
    }

    [Fact]
    public async Task Scan_AliveWithoutMac_KeptForDisplay_ExcludedFromDiff()
    {
        string path = TempPath();
        try
        {
            FakeProbe probe = new FakeProbe().AliveWithoutMac("192.168.9.12") // ping 通但邻居表拿不到 MAC
                .Alive("192.168.9.10", "AA:00:00:00:00:10");
            var service = new LanScanService(probe, new LanBaselineStore(path), hostnameResolver: static _ => Task.FromResult<string?>(null));

            LanScanResult round1 = await RunScan(service);
            Assert.Equal("", round1.Devices.Single(d => d.Ip == "192.168.9.12").Mac);
            Assert.Single(round1.Events, e => e.Type == LanEventType.NewDevice); // 仅 …:10 进基线

            LanScanResult round2 = await RunScan(service); // 空 MAC 行不得每轮刷 NewDevice
            Assert.DoesNotContain(round2.Events, e => e.Ip == "192.168.9.12");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Scan_Cancelled_WasCancelled_NoBaselineCommit()
    {
        string path = TempPath();
        try
        {
            FakeProbe probe = new FakeProbe().Alive("192.168.9.10", "AA:00:00:00:00:10");
            var service = new LanScanService(probe, new LanBaselineStore(path), hostnameResolver: static _ => Task.FromResult<string?>(null));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            LanScanResult cancelled = await service.ScanAsync(TestPlan(), progress: null, ct: cts.Token);
            Assert.True(cancelled.WasCancelled);
            Assert.Empty(cancelled.Devices);
            Assert.Equal(LanBaselineLoadStatus.Missing, new LanBaselineStore(path).Load().State);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Scan_ReportsProgress_ToTotal()
    {
        string path = TempPath();
        try
        {
            var probe = new FakeProbe();
            var progress = new List<LanScanProgress>();
            var service = new LanScanService(probe, new LanBaselineStore(path), hostnameResolver: static _ => Task.FromResult<string?>(null));
            await service.ScanAsync(TestPlan(), new SyncProgress<LanScanProgress>(progress.Add), CancellationToken.None);

            LanScanProgress arp = progress.First(p => p.Phase == "ARP 扫段");
            Assert.Equal(254, arp.Total);
            Assert.InRange(arp.Done, 1, 254);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Scan_BusLog_CarriesLanScanActionAndOutcome()
    {
        string path = TempPath();
        try
        {
            FakeProbe probe = new FakeProbe().Alive("192.168.9.10", "AA:00:00:00:00:10");
            List<Core.Logging.LogEntry> entries = await BusCapture.RecordAsync(async () =>
            {
                var service = new LanScanService(
                    probe, new LanBaselineStore(path), new FileLogger("netmanager"),
                    hostnameResolver: static _ => Task.FromResult<string?>(null));
                await RunScan(service);
            });

            Assert.Contains(entries, e => e.Action == LanScanService.ScanAction
                && e.Outcome == Core.Logging.LogResult.Success);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ═══════════════ LanOs / LanNbstat / 富化（实机验收整改 2026-09-12） ═══════════════

    [Theory]
    [InlineData(128, "Windows (推断)")]
    [InlineData(121, "Windows (推断)")]
    [InlineData(64, "Linux / Android / macOS (推断)")]
    [InlineData(57, "Linux / Android / macOS (推断)")]
    [InlineData(255, "网络设备 / 老款苹果 (推断)")]
    [InlineData(50, null)]
    [InlineData(200, null)]
    public void Os_Classify_TtlBands(int? ttl, string? expected) =>
        Assert.Equal(expected, LanOs.Classify(ttl));

    [Fact]
    public void Nbstat_Prefers20Unique_AndFallsBackToFirstUnique00()
    {
        const string full = """
            Interface: Ethernet 2
            Node Address: 00-15-5D-11-22-33

              Computer Name          <00>  UNIQUE      Registered      192.168.1.20         192.168.1.20
              WORKGROUP              <00>  GROUP       Registered      192.168.1.20         192.168.1.20
              DESKTOP-ABC            <20>  UNIQUE      Registered      192.168.1.20         192.168.1.20
            """;
        const string no20 = """
            Interface: Ethernet 2

              PC01                   <00>  UNIQUE      Registered      10.0.0.9             10.0.0.9
              WORKGROUP              <00>  GROUP       Registered      10.0.0.9             10.0.0.9
            """;
        Assert.Equal("DESKTOP-ABC", LanNbstat.Parse(full));
        Assert.Equal("PC01", LanNbstat.Parse(no20));
        Assert.Null(LanNbstat.Parse("命令失败: 找不到。"));
        Assert.Null(LanNbstat.Parse(null));
    }

    private sealed class FakePeerProbe : ILanPeerProbe
    {
        public LanPeerInfo Info { get; init; } = new(128, "NB-PC");

        public Task<LanPeerInfo> QueryAsync(string ipv4, CancellationToken ct = default) => Task.FromResult(Info);
    }

    [Fact]
    public async Task Scan_WithPeerProbe_FillsNetBiosNameAndOsText()
    {
        string path = TempPath();
        try
        {
            FakeProbe probe = new FakeProbe().Alive("192.168.9.10", "AA:00:00:00:00:10");
            var service = new LanScanService(
                probe, new LanBaselineStore(path),
                hostnameResolver: static _ => Task.FromResult<string?>(null), // DNS 无 PTR
                peerProbe: new FakePeerProbe());
            LanScanResult result = await RunScan(service);
            LanDevice only = Assert.Single(result.Devices);
            Assert.Equal("NB-PC", only.Hostname);           // nbtstat 兜底
            Assert.Equal("Windows (推断)", only.Os);          // TTL=128
            Assert.Equal("Windows (推断)", service.PeekBaseline()!.Entries.Single().Os);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PingOnce_Loopback_ReturnsVerdict()
    {
        string path = TempPath();
        try
        {
            var service = new LanScanService(new FakeProbe(), new LanBaselineStore(path));
            string verdict = await service.PingOnceAsync("127.0.0.1");
            Assert.StartsWith("✅", verdict); // 回环必应答（与持续 ping 用例同源先例）
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LanBaseline Baseline(params (string Ip, string Mac)[] pairs) =>
        new(LanBaselineStore.FormatVersion, T0,
            pairs.Select(p => new LanBaselineEntry(p.Ip, p.Mac, null, null, T0, T0)).ToList(), []);

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
    }

    /// <summary>假探针：alive 集合可控；每 IP 一条 MAC 循环表（多次读表按读序取模），支撑稳定/翻转两种复核形态。</summary>
    private sealed class FakeProbe : ILanNeighborProbe
    {
        private readonly HashSet<string> _alive = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _cycles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _readCounts = new(StringComparer.Ordinal);
        private int _pokes;

        public int Pokes => _pokes;

        public FakeProbe Alive(string ip, params string[] macCycle)
        {
            _alive.Add(ip);
            _cycles[ip] = macCycle;
            return this;
        }

        /// <summary>应答但邻居表始终学不到 MAC（跨网段/防火墙形态）。</summary>
        public FakeProbe AliveWithoutMac(string ip)
        {
            _alive.Add(ip);
            _cycles[ip] = [];
            return this;
        }

        public void SetMac(string ip, string mac)
        {
            _cycles[ip] = [mac];
            _readCounts.Remove(ip);
        }

        public bool TryPoke(string ipv4)
        {
            Interlocked.Increment(ref _pokes);
            return _alive.Contains(ipv4);
        }

        public IReadOnlyList<NeighborEntry> ReadNeighbors()
        {
            List<NeighborEntry> rows = [];
            foreach (KeyValuePair<string, string[]> pair in _cycles)
            {
                if (!_alive.Contains(pair.Key) || pair.Value.Length == 0)
                {
                    continue; // 应答但无 MAC：模拟邻居表尚未学到
                }

                int read = _readCounts.TryGetValue(pair.Key, out int n) ? n : 0;
                _readCounts[pair.Key] = read + 1;
                rows.Add(new NeighborEntry(pair.Key, pair.Value[read % pair.Value.Length], 1, 5));
            }

            return rows;
        }
    }
}
