using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// pnputil 封装实现。成熟方案优先：枚举/导出/删除/添加全部走 pnputil 子命令
/// （与 DriverStoreExplorer 的官方机制路线一致），不自写 SetupAPI 互操作。
/// 输出按系统 OEM 代码页解码（中文系统为 GBK 系，UTF-8 强解会乱码导致解析失败）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class PnpUtilService : IPnpUtilClient
{
    /// <summary>enum-drivers 属只读查询但输出可能很大，给足量超时。</summary>
    private static readonly TimeSpan EnumTimeout = TimeSpan.FromSeconds(Configuration.AppConstants.PnpUtilEnumTimeoutSeconds);

    /// <summary>导出单包可能上百 MB，走写操作量级超时。</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(Configuration.AppConstants.PnpUtilWriteTimeoutMinutes);

    private static Encoding? _outputEncoding;

    private readonly string _pnputilPath;

    /// <summary>注入 pnputil.exe 路径（测试可指向假可执行）；缺省取 System32 下的官方副本。</summary>
    public PnpUtilService(string? pnputilPath = null)
    {
        _pnputilPath = pnputilPath
            ?? Path.Combine(Environment.SystemDirectory, "pnputil.exe");
    }

    /// <summary>枚举 Driver Store 全部驱动包：优先 XML 结构化输出，老系统回退文本解析。</summary>
    public async Task<IReadOnlyList<DriverPackage>> EnumDriversAsync(CancellationToken ct = default)
    {
        // v2.0（2026-09-05）：优先官方结构化 XML（含 Signer/ExtensionId/文件数/官方设备关联，
        // 本机 Win11 26300 实测支持）。XML 失败（老系统不支持 /format）时回退文本解析。
        try
        {
            (int exitCode, string output) = await RunAsync(
                ["/enum-drivers", "/devices", "/files", "/format", "xml"], ct, EnumTimeout).ConfigureAwait(false);
            if (exitCode == 0)
            {
                List<DriverPackage> packages = ParseEnumXml(output);
                if (packages.Count > 0)
                {
                    return packages;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 回退文本路径
        }

        (int textExit, string textOutput) = await RunAsync(["/enum-drivers"], ct, EnumTimeout).ConfigureAwait(false);
        if (textExit != 0)
        {
            throw new InvalidOperationException($"pnputil /enum-drivers 失败（退出码 {textExit}）：{Truncate(textOutput)}");
        }

        return ParseEnumOutput(textOutput);
    }

    /// <summary>导出单个驱动包到目标目录（目录必须已存在）。</summary>
    public Task<DriverRunResult> ExportAsync(string publishedName, string destinationDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        return RunWriteAsync(["/export-driver", publishedName, destinationDir], ct);
    }

    /// <summary>直连版逐包导出到独立暂存目录（对齐提权版 D-3 契约：规避同名原始 INF 相互覆盖；
    /// 提权场景请用 ElevatedPnpUtilClient 的单次 UAC 版）。</summary>
    public async Task<DriverRunResult> ExportManyAsync(IReadOnlyList<string> publishedNames, string destinationDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        foreach (string name in publishedNames)
        {
            if (!PnpUtilTokenRules.IsValidInfFileName(name))
            {
                throw new ArgumentException($"非法的驱动包发布名：{name}", nameof(publishedNames));
            }

            string stageDir = DriverBackupOrganizer.StageDirFor(destinationDir, name);
            Directory.CreateDirectory(stageDir); // pnputil 要求目标目录已存在
            DriverRunResult result = await ExportAsync(name, stageDir, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return result;
            }
        }

        return new DriverRunResult(true, 0, $"{publishedNames.Count} 个驱动包全部导出成功（按包独立暂存目录）");
    }

    /// <summary>删除驱动包（/delete-driver），目标经 oem 白名单校验；force = 连同设备关联强制删除。</summary>
    public Task<DriverRunResult> DeleteAsync(string publishedName, bool force, CancellationToken ct = default)
    {
        if (!PnpUtilTokenRules.IsDeletablePublishedName(publishedName))
        {
            throw new ArgumentException($"删除目标必须是第三方 oem 包发布名：{publishedName}", nameof(publishedName));
        }

        List<string> args = ["/delete-driver", publishedName];
        if (force)
        {
            args.Add("/force");
        }

        return RunWriteAsync(args, ct);
    }

    /// <summary>直连版逐包顺序删除（提权场景请用 ElevatedPnpUtilClient 的单次 UAC 版）。目标白名单同 <see cref="DeleteAsync"/>。</summary>
    public async Task<DriverRunResult> DeleteManyAsync(IReadOnlyList<string> publishedNames, bool force, CancellationToken ct = default)
    {
        foreach (string name in publishedNames)
        {
            DriverRunResult result = await DeleteAsync(name, force, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return result;
            }
        }

        return new DriverRunResult(true, 0, $"{publishedNames.Count} 个驱动包全部删除成功");
    }

    /// <summary>添加 INF 驱动包到 Driver Store（可选 /install 安装到匹配设备），双重成功判定。</summary>
    public async Task<DriverRunResult> AddDriverAsync(string infPath, bool install, CancellationToken ct = default)
    {
        if (!PnpUtilTokenRules.IsValidInfPathForAdd(infPath))
        {
            throw new ArgumentException($"INF 路径必须是已存在的绝对路径且以 .inf 结尾：{infPath}", nameof(infPath));
        }

        List<string> args = ["/add-driver", infPath];
        if (install)
        {
            args.Add("/install");
        }

        // 双重判定（RAPR 经验）：退出码 0 之外还须比对输出「总数/已添加数」
        DriverRunResult result = await RunWriteAsync(args, ct).ConfigureAwait(false);
        return PnpUtilOutputAnalyzer.VerifyAddResult(result);
    }

    /// <summary>直连版逐 INF 顺序添加（提权场景请用 ElevatedPnpUtilClient 的单次 UAC 版）；逐个保留计数双判。</summary>
    public async Task<DriverRunResult> AddManyAsync(IReadOnlyList<string> infPaths, bool install, CancellationToken ct = default)
    {
        foreach (string infPath in infPaths)
        {
            DriverRunResult result = await AddDriverAsync(infPath, install, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return result;
            }
        }

        return new DriverRunResult(true, 0, $"{infPaths.Count} 个 INF 全部添加成功");
    }


    private static string Truncate(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    // ------------------------------------------------------------------
    // 进程执行（参数逐项传递，不拼接命令行；路径由 ArgumentList 自动加引号）
    // ------------------------------------------------------------------
    private async Task<DriverRunResult> RunWriteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        (int exitCode, string output) = await RunAsync(args, ct, WriteTimeout).ConfigureAwait(false);
        return new DriverRunResult(exitCode == 0, exitCode, output);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(_pnputilPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ResolveOutputEncoding(),
            StandardErrorEncoding = ResolveOutputEncoding(),
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = new() { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException("pnputil 进程启动失败");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            // 补同步等待排空异步重定向缓冲（教训见 WingetService 同款修复）
            proc.WaitForExit();
            return (proc.ExitCode, output.ToString());
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }

            throw;
        }
    }

    /// <summary>
    /// pnputil 控制台输出走本机 OEM 代码页（中文系统 GBK 系）。
    /// 解析顺序：注册表 OEMCP（最可靠，GUI 进程里 Console.OutputEncoding 不可信）→ Console → 936 → UTF8 兜底。
    /// 编码错了中文标签会乱码，关键词全部失配——这是"列显示为空"事故（2026-09-04）的直接根因之一。
    /// </summary>
    private static Encoding ResolveOutputEncoding()
    {
        if (_outputEncoding is not null)
        {
            return _outputEncoding;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Nls\CodePage");
            if (key?.GetValue("OEMCP") is string oemcp
                && int.TryParse(oemcp, out int codePage)
                && Encoding.GetEncoding(codePage) is { } fromRegistry)
            {
                _outputEncoding = fromRegistry;
                return _outputEncoding;
            }
        }
        catch
        {
            // 注册表不可读则走后续兜底
        }

        try
        {
            _outputEncoding = Console.OutputEncoding;
        }
        catch
        {
            _outputEncoding = null;
        }

        if (_outputEncoding is null)
        {
            try
            {
                _outputEncoding = Encoding.GetEncoding(936);
            }
            catch
            {
                _outputEncoding = Encoding.UTF8;
            }
        }

        return _outputEncoding;
    }
}
