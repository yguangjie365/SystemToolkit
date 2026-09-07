using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 修复服务测试（fake ICommandRunner）：命令构造、安全序列范围、失败即停、
/// 适配器自动选取。真实 netsh / ipconfig 是系统边界，不做单测。自旧工程移植，L15 英文命名。
/// </summary>
public class NetRepairServiceTests
{
    private sealed class FakeRunner : ICommandRunner
    {
        public List<(string FileName, string Args)> Invocations { get; } = new();

        public List<string> Lines { get; } = new();

        public int ExitCode { get; set; }

        public Task<int> RunAsync(string fileName, string arguments, Action<string> onLine, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Invocations.Add((fileName, arguments));
            onLine("(fake) ok");
            Lines.Add($"{fileName} {arguments}");
            return Task.FromResult(ExitCode);
        }
    }

    private readonly FakeRunner _runner = new();
    private readonly NetworkTestFakes.FakeInfoService _info = new();

    private NetRepairService Create() => new(_runner, _info);

    private static NetAdapterInfo UpAdapter(string name = "以太网") => new(
        name, "fake", NetType.Ethernet, OperStatus.Up, 1000, "AA", true,
        new[] { "192.168.1.10/24" }, new[] { "192.168.1.1" }, Array.Empty<string>());

    [Fact]
    public void Steps_SixEntries_WithAdminAndRebootFlags()
    {
        IReadOnlyList<RepairStepDescriptor> steps = Create().Steps;

        Assert.Equal(6, steps.Count);
        Assert.Equal(new[] { "flushdns", "renew", "bounce", "arpclear", "winsockreset", "ipreset" }, steps.Select(s => s.Id));
        Assert.All(steps.Where(s => s.Id is "bounce" or "arpclear" or "winsockreset" or "ipreset"), s => Assert.True(s.RequiresAdmin));
        Assert.All(steps.Where(s => s.Id is "flushdns" or "renew"), s => Assert.False(s.RequiresAdmin));
        Assert.True(steps.Single(s => s.Id == "winsockreset").RequiresReboot);
        Assert.True(steps.Single(s => s.Id == "ipreset").RequiresReboot);
        Assert.False(steps.Single(s => s.Id == "bounce").RequiresReboot);
    }

    [Fact]
    public async Task RunSafeSequence_ExecutesOnlyNonAdminNonRebootSteps()
    {
        _info.Adapters.Add(UpAdapter());

        IReadOnlyList<string> executed = await Create().RunSafeSequenceAsync(_ => { });

        Assert.Equal(new[] { "flushdns", "renew" }, executed);
        Assert.All(_runner.Invocations, i => Assert.Equal("ipconfig", i.FileName));
        Assert.Equal("/flushdns", _runner.Invocations[0].Args);
        Assert.Equal("/renew \"以太网\"", _runner.Invocations[1].Args);
    }

    [Fact]
    public async Task RunSafeSequence_FirstFailure_StopsImmediately()
    {
        _info.Adapters.Add(UpAdapter());
        _runner.ExitCode = 1;

        IReadOnlyList<string> executed = await Create().RunSafeSequenceAsync(_ => { });

        Assert.Empty(executed);
        Assert.Single(_runner.Invocations); // 只跑了第一条就停
    }

    [Fact]
    public async Task ExecuteWithoutAdapter_AutoSelectsFirstConnected_AndLogs()
    {
        _info.Adapters.Add(UpAdapter("以太网"));
        var lines = new List<string>();

        await Create().ExecuteAsync("renew", lines.Add);

        Assert.Contains(lines, l => l.Contains("自动选用首选已连接适配器：以太网"));
    }

    [Fact]
    public async Task AutoAdapterSelect_PrefersPhysicalOverVirtual()
    {
        // 【审查修复 2.4】虚拟网卡（Other 类型）Up 且枚举靠前——renew 对它无意义，必须跳过
        _info.Adapters.Add(new NetAdapterInfo("VirtualBox Host-Only", "fake", NetType.Other, OperStatus.Up, 0, "CC", false,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
        _info.Adapters.Add(UpAdapter("以太网"));

        var lines = new List<string>();
        await Create().ExecuteAsync("renew", lines.Add);

        Assert.Contains(lines, l => l.Contains("自动选用首选已连接适配器：以太网"));
        Assert.Contains("/renew \"以太网\"", _runner.Invocations.Select(i => i.Args));
    }

    [Fact]
    public async Task Bounce_DisableThenEnable_TwoCommands()
    {
        _info.Adapters.Add(UpAdapter());

        int exit = await Create().ExecuteAsync("bounce", _ => { }, "以太网");

        Assert.Equal(0, exit);
        Assert.Equal(2, _runner.Invocations.Count);
        Assert.Equal("interface set interface name=\"以太网\" admin=disable", _runner.Invocations[0].Args);
        Assert.Equal("interface set interface name=\"以太网\" admin=enable", _runner.Invocations[1].Args);
    }

    [Fact]
    public async Task Bounce_DisableFails_SkipsEnable()
    {
        _info.Adapters.Add(UpAdapter());
        _runner.ExitCode = 1;

        int exit = await Create().ExecuteAsync("bounce", _ => { }, "以太网");

        Assert.Equal(1, exit);
        Assert.Single(_runner.Invocations);
    }

    [Fact]
    public async Task Execute_AdapterNeededButNoneConnected_FailsWithoutProcess()
    {
        // 只有一个 Down 适配器
        _info.Adapters.Add(new NetAdapterInfo("以太网", "fake", NetType.Ethernet, OperStatus.Down, 1000, "AA", true,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));

        int exit = await Create().ExecuteAsync("renew", _ => { });

        Assert.Equal(-1, exit);
        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task ArpClear_EmitsArpCommand()
    {
        int exit = await Create().ExecuteAsync("arpclear", _ => { });

        Assert.Equal(0, exit);
        (string file, string args) = Assert.Single(_runner.Invocations);
        Assert.Equal("arp", file);
        Assert.Equal("-d *", args);
    }

    [Fact]
    public async Task ResetCommands_RouteThroughNetsh()
    {
        await Create().ExecuteAsync("winsockreset", _ => { });
        await Create().ExecuteAsync("ipreset", _ => { });

        Assert.Equal("winsock reset", _runner.Invocations[0].Args);
        Assert.Equal("int ip reset", _runner.Invocations[1].Args);
        Assert.All(_runner.Invocations, i => Assert.Equal("netsh", i.FileName));
    }

    [Fact]
    public async Task UnknownStepId_ThrowsWithoutProcess()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create().ExecuteAsync("not-a-step", _ => { }));

        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task EveryCommand_EchoedBeforeExecution()
    {
        var lines = new List<string>();

        await Create().ExecuteAsync("flushdns", lines.Add);

        Assert.Equal("$ ipconfig /flushdns", lines[0]);
        Assert.Equal("(fake) ok", lines[1]);
    }
}
