using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.ElevatedHelper;

/// <summary>
/// Helper 内的 netsh / ipconfig / arp 执行器：收集子进程原始字节后按
/// <see cref="OutputDecoder"/> 候选链择优解码——【真机实测】netsh 与 ipconfig 的重定向输出
/// 编码不一致（netsh 可 UTF-8、ipconfig 是 GBK），照抄 pnputil 通道的固定编码解码必然一边乱码。
/// 解码策略与主进程 <c>CommandRunner</c> 同源（含 CodePagesEncodingProvider 注册防线的教训）。
/// </summary>
internal static class NetRunner
{
    static NetRunner()
    {
        // cp936 等非 UTF 系代码页必须注册 CodePagesEncodingProvider（主进程 CommandRunner 同款防线）
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 注册失败（重复注册等）：不阻断，候选链还有 UTF-8 兜底
        }

        DecoderCandidates = OutputDecoder.BuildCandidates(GetOEMCP(), GetACP());
    }

    // 无初始化器：候选构造依赖静态构造器内的注册（顺序陷阱见 CommandRunner 注释）
    private static readonly IReadOnlyList<Encoding> DecoderCandidates;

    /// <summary>执行一条命令并返回（退出码, 解码后输出）。不设内部超时：客户端负责终止 Helper 进程树。</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 整串传递：netsh 自带解析器，name="…" 的内嵌引号必须原样到达
            Arguments = arguments,
        };

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        Task stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdout);
        Task stderrTask = proc.StandardError.BaseStream.CopyToAsync(stderr);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        byte[] so = stdout.ToArray();
        byte[] se = stderr.ToArray();
        Encoding encoding = OutputDecoder.PickBest(so.Length > 0 ? so : se, DecoderCandidates);
        var sb = new StringBuilder();
        OutputDecoder.ForEachLine(encoding.GetString(so), line => sb.AppendLine(line));
        OutputDecoder.ForEachLine(encoding.GetString(se), line => sb.AppendLine(line));
        return (proc.ExitCode, sb.ToString());
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
