using System.Text.Json;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 全量网络快照服务测试（2026-09-06 新增服务，设计 04 §4 Network Snapshot 纪律）：
/// 采集落盘（snake_case JSON / 原子写）、列表排序、损坏文件跳过、恢复回放
/// （适配器 IPv4/DNS → TCP → 跃点数 → 代理）、恢复前自动 BeforeChange 快照、
/// 只回放仍存在的适配器、MaxKeep 淘汰。真实网络/注册表全经 fake 隔离。
/// </summary>
public class NetworkSnapshotServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly NetworkTestFakes.FakeInfoService _info = new();
    private readonly NetworkTestFakes.FakeTuningService _tuning = new();
    private readonly NetworkTestFakes.FakeConfigService _config = new();

    public NetworkSnapshotServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"net-snapshot-test-{Guid.NewGuid():N}");
    }

    private NetworkSnapshotService Create() => new(_info, _tuning, _config, _directory);

    private static NetAdapterInfo Adapter(
        string name, bool isDhcp = true,
        string[]? ipv4 = null, string[]? gateways = null, string[]? dns = null) => new(
        name, "fake", NetType.Ethernet, OperStatus.Up, 1000, "AA", isDhcp,
        ipv4 ?? (isDhcp ? new[] { "192.168.1.10/24" } : Array.Empty<string>()),
        gateways ?? (isDhcp ? new[] { "192.168.1.1" } : Array.Empty<string>()),
        dns ?? Array.Empty<string>());

    private void WriteSnapshotFile(NetworkSnapshotRecord record)
    {
        Directory.CreateDirectory(_directory);
        string fileName = $"{record.CreatedAt:yyyyMMdd-HHmmss}_{record.Reason}_{record.Id}.json";
        File.WriteAllText(Path.Combine(_directory, fileName),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }

    // ── 采集 ──

    [Fact]
    public async Task Capture_WritesSnakeCaseJson_WithSchemaAndContent()
    {
        _info.Adapters.Add(Adapter("以太网"));
        _tuning.Current = new TcpGlobalSettings("normal", true, false, 10);
        _info.Proxy = new ProxyInfo(true, "127.0.0.1:7890");

        NetworkSnapshotRecord record = await Create().CaptureAsync(
            NetworkSnapshotService.ReasonBeforeChange, relatedAction: "SetDns", correlationId: "corr-1",
            onLine: _ => { });

        string[] files = Directory.GetFiles(_directory, "*.json");
        Assert.Single(files);

        NetworkSnapshotRecord? roundTrip = JsonSerializer.Deserialize<NetworkSnapshotRecord>(
            File.ReadAllText(files[0]),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.NotNull(roundTrip);
        Assert.Equal(record.Id, roundTrip!.Id);
        Assert.Equal("1.0", roundTrip.Content.SchemaVersion);
        Assert.Equal("SetDns", roundTrip.RelatedAction);
        Assert.Equal("corr-1", roundTrip.CorrelationId);
        Assert.Single(roundTrip.Content.Adapters);
        Assert.Equal("normal", roundTrip.Content.Tcp!.AutoTuningLevel);
        Assert.True(roundTrip.Content.Proxy.Enabled);
    }

    [Fact]
    public async Task Capture_UnknownReason_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Create().CaptureAsync("Whatever", onLine: _ => { }));
    }

    // ── 列表 / 单查 / 删除 ──

    [Fact]
    public async Task List_EmptyDirectory_ReturnsEmpty()
    {
        Assert.Empty(await Create().ListAsync());
    }

    [Fact]
    public async Task List_CorruptFileSkipped_ValidOnesKept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "20260101-000000_Manual_broken.json"), "{{{ 不是 JSON");
        _info.Adapters.Add(Adapter("以太网"));
        await Create().CaptureAsync(NetworkSnapshotService.ReasonManual, onLine: _ => { });

        IReadOnlyList<NetworkSnapshotRecord> records = await Create().ListAsync();

        Assert.Single(records);
    }

    [Fact]
    public async Task List_OrderedByCreatedAtDescending()
    {
        WriteSnapshotFile(MakeRecord("older", new DateTime(2026, 1, 1, 8, 0, 0)));
        WriteSnapshotFile(MakeRecord("newer", new DateTime(2026, 9, 6, 20, 0, 0)));

        IReadOnlyList<NetworkSnapshotRecord> records = await Create().ListAsync();

        Assert.Equal(["newer", "older"], records.Select(r => r.Name));
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        Assert.Null(await Create().GetAsync("no-such-id"));
    }

    [Fact]
    public async Task Delete_RemovesSnapshotFile()
    {
        _info.Adapters.Add(Adapter("以太网"));
        NetworkSnapshotRecord record = await Create().CaptureAsync(NetworkSnapshotService.ReasonManual, onLine: _ => { });

        await Create().DeleteAsync(record.Id);

        Assert.Empty(await Create().ListAsync());
    }

    // ── 恢复 ──

    [Fact]
    public async Task Restore_UnknownId_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().RestoreAsync("no-such-id", _ => { }));
    }

    [Fact]
    public async Task Restore_CapturesBeforeChangeSnapshotFirst()
    {
        // 修改类操作 100% 快照纪律：还原本身也是修改，先留一份当前状态的后悔药
        _info.Adapters.Add(Adapter("以太网"));
        NetworkSnapshotRecord record = await Create().CaptureAsync(NetworkSnapshotService.ReasonManual, onLine: _ => { });

        await Create().RestoreAsync(record.Id, _ => { });

        IReadOnlyList<NetworkSnapshotRecord> after = await Create().ListAsync();
        Assert.Contains(after, r => r.Reason == NetworkSnapshotService.ReasonBeforeChange && r.RelatedAction == "RestoreSnapshot");
    }

    [Fact]
    public async Task Restore_ReplaysAdapterIpv4AndDns()
    {
        // 快照：DHCP 适配器（DNS 主备）+ 静态 IP 适配器（/8 → 255.0.0.0，主网关）
        NetAdapterInfo dhcpAdapter = Adapter("以太网", isDhcp: true, dns: new[] { "223.5.5.5", "223.6.6.6" });
        NetAdapterInfo staticAdapter = Adapter("LAN", isDhcp: false, ipv4: new[] { "10.0.0.5/8" }, gateways: new[] { "10.0.0.254" });
        NetworkSnapshotRecord record = MakeRecord("x", DateTime.Now,
            content: new NetworkSnapshotContent("1.0", new[] { dhcpAdapter, staticAdapter }, null,
                Array.Empty<InterfaceMetricInfo>(), new ProxyInfo(false, null)));
        WriteSnapshotFile(record);
        _info.Adapters.Add(Adapter("以太网")); // 当前仍存在 → 回放
        _info.Adapters.Add(Adapter("LAN"));

        await Create().RestoreAsync(record.Id, _ => { });

        Assert.Contains("dhcp:以太网", _config.Calls);
        Assert.Contains("dns:以太网:223.5.5.5/223.6.6.6", _config.Calls);
        Assert.Contains("static:LAN:10.0.0.5/255.0.0.0/10.0.0.254", _config.Calls);
        Assert.Contains("dns:LAN:dhcp/-", _config.Calls); // 静态适配器快照里无 DNS → 恢复自动获取
    }

    [Fact]
    public async Task Restore_AdapterNoLongerPresent_SkipsItsConfig()
    {
        NetAdapterInfo ghost = Adapter("Ghost");
        NetworkSnapshotRecord record = MakeRecord("x", DateTime.Now,
            content: new NetworkSnapshotContent("1.0", new[] { ghost }, null,
                Array.Empty<InterfaceMetricInfo>(), new ProxyInfo(false, null)));
        WriteSnapshotFile(record);
        _info.Adapters.Clear(); // 当前无任何适配器

        var lines = new List<string>();
        await Create().RestoreAsync(record.Id, lines.Add);

        Assert.Empty(_config.Calls);
        Assert.Contains(lines, l => l.Contains("Ghost") && l.Contains("已不存在"));
    }

    [Fact]
    public async Task Restore_ReplaysTcpMetricsAndProxy()
    {
        var snapshotTcp = new TcpGlobalSettings("normal", true, false, 10);
        NetworkSnapshotRecord record = MakeRecord("x", DateTime.Now,
            content: new NetworkSnapshotContent("1.0", Array.Empty<NetAdapterInfo>(), snapshotTcp,
                new[] { new InterfaceMetricInfo("LAN", 25) }, new ProxyInfo(true, "127.0.0.1:7890")));
        WriteSnapshotFile(record);

        await Create().RestoreAsync(record.Id, _ => { });

        TcpGlobalSettings applied = Assert.Single(_tuning.Applied);
        Assert.Equal("normal", applied.AutoTuningLevel);
        Assert.Null(applied.InitialRto); // 只读展示字段不回放
        Assert.Equal(("LAN", 25), _tuning.AppliedMetrics.Single());
        Assert.Equal((true, "127.0.0.1:7890"), _info.SetProxyCalls.Single());
    }

    // ── 纯函数 ──

    [Theory]
    [InlineData(0, "0.0.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(32, "255.255.255.255")]
    public void PrefixToMask_KnownPrefixes_ConvertsCorrectly(int prefix, string expected)
    {
        Assert.Equal(expected, NetworkSnapshotService.PrefixToMask(prefix));
    }

    [Theory]
    [InlineData(33)]
    [InlineData(-1)]
    public void PrefixToMask_OutOfRange_Throws(int prefix)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkSnapshotService.PrefixToMask(prefix));
    }

    // ── 容量淘汰 ──

    [Fact]
    public async Task Capture_OverMaxKeep_PrunesOldestKeepsNewest()
    {
        // 【回归】淘汰筛选曾用 "snapshot_*.json" 模式而真实文件名是时间前缀——永不匹配的死代码
        Directory.CreateDirectory(_directory);
        DateTime baseTime = new(2026, 1, 1, 0, 0, 0);
        for (int i = 0; i < NetworkSnapshotService.MaxKeep + 3; i++)
        {
            string name = $"{baseTime.AddSeconds(i):yyyyMMdd-HHmmss}_Manual_{i}.json";
            File.WriteAllText(Path.Combine(_directory, name), "{}");
        }

        _info.Adapters.Add(Adapter("以太网"));
        await Create().CaptureAsync(NetworkSnapshotService.ReasonManual, onLine: _ => { });

        string[] files = Directory.GetFiles(_directory, "*.json");
        Assert.Equal(NetworkSnapshotService.MaxKeep, files.Length);
        Assert.DoesNotContain(files, f => f.Contains("_Manual_0.json"));  // 最旧的被淘汰
        Assert.DoesNotContain(files, f => f.Contains("_Manual_2.json"));
        Assert.Contains(files, f => f.Contains($"_Manual_{NetworkSnapshotService.MaxKeep + 2}.json")); // 最新保留
    }

    private static NetworkSnapshotRecord MakeRecord(
        string name, DateTime createdAt, NetworkSnapshotContent? content = null)
        => new(Guid.NewGuid().ToString("N"), name, NetworkSnapshotService.ReasonManual,
            null, null, createdAt,
            content ?? new NetworkSnapshotContent("1.0", Array.Empty<NetAdapterInfo>(), null,
                Array.Empty<InterfaceMetricInfo>(), new ProxyInfo(false, null)));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
