using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="ICommandRunner"/> 的真实实现：直连进程 + 按 OEM 代码页解码。
/// <para>
/// 【编码·已实测决策】winget 原生输出 UTF-8（WingetService 直连 + UTF-8 解码即可），
/// 但 netsh / ipconfig 走<b>控制台 OEM 代码页</b>（zh-CN = cp936）——照抄 winget 会全乱码。
/// 本类按 <c>GetOEMCP()</c> 解码；OEM 码不可用时回退 ANSI 代码页（GetACP），仍不可用退 UTF-8。
/// 刻意不走 <c>cmd /c chcp 65001</c>：那会引入 cmd 元字符解析层（引号内 %var% 仍展开），注入面更大。
/// </para>
/// <para>
/// 【审查修复·超时】支持 <c>timeout</c>：超时 / 外部取消时<b>终止整个进程树</b>并返回 -1。
/// 外部进程可能无限挂起（DHCP 无响应时 <c>ipconfig /renew</c> 可阻塞 30s+），
/// 不设超时会锁死 UI 的防重入门闩。进程启动 / 解码 / 超时属系统边界，不做单测，靠真机验收。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommandRunner : ICommandRunner
{
    // 【真机修复 2026-08-31】cp936 等非 UTF 系代码页必须注册 CodePagesEncodingProvider
    // 才能被 Encoding.GetEncoding 解析，否则抛 NotSupportedException → 静态构造炸 →
    // 所有 netsh/ipconfig 操作全灭（截图实证：刷新 DNS 缓存报 TypeInitializationException）。
    // 【C# 顺序陷阱】静态字段初始化器按声明顺序在静态构造器体内执行、先于其 body——
    // 因此 DecoderCandidates 必须声明为无初始化器字段、在注册之后于 body 内显式构造，
    // 否则候选里 936 会因「尚未注册」被静默跳过（实测踩过，测试互依赖曾掩盖）。
    static CommandRunner()
    {
        try
        {
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // 注册失败（重复注册等）：不阻断静态构造，候选链还有 UTF-8 兜底
        }

        // 解码候选（OEM → ACP → GBK(936) → UTF-8）。【真机修复】netsh 与 ipconfig 的
        // 重定向输出编码不一致（netsh 可 UTF-8 解析、ipconfig 是 GBK）——固定单一编码
        // 必然一边乱码，改为 RunAsync 结束后按替换符计数择优（OutputDecoder）。
        DecoderCandidates = OutputDecoder.BuildCandidates(GetOEMCP(), GetACP());
    }

    // 无初始化器：见静态构造器体内的注释（初始化顺序）
    private static readonly IReadOnlyList<Encoding> DecoderCandidates;

    /// <inheritdoc cref="ICommandRunner.RunAsync"/>
    public async Task<int> RunAsync(string fileName, string arguments, Action<string> onLine, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 不再指定解码编码：改从 BaseStream 收集原始字节，结束后按 OutputDecoder 择优解码
            // 整串传递：netsh 自带解析器，name="…" 的内嵌引号必须原样到达（见 ICommandRunner 备注）
            Arguments = arguments,
        };

        using Process proc = new() { StartInfo = startInfo };

        // 超时与外部取消合成一个令牌（先例：WingetService.RunProcessAsync）
        TimeSpan? timeoutValue = timeout;
        using CancellationTokenSource? timeoutCts = timeoutValue is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts?.CancelAfter(timeoutValue.GetValueOrDefault());
        CancellationToken effectiveCt = timeoutCts?.Token ?? ct;

        // REVIEW-3 G-8：输出收集任务在 try 外声明——catch（超时/取消）路径也要等待它们，
        // 否则 Kill 关流后的 CopyToAsync 以 faulted 收场成为 UnobservedTaskException 噪音
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;

        try
        {
            proc.Start();

            // 收集原始字节（stdout 与 stderr 分开），结束后统一择优解码。
            // 代价：日志行在命令结束后批量出现（netsh / ipconfig 均为秒级短输出，可接受）；
            // 收益：彻底消除「netsh 与 ipconfig 输出编码不一致」导致的中文乱码。
            stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdout, effectiveCt);
            stderrTask = proc.StandardError.BaseStream.CopyToAsync(stderr, effectiveCt);

            await proc.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            DecodeLines(stdout.ToArray(), stderr.ToArray(), onLine);

            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // 【审查修复】超时 / 取消时必须终止子进程：否则僵尸 netsh / ipconfig
            // 仍在后台改系统状态，且 UI 防重入门闩已被释放
            TryKill(proc, fileName, onLine);
            // REVIEW-3 G-8：Kill 关流后 CopyToAsync 可能以 IOException faulted 收场——
            // 不等待会成为 UnobservedTaskException 污染 crash 日志（有全局兜底不崩溃，但留噪音）
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // 输出收集失败本身无补救意义（进程已被杀，结果按 -1 处理）
            }
            onLine($"[进程] ❌ {fileName} 超时或被取消，已强制终止（退出码按 -1 处理）");
            return -1;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            onLine($"启动 {fileName} 失败：{ex.Message}");
            return -1;
        }
        catch (Exception ex)
        {
            // 【真机修复 2026-08-31】系统边界的任何意外都降级为 -1 + 日志，绝不穿透到 VM
            //（此前 DNS 应用时 TypeInitializationException 穿透命令链导致界面崩溃）
            onLine($"[进程] ❌ {fileName} 执行异常：{ex.Message}");
            return -1;
        }
    }

    /// <summary>按候选编码择优解码 stdout / stderr，并逐行回调（\r\n 切分）。</summary>
    private void DecodeLines(byte[] stdout, byte[] stderr, Action<string> onLine)
    {
        Encoding encoding = OutputDecoder.PickBest(stdout.Length > 0 ? stdout : stderr, DecoderCandidates);
        OutputDecoder.ForEachLine(encoding.GetString(stdout), onLine);
        OutputDecoder.ForEachLine(encoding.GetString(stderr), onLine);
    }

    private static void TryKill(Process proc, string fileName, Action<string> onLine)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            onLine($"[进程] ⚠️ 终止 {fileName} 失败：{ex.Message}（进程可能已自行退出）");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
