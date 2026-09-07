using System.Diagnostics;

namespace SystemToolkit.Core.Elevated;

/// <summary>提权进程执行结果（成功 = 退出码 0；Output = 结果文件回传的完整输出）。</summary>
public sealed record ElevatedRunResult(bool Success, int ExitCode, string Output);

/// <summary>
/// 提权 Helper 进程收尾（跨域共用：驱动 <c>ElevatedPnpUtilClient</c> 与网络
/// <c>ElevatingCommandRunner</c> 同一模式，2026-09-06 自驱动侧抽出）。
/// <para>
/// 等待退出 → 读取结果文件 → 取消/超时则尽力终止整棵进程树 → 两条路径都清理临时文件。
/// 🔴 取消纪律（审查 2026-09-05 S2）：取消/超时必须终止 Helper——否则用户以为已中止，
/// 提权操作仍在后台继续执行。internal 逻辑经各客户端的 internal 包装直测：
/// 测试用非提权哑进程覆盖两条路径（03 测试规范：禁止真实 UAC）。
/// </para>
/// </summary>
public static class ElevatedProcessFinisher
{
    /// <summary>收尾提权进程并回读结果文件。</summary>
    public static async Task<ElevatedRunResult> FinishAsync(
        Process proc, string outFile, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        string output = "";
        bool cancelled = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            output = File.Exists(outFile)
                ? await File.ReadAllTextAsync(outFile, System.Text.Encoding.UTF8).ConfigureAwait(false)
                : "";
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }
        }
        finally
        {
            try
            {
                File.Delete(outFile);
            }
            catch
            {
                // 临时文件清理尽力而为
            }
        }

        return cancelled
            ? new ElevatedRunResult(false, -2, "提权操作超时或被取消（已尝试终止提权进程树，请刷新查看实际结果）")
            : new ElevatedRunResult(proc.ExitCode == 0, proc.ExitCode, output);
    }
}
