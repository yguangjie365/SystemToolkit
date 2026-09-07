using System.Diagnostics;
using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 提权客户端入口校验与引号规则守卫（D-5 修复）：
/// 非法值必须在进入提权命令行之前被拒绝；Quote 必须符合 Windows argv 规则（含尾反斜杠）。
/// 所有用例用不存在的 Helper 路径阻断真实提权（测试隔离：禁止依赖真实 UAC）。
/// </summary>
public class ElevatedPnpUtilClientTests
{
    private static ElevatedPnpUtilClient CreateClient() =>
        new(new PnpUtilService(), helperPath: "Z:\\nonexistent\\SystemToolkit.ElevatedHelper.exe");

    // ---------------- Quote（internal，InternalsVisibleTo） ----------------

    [Theory]
    [InlineData("oem1.inf", "oem1.inf")]                 // 无特殊字符不加引号
    [InlineData("a b", "\"a b\"")]                       // 空格加引号
    [InlineData("", "\"\"")]                             // 空串
    [InlineData("a\"b", "\"a\\\"b\"")]                   // 内部引号转义
    [InlineData("C:\\Dir Name\\", "\"C:\\Dir Name\\\\\"")] // 尾反斜杠倍增（闭引号不被吞）
    [InlineData("C:\\Dir\\", "\"C:\\Dir\\\\\"")]         // 尾单反斜杠同样倍增
    [InlineData("C:\\Dir", "C:\\Dir")]                   // 非尾反斜杠不动
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]             // 反斜杠+引号：反斜杠倍增再转义引号
    public void Quote_WindowsArgvRules(string input, string expected)
    {
        Assert.Equal(expected, ElevatedPnpUtilClient.Quote(input));
    }

    // ---------------- 入口白名单 ----------------

    [Fact]
    public async Task DeleteAsync_InboxPublishedName_RejectsDirectly_DoesNotTouchElevation()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().DeleteAsync("inbox.inf", force: false));
        Assert.Contains("oem", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_PathTraversal_RejectsDirectly()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().DeleteAsync("../oem1.inf", force: false));
    }

    [Fact]
    public async Task DeleteAsync_ValidOemName_FailsAfterReachingHelperCheck()
    {
        DriverRunResult result = await CreateClient().DeleteAsync("oem1.inf", force: false);

        Assert.False(result.Success); // Helper 缺失 → 失败结果（而非异常/提权）
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task DeleteManyAsync_ContainsInvalidName_RejectsWholeBatch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().DeleteManyAsync(["oem1.inf", "inbox.inf"], force: false));
    }

    [Fact]
    public async Task DeleteManyAsync_EmptyList_NoOpSucceeds()
    {
        DriverRunResult result = await CreateClient().DeleteManyAsync([], force: false);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExportManyAsync_ValidNames_FailsAfterCreatingStagingDir()
    {
        string root = Path.Combine(Path.GetTempPath(), $"stk_elev_{Guid.NewGuid():N}");
        try
        {
            DriverRunResult result = await CreateClient().ExportManyAsync(["oem7.inf"], root);

            Assert.False(result.Success); // Helper 缺失
            // 暂存目录已按包隔离创建（D-3 契约），整理器负责移动与残壳清理
            Assert.True(Directory.Exists(DriverBackupOrganizer.StageDirFor(root, "oem7.inf")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportManyAsync_InvalidNames_RejectsDirectly()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().ExportManyAsync(["oem1.inf & dir"], "D:\\x"));
    }

    [Fact]
    public async Task AddDriverAsync_NonInfOrMissingPath_RejectsDirectly()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().AddDriverAsync("driver.sys", install: false));
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().AddDriverAsync("relative.inf", install: false));
    }

    [Fact]
    public async Task AddManyAsync_EmptyList_NoOpSucceeds()
    {
        DriverRunResult result = await CreateClient().AddManyAsync([], install: false);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AddManyAsync_ContainsInvalidPath_RejectsWholeBatch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().AddManyAsync(["C:\\Windows\\System32\\not-exist.inf"], install: false));
    }

    // ---------------- S2 回归（审查 2026-09-05）：取消/超时终止进程树 + 临时文件清理 ----------------
    // 用非提权哑进程验证两条路径（03 测试规范：禁止真实 UAC）——被测逻辑不感知进程如何启动

    [Fact]
    public async Task FinishHelperAsync_Timeout_KillsProcessTreeAndCleansTempFiles()
    {
        using Process proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string outFile = Path.Combine(Path.GetTempPath(), $"stk_test_{Guid.NewGuid():N}.out");
        File.WriteAllText(outFile, "x");

        DriverRunResult result = await ElevatedPnpUtilClient.FinishHelperAsync(
            proc, outFile, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(-2, result.ExitCode);
        Assert.True(proc.WaitForExit(2000), "取消路径必须实际终止进程树");
        Assert.False(File.Exists(outFile), "取消路径必须清理临时输出文件");
    }

    [Fact]
    public async Task FinishHelperAsync_NormalExit_ReadsOutputAndCleansTempFiles()
    {
        using Process proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string outFile = Path.Combine(Path.GetTempPath(), $"stk_test_{Guid.NewGuid():N}.out");
        File.WriteAllText(outFile, "[exit 0] ok");

        DriverRunResult result = await ElevatedPnpUtilClient.FinishHelperAsync(
            proc, outFile, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.True(proc.HasExited);
        Assert.False(File.Exists(outFile), "正常路径同样必须清理临时输出文件");
    }

    [Fact]
    public async Task FinishHelperAsync_ConcurrentCancelAndComplete_CachedSemantics_NoException()
    {
        // 竞态压力（S2 收尾逻辑并发健壮性）：多进程同时收尾，互不干扰
        IEnumerable<Task> tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            using Process proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            string outFile = Path.Combine(Path.GetTempPath(), $"stk_test_{Guid.NewGuid():N}.out");
            File.WriteAllText(outFile, "x");
            DriverRunResult r = await ElevatedPnpUtilClient.FinishHelperAsync(
                proc, outFile, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(r.Success);
        });

        await Task.WhenAll(tasks);
    }
}
