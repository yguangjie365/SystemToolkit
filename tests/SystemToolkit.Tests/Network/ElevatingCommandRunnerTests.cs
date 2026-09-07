using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 提权装饰器测试（2026-09-06 新增）：白名单外直连委托、白名单内经 Helper——
/// 真实 UAC 弹窗无法在测试中触发，Helper 路径用「程序缺失」验证编排与日志
/// （RunElevatedAsync 前置 File.Exists 检查 → -1）。UAC 拒绝（1223）与真 Helper
/// 端到端属系统边界，按驱动模块同款纪律不做自动化。
/// </summary>
public class ElevatingCommandRunnerTests
{
    private sealed class FakeRunner : ICommandRunner
    {
        public List<(string FileName, string Args)> Invocations { get; } = new();

        public int ExitCode { get; set; } = 0;

        public Task<int> RunAsync(string fileName, string arguments, Action<string> onLine, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Invocations.Add((fileName, arguments));
            return Task.FromResult(ExitCode);
        }
    }

    [Fact]
    public async Task NonWhitelistedCommand_DelegatesDirectlyToInner()
    {
        var inner = new FakeRunner();
        // helperPath 指向不存在的文件：若误走提权路径会返回 -1，本用例即失败
        var runner = new ElevatingCommandRunner(inner, helperPath: Path.Combine(Path.GetTempPath(), "no-such-helper.exe"));

        int exit = await runner.RunAsync("netsh", "interface tcp show global", _ => { });

        Assert.Equal(0, exit);
        (string file, string args) = Assert.Single(inner.Invocations);
        Assert.Equal("netsh", file);
        Assert.Equal("interface tcp show global", args);
    }

    [Fact]
    public async Task WhitelistedWrite_MissingHelper_ReturnsMinusOneWithLog()
    {
        var inner = new FakeRunner();
        var lines = new List<string>();
        var runner = new ElevatingCommandRunner(inner, helperPath: Path.Combine(Path.GetTempPath(), "no-such-helper.exe"));

        int exit = await runner.RunAsync("netsh", "winsock reset", lines.Add);

        Assert.Equal(-1, exit);
        Assert.Empty(inner.Invocations); // 不经内层直连（写命令必须提权）
        Assert.Contains(lines, l => l.Contains("提权辅助进程缺失"));
    }

    [Fact]
    public async Task ThrottlingWrite_MissingHelper_ReturnsMinusOneWithLog()
    {
        var inner = new FakeRunner();
        var lines = new List<string>();
        var runner = new ElevatingCommandRunner(inner, helperPath: Path.Combine(Path.GetTempPath(), "no-such-helper.exe"));

        int exit = await runner.RunThrottlingWriteAsync(0xFFFFFFFFu, lines.Add);

        Assert.Equal(-1, exit);
        Assert.Contains(lines, l => l.Contains("NetworkThrottlingIndex"));
        Assert.Contains(lines, l => l.Contains("提权辅助进程缺失"));
    }
}
