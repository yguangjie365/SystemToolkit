using System.Text.Json;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// TCP 调优服务测试：快照 round-trip（真实临时文件）、无快照拒绝还原、
/// 应用命令流（fake ICommandRunner：只对 show global 返回样例输出，其余命令返回退出码）。
/// NetworkThrottlingIndex 的注册表读写是系统边界——涉及注册表的还原路径不做单测，
/// 快照里 throttling 为 null 时还原只走 netsh（可测）。自旧工程移植，L15 英文命名。
/// </summary>
public class TcpTuningSnapshotTests : IDisposable
{
    private sealed class FakeRunner : ICommandRunner
    {
        private const string ShowGlobalArgs = "interface tcp show global";
        private const string ShowInterfacesArgs = "interface ipv4 show interfaces";

        public List<string> ShowOutput { get; set; } = new();

        public List<string> ShowInterfacesOutput { get; set; } = new();

        public List<(string FileName, string Args)> Invocations { get; } = new();

        public int ExitCode { get; set; }

        public Task<int> RunAsync(string fileName, string arguments, Action<string> onLine, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Invocations.Add((fileName, arguments));
            if (arguments == ShowGlobalArgs)
            {
                foreach (string line in ShowOutput)
                {
                    onLine(line);
                }
            }
            else if (arguments == ShowInterfacesArgs)
            {
                foreach (string line in ShowInterfacesOutput)
                {
                    onLine(line);
                }
            }

            return Task.FromResult(ExitCode);
        }
    }

    private static readonly string[] InterfaceSample =
    {
        "Idx     Met         MTU          状态                名称",
        "---  ----------  ----------  ------------  ---------------------------",
        "  1          75  4294967295  connected     Loopback Pseudo-Interface 1",
        "  8          25        1500  connected     LAN",
    };

    private static readonly string[] ZhSample =
    {
        "TCP 全局参数",
        "接收方缩放状态                : enabled",
        "窗口自动调节级别              : normal",
        "ECN 能力                      : disabled",
    };

    private readonly string _snapshotPath;
    private readonly FakeRunner _runner;

    public TcpTuningSnapshotTests()
    {
        _snapshotPath = Path.Combine(Path.GetTempPath(), $"tuning-test-{Guid.NewGuid():N}.json");
        _runner = new FakeRunner { ShowOutput = ZhSample.ToList() };
    }

    private TcpTuningService Create() => new(_runner, _snapshotPath);

    [Fact]
    public void Initial_NoSnapshot()
    {
        Assert.False(Create().HasSnapshot);
    }

    [Fact]
    public async Task Apply_AutoSnapshot_CapturesBeforeState()
    {
        TcpTuningService service = Create();

        // 改前：normal / enabled / disabled；目标：禁用自动调谐
        await service.ApplyAsync(new TcpGlobalSettings("disabled", null, null, null), _ => { });

        Assert.True(service.HasSnapshot);

        // 与写入侧使用同一命名策略（snake_case），否则属性对不上、NetshValues 为 null
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        TuningSnapshot? snapshot = JsonSerializer.Deserialize<TuningSnapshot>(File.ReadAllText(_snapshotPath), jsonOptions);
        Assert.NotNull(snapshot);
        Assert.Equal("normal", snapshot!.NetshValues["autotuninglevel"]);
        Assert.Equal("enabled", snapshot.NetshValues["rss"]);
        Assert.Equal("disabled", snapshot.NetshValues["ecncapability"]);
        // NetworkThrottlingIndex 来自真实注册表（系统边界），值依赖机器，不在单测断言范围
    }

    [Fact]
    public async Task Apply_DiffOnly_EmitsChangedCommands()
    {
        await Create().ApplyAsync(new TcpGlobalSettings("disabled", null, null, null), _ => { });

        // 只有 autotuninglevel 变了：rss / ecn / 注册表都不该有命令
        var setCommands = _runner.Invocations.Where(i => i.Args.StartsWith("interface tcp set global")).Select(i => i.Args).ToList();
        string setCommand = Assert.Single(setCommands);
        Assert.Equal("interface tcp set global autotuninglevel=disabled", setCommand);
    }

    [Fact]
    public async Task Apply_StateReadBack_ComesFromParser()
    {
        TcpTuningService service = Create();
        await service.ApplyAsync(new TcpGlobalSettings("disabled", null, null, null), _ => { });

        // fake runner 的 show 输出没变（模拟解析器视角），但服务把命令发出去了；
        // 状态以解析为准：ReadAsync 返回的仍是样例值——本用例验证命令流而非状态回读
        TcpGlobalSettings current = await service.ReadAsync();
        Assert.Equal("normal", current.AutoTuningLevel);
    }

    [Fact]
    public async Task Apply_AllGarbage_ZeroWritesWithSkipHints()
    {
        // 【审查修复 🔴1.1】show global 全乱码 → 解析全 null → 应用任何目标都必须零写入。
        // 「未知」绝不能被当成可比较的值去写——否则新版本 Windows 措辞一变，用户的
        // TCP 参数会被盲目重写，且快照里没有这些项、无法还原。
        _runner.ShowOutput = new List<string> { "??????", "###", "::::" };
        var lines = new List<string>();

        await Create().ApplyAsync(new TcpGlobalSettings("disabled", true, false, null), lines.Add);

        Assert.DoesNotContain(_runner.Invocations, i => i.Args.StartsWith("interface tcp set global"));
        Assert.Contains(lines, l => l.Contains("自动调谐级别当前值未知"));
        Assert.Contains(lines, l => l.Contains("RSS 当前值未知"));
        Assert.Contains(lines, l => l.Contains("ECN 当前值未知"));
    }

    [Fact]
    public async Task ApplyInterfaceMetric_SnapshotContainsAllInterfaces_AndCorrectCommand()
    {
        // 【M6c P1-6】应用 metric：快照同时记录 TCP 参数与全部接口 metric（一份改前快照）
        _runner.ShowInterfacesOutput = InterfaceSample.ToList();

        await Create().ApplyInterfaceMetricAsync("LAN", 5, _ => { });

        string json = File.ReadAllText(_snapshotPath);
        Assert.True(json.Contains("\"interface_metrics\""), "快照 JSON 缺 interface_metrics，实际：\n" + json);
        TuningSnapshot? snapshot = JsonSerializer.Deserialize<TuningSnapshot>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.NotNull(snapshot!.InterfaceMetrics);
        Assert.Equal(25, snapshot.InterfaceMetrics!["LAN"]);

        Assert.Contains(_runner.Invocations,
            i => i.Args == "interface ipv4 set interface name=\"LAN\" metric=5");
    }

    [Fact]
    public async Task ApplyInterfaceMetric_SameValue_SnapshotOnlyNoCommand()
    {
        _runner.ShowInterfacesOutput = InterfaceSample.ToList();

        await Create().ApplyInterfaceMetricAsync("LAN", 25, _ => { });

        Assert.DoesNotContain(_runner.Invocations,
            i => i.Args.Contains("set interface"));
    }

    [Fact]
    public async Task ApplyInterfaceMetric_MissingInterface_ErrorNoCommand()
    {
        var lines = new List<string>();

        await Create().ApplyInterfaceMetricAsync("不存在的网卡", 5, lines.Add);

        Assert.DoesNotContain(_runner.Invocations, i => i.Args.Contains("set interface"));
        Assert.Contains(lines, l => l.Contains("不存在"));
    }

    [Fact]
    public async Task Restore_InterfaceMetric_RevertsToSnapshotValue()
    {
        _runner.ShowInterfacesOutput = InterfaceSample.ToList(); // 改前状态（LAN=25）
        TcpTuningService service = Create();
        await service.ApplyInterfaceMetricAsync("LAN", 5, _ => { }); // 25 → 5

        // 模拟「应用已生效」：show interfaces 现在返回 5，还原时应写回快照值 25
        _runner.ShowInterfacesOutput = new List<string>
        {
            "Idx     Met         MTU          状态                名称",
            "---  ----------  ----------  ------------  ---------------------------",
            "  8           5        1500  connected     LAN",
        };
        int before = _runner.Invocations.Count;

        await service.RestoreAsync(_ => { });

        // 快照记录的改前值 25：还原时写回
        Assert.Contains(_runner.Invocations.Skip(before),
            i => i.Args == "interface ipv4 set interface name=\"LAN\" metric=25");
    }

    [Fact]
    public async Task Restore_LegacySnapshotWithoutInterfaceMetrics_SkipsGracefully()
    {
        // 【契约测试】Overview 缓存契约的教训：旧 JSON 文本钉死，防字段演化回归
        File.WriteAllText(_snapshotPath, """
			{
			  "captured_at": "2026-08-31T10:00:00",
			  "netsh_values": { "autotuninglevel": "normal" },
			  "network_throttling_index": 10
			}
			""");

        await Create().RestoreAsync(_ => { });

        // netsh 项正常还原（autotuninglevel），InterfaceMetrics 缺失 → 跳过不抛
        Assert.Contains(_runner.Invocations,
            i => i.Args == "interface tcp set global autotuninglevel=normal");
    }

    [Fact]
    public async Task Restore_NoSnapshot_Rejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().RestoreAsync(_ => { }));

        Assert.DoesNotContain(_runner.Invocations, i => i.Args.StartsWith("interface tcp set global"));
    }

    [Fact]
    public async Task Restore_ReplaysEverySnapshotKey()
    {
        TcpTuningService service = Create();
        await service.ApplyAsync(new TcpGlobalSettings("disabled", null, null, null), _ => { });
        int before = _runner.Invocations.Count;

        await service.RestoreAsync(_ => { });

        // 还原对快照里的每个键都发命令（还原语义 = 无条件回滚到改前状态）
        var restoreCommands = _runner.Invocations.Skip(before)
            .Where(i => i.Args.StartsWith("interface tcp set global")).Select(i => i.Args).ToList();
        Assert.Equal(3, restoreCommands.Count);
        Assert.Contains("interface tcp set global autotuninglevel=normal", restoreCommands);
        Assert.Contains("interface tcp set global rss=enabled", restoreCommands);
        Assert.Contains("interface tcp set global ecncapability=disabled", restoreCommands);
    }

    [Fact]
    public async Task Snapshot_JsonSnakeCase_WithTimestamp()
    {
        await Create().ApplyAsync(new TcpGlobalSettings("disabled", null, null, null), _ => { });

        string json = File.ReadAllText(_snapshotPath);
        Assert.Contains("\"captured_at\"", json);
        Assert.Contains("\"netsh_values\"", json);
        Assert.Contains("\"network_throttling_index\"", json);
    }

    void IDisposable.Dispose() => File.Delete(_snapshotPath);
}
