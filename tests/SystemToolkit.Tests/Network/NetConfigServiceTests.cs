using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// NetConfigService 经 fake ICommandRunner 验证：命令构造正确传递、退出码透传、
/// 非法输入在执行前拦截（不触碰真实 netsh 进程）。自旧工程移植，L15 英文命名。
/// </summary>
public class NetConfigServiceTests
{
    private sealed class FakeRunner : ICommandRunner
    {
        public List<(string FileName, string Args)> Invocations { get; } = new();

        public List<string> Lines { get; } = new();

        public int ExitCode { get; set; }

        public Task<int> RunAsync(string fileName, string arguments, Action<string> onLine, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Invocations.Add((fileName, arguments));
            onLine("(fake) 命令已执行");
            Lines.Add($"{fileName} {arguments}");
            return Task.FromResult(ExitCode);
        }
    }

    private readonly FakeRunner _runner = new();

    private NetConfigService Create() => new(_runner);

    [Fact]
    public async Task SetStaticIp_ValidInput_EmitsCommandAndPassesExitCode()
    {
        _runner.ExitCode = 0;

        int exit = await Create().SetStaticIpAsync("以太网", "192.168.1.10", "255.255.255.0", "192.168.1.1", _ => { });

        Assert.Equal(0, exit);
        (string file, string args) = Assert.Single(_runner.Invocations);
        Assert.Equal("netsh", file);
        Assert.Equal("interface ipv4 set address name=\"以太网\" source=static address=192.168.1.10 mask=255.255.255.0 gateway=192.168.1.1", args);
    }

    [Fact]
    public async Task SetStaticIp_InvalidIp_RejectedBeforeProcess()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Create().SetStaticIpAsync("以太网", "999.168.1.10", "255.255.255.0", null, _ => { }));

        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task SetStaticIp_InvalidGateway_RejectedBeforeProcess()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Create().SetStaticIpAsync("以太网", "192.168.1.10", "255.255.255.0", "abc", _ => { }));

        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task SetDns_PrimaryAndSecondary_EmitsTwoCommands()
    {
        int exit = await Create().SetDnsAsync("以太网", "223.5.5.5", "223.6.6.6", _ => { });

        Assert.Equal(0, exit);
        Assert.Equal(2, _runner.Invocations.Count);
        Assert.Equal("interface ipv4 set dnsservers name=\"以太网\" source=static address=223.5.5.5 register=primary", _runner.Invocations[0].Args);
        Assert.Equal("interface ipv4 add dnsservers name=\"以太网\" address=223.6.6.6 index=2", _runner.Invocations[1].Args);
    }

    [Fact]
    public async Task SetDns_NullPrimary_FallsBackToDhcpIgnoringSecondary()
    {
        int exit = await Create().SetDnsAsync("以太网", null, "223.6.6.6", _ => { });

        Assert.Equal(0, exit);
        (string _, string args) = Assert.Single(_runner.Invocations);
        Assert.Equal("interface ipv4 set dnsservers name=\"以太网\" source=dhcp", args);
    }

    [Fact]
    public async Task SetDns_InvalidSecondary_RejectedBeforeAnyCommand()
    {
        // 校验必须前移：不出现「主 DNS 已应用、备用格式非法抛异常」的半套状态
        await Assert.ThrowsAsync<ArgumentException>(
            () => Create().SetDnsAsync("以太网", "223.5.5.5", "not-an-ip", _ => { }));

        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task SetDns_FirstCommandFails_DoesNotAppendSecondary()
    {
        _runner.ExitCode = 1;

        int exit = await Create().SetDnsAsync("以太网", "223.5.5.5", "223.6.6.6", _ => { });

        Assert.Equal(1, exit);
        Assert.Single(_runner.Invocations);
    }

    [Fact]
    public async Task SetAdapterEnabled_EmitsCorrectCommand()
    {
        int exit = await Create().SetAdapterEnabledAsync("以太网", enabled: false, _ => { });

        Assert.Equal(0, exit);
        (string file, string args) = Assert.Single(_runner.Invocations);
        Assert.Equal("netsh", file);
        Assert.Equal("interface set interface name=\"以太网\" admin=disable", args);
    }

    [Fact]
    public async Task SetDhcp_AdapterNameContainsQuote_DegradesWithoutProcess()
    {
        // 【审查修复 2.1】构造防线抛出的 ArgumentException 在服务层转成降级，不穿透到 VM
        var lines = new List<string>();
        int exit = await Create().SetDhcpAsync("坏\"名字", lines.Add);

        Assert.Equal(-1, exit);
        Assert.Empty(_runner.Invocations);
        Assert.Contains(lines, l => l.Contains("命令构造被拒绝"));
    }

    [Fact]
    public async Task SetDhcp_EmitsCorrectCommand()
    {
        int exit = await Create().SetDhcpAsync("以太网", _ => { });

        Assert.Equal(0, exit);
        (string file, string args) = Assert.Single(_runner.Invocations);
        Assert.Equal("netsh", file);
        Assert.Equal("interface ipv4 set address name=\"以太网\" source=dhcp", args);
    }

    [Fact]
    public async Task SetStaticIp_ShortenedIpv4_RejectedByStrictValidation()
    {
        // 【审查修复 2.3】IPAddress.TryParse 会把 "1.2.3" 当 1.2.0.3 放行——必须拒绝
        await Assert.ThrowsAsync<ArgumentException>(
            () => Create().SetStaticIpAsync("以太网", "1.2.3", "255.255.255.0", null, _ => { }));

        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task EveryCommand_EchoedBeforeExecution()
    {
        var lines = new List<string>();

        await Create().SetDhcpAsync("以太网", lines.Add);

        Assert.Equal("$ netsh interface ipv4 set address name=\"以太网\" source=dhcp", lines[0]);
        Assert.Equal("(fake) 命令已执行", lines[1]);
    }
}
