using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using SystemToolkit.Core.Elevated;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// 提权版 pnputil 客户端：枚举（只读）直连；导出/删除/添加经 Elevated Helper 进程
/// （verb=runas → UAC）单次覆盖整批，输出经临时文件回传。
/// 🔴 纪律（03 设计 §6）：用户拒绝 UAC → 流程安全终止、返回失败结果、无副作用。
/// 🔴 入口白名单（05 安全设计）：publishedName/infPath 一律经 <see cref="PnpUtilTokenRules"/> 校验
/// 后才进入提权命令行——禁止依赖"调用方凑巧给对"。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedPnpUtilClient : IPnpUtilClient
{
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromMinutes(20);

    private readonly PnpUtilService _inner;
    private readonly string _helperPath;
    private readonly Func<string> _tempDirectoryProvider;

    /// <summary>注入非提权内层客户端、Helper 程序路径与临时目录提供器（测试可替换）。</summary>
    public ElevatedPnpUtilClient(
        PnpUtilService inner,
        string? helperPath = null,
        Func<string>? tempDirectoryProvider = null)
    {
        _inner = inner;
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "SystemToolkit.ElevatedHelper.exe");
        // 临时目录注入点（审查 L17：03 测试规范 §四——外部依赖必须注入，禁止服务内直接 Path.GetTempPath）
        _tempDirectoryProvider = tempDirectoryProvider ?? Path.GetTempPath;
    }

    /// <summary>枚举驱动包：只读查询无需提权，直接委托非提权内层客户端。</summary>
    public Task<IReadOnlyList<DriverPackage>> EnumDriversAsync(CancellationToken ct = default)
        => _inner.EnumDriversAsync(ct); // 只读查询无需提权

    /// <summary>批量导出：单次提权内逐包 /export-driver，写入按包独立暂存目录（见接口契约）。</summary>
    public Task<DriverRunResult> ExportManyAsync(
        IReadOnlyList<string> publishedNames, string destinationDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        if (publishedNames.Count == 0)
        {
            return Task.FromResult(new DriverRunResult(true, 0, "未指定任何驱动包，无操作"));
        }

        // 按包独立暂存（D-3 修复）：pnputil 导出目录名 = 原始 INF 名，同名原始 INF 的多版本包
        // 会写进同一目录相互覆盖（NVIDIA/AMD 多版本共存正是备份主场景）——暂存隔离后由
        // DriverBackupOrganizer 按暂存目录精确对位整理
        var segments = new List<IReadOnlyList<string>>();
        foreach (string name in publishedNames)
        {
            if (!PnpUtilTokenRules.IsValidInfFileName(name))
            {
                throw new ArgumentException($"非法的驱动包发布名：{name}", nameof(publishedNames));
            }

            string stageDir = DriverBackupOrganizer.StageDirFor(destinationDir, name);
            Directory.CreateDirectory(stageDir); // pnputil 要求目标目录已存在
            segments.Add(["/export-driver", name, stageDir]);
        }

        return RunElevatedAsync(segments, ct);
    }

    /// <summary>导出单个驱动包：单次提权经 Helper 执行 /export-driver。</summary>
    public Task<DriverRunResult> ExportAsync(string publishedName, string destinationDir, CancellationToken ct = default)
        => ExportManyAsync([publishedName], destinationDir, ct);

    /// <summary>删除单个驱动包：单次提权经 Helper 执行 /delete-driver，目标经 oem 白名单校验。</summary>
    public Task<DriverRunResult> DeleteAsync(string publishedName, bool force, CancellationToken ct = default)
    {
        if (!PnpUtilTokenRules.IsDeletablePublishedName(publishedName))
        {
            throw new ArgumentException($"删除目标必须是第三方 oem 包发布名：{publishedName}", nameof(publishedName));
        }

        List<string> cmd = ["/delete-driver", publishedName];
        if (force)
        {
            cmd.Add("/force");
        }

        return RunElevatedAsync([cmd], ct);
    }

    /// <summary>批量删除：单次 UAC 内逐包 /delete-driver。目标白名单同 <see cref="DeleteAsync"/>。</summary>
    public Task<DriverRunResult> DeleteManyAsync(IReadOnlyList<string> publishedNames, bool force, CancellationToken ct = default)
    {
        if (publishedNames.Count == 0)
        {
            return Task.FromResult(new DriverRunResult(true, 0, "未指定任何驱动包，无操作"));
        }

        var segments = new List<IReadOnlyList<string>>();
        foreach (string name in publishedNames)
        {
            if (!PnpUtilTokenRules.IsDeletablePublishedName(name))
            {
                throw new ArgumentException($"删除目标必须是第三方 oem 包发布名：{name}", nameof(publishedNames));
            }

            List<string> cmd = ["/delete-driver", name];
            if (force)
            {
                cmd.Add("/force");
            }

            segments.Add(cmd);
        }

        return RunElevatedAsync(segments, ct);
    }

    /// <summary>添加驱动到 Driver Store（可选安装）。双重判定见 <see cref="PnpUtilOutputAnalyzer.VerifyAddResult"/>。</summary>
    public async Task<DriverRunResult> AddDriverAsync(string infPath, bool install, CancellationToken ct = default)
    {
        if (!PnpUtilTokenRules.IsValidInfPathForAdd(infPath))
        {
            throw new ArgumentException($"INF 路径必须是已存在的绝对路径且以 .inf 结尾：{infPath}", nameof(infPath));
        }

        List<string> cmd = ["/add-driver", infPath];
        if (install)
        {
            cmd.Add("/install");
        }

        DriverRunResult result = await RunElevatedAsync([cmd], ct).ConfigureAwait(false);
        return PnpUtilOutputAnalyzer.VerifyAddResult(result);
    }

    /// <summary>批量添加：单次 UAC 内逐 INF /add-driver。批输出多段合并且无法按段拆计数，
    /// 成功判定基于逐段退出码；失败明细由调用方用 ExtractFailedSegments 提取。</summary>
    public Task<DriverRunResult> AddManyAsync(IReadOnlyList<string> infPaths, bool install, CancellationToken ct = default)
    {
        if (infPaths.Count == 0)
        {
            return Task.FromResult(new DriverRunResult(true, 0, "未指定任何 INF，无操作"));
        }

        var segments = new List<IReadOnlyList<string>>();
        foreach (string infPath in infPaths)
        {
            if (!PnpUtilTokenRules.IsValidInfPathForAdd(infPath))
            {
                throw new ArgumentException($"INF 路径必须是已存在的绝对路径且以 .inf 结尾：{infPath}", nameof(infPaths));
            }

            List<string> cmd = ["/add-driver", infPath];
            if (install)
            {
                cmd.Add("/install");
            }

            segments.Add(cmd);
        }

        return RunElevatedAsync(segments, ct);
    }

    /// <summary>方案甲：提权翻译设备类 GUID → 中文名。返回 "guid=中文名" 行表；UAC 拒绝返回 null（调用方回退其它来源）。</summary>
    public async Task<Dictionary<string, string>?> QueryClassNamesAsync(
        IReadOnlyCollection<string> classGuids, CancellationToken ct = default)
    {
        if (classGuids.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        string outFile = Path.Combine(_tempDirectoryProvider(), $"stk_cls_{Guid.NewGuid():N}.out");
        DriverRunResult result = await RunElevatedWithArgsAsync(
            [.. classGuids.Select(g => g.Trim())], outFile, "classnames", ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null; // UAC 拒绝 / 失败：调用方回退注册表/INF/GUID
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 结果文件 UTF-8 带 BOM，首行 GUID 可能带 \uFEFF 前缀，须剥离
        string[] lines = result.Output.TrimStart('\uFEFF').Split('\n');
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string guid = trimmed[..eq].Trim();
            string name = trimmed[(eq + 1)..].Trim();
            if (name.Length > 0)
            {
                map[guid] = name;
            }
        }

        return map;
    }

    /// <summary>启动提权 Helper 执行 pnputil 分段命令。</summary>
    private Task<DriverRunResult> RunElevatedAsync(List<IReadOnlyList<string>> segments, CancellationToken ct)
    {
        string outFile = Path.Combine(_tempDirectoryProvider(), $"stk_elev_{Guid.NewGuid():N}.out");
        return RunElevatedWithArgsAsync(BuildPnpUtilArguments(segments), outFile, null, ct);
    }

    /// <summary>通用提权通道：启动 Helper 传 commandWord + args，输出经 outFile 回传。UAC 拒绝 → 失败结果，不抛。</summary>
    private async Task<DriverRunResult> RunElevatedWithArgsAsync(
        IReadOnlyList<string> args, string outFile, string? commandWord, CancellationToken ct)
    {
        if (!File.Exists(_helperPath))
        {
            return new DriverRunResult(false, -1, $"提权辅助进程缺失：{_helperPath}");
        }

        var psi = new ProcessStartInfo(_helperPath)
        {
            UseShellExecute = true, // verb=runas 必须走 shell 启动通道
            Verb = "runas",
        };
        var allArgs = new List<string> { "--out", outFile };
        if (!string.IsNullOrWhiteSpace(commandWord))
        {
            allArgs.Add(commandWord!);
        }

        allArgs.AddRange(args);
        psi.Arguments = string.Join(' ', allArgs.Select(Quote));

        using Process proc = new() { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new DriverRunResult(false, 1223, "用户拒绝了 UAC 提权请求，操作已安全终止（无副作用）");
        }

        return await FinishHelperAsync(proc, outFile, ElevatedTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>收尾提权进程：等待退出 → 读取结果文件 → 取消/超时则尽力终止整棵进程树 → 两条路径都清理临时文件。
    /// 🔴 取消纪律（审查 2026-09-05 S2）：取消/超时必须终止 Helper——否则用户以为已中止，
    /// 提权删除/添加仍在后台继续执行（对齐 PnpUtilService/WingetService 的 Kill(entireProcessTree) 约定）。
    /// internal 供测试直测：被测逻辑不感知进程如何启动，测试用非提权哑进程覆盖两条路径（03 测试规范：禁止真实 UAC）。
    /// 【2026-09-06】实现抽出为跨域共用 <see cref="ElevatedProcessFinisher.FinishAsync"/>（网络提权通道同款），本包装保持签名兼容既有测试。</summary>
    internal static async Task<DriverRunResult> FinishHelperAsync(
        Process proc, string outFile, TimeSpan timeout, CancellationToken ct)
    {
        ElevatedRunResult result = await ElevatedProcessFinisher.FinishAsync(proc, outFile, timeout, ct).ConfigureAwait(false);
        return new DriverRunResult(result.Success, result.ExitCode, result.Output);
    }

    /// <summary>拼 pnputil 分段命令：命令字 "pnputil" + "--" 分隔各段。</summary>
    private static List<string> BuildPnpUtilArguments(List<IReadOnlyList<string>> segments)
    {
        var parts = new List<string> { "pnputil" };
        bool first = true;
        foreach (IReadOnlyList<string> segment in segments)
        {
            if (!first)
            {
                parts.Add("--");
            }

            first = false;
            parts.AddRange(segment);
        }

        return parts;
    }

    /// <summary>Windows argv 规则引号包裹（UseShellExecute=true 下 ArgumentList 失效，必须手工拼）：
    /// 含空格/引号/结尾反斜杠时整体加引号；内部引号前反斜杠倍增；结尾反斜杠在闭引号前倍增——
    /// 漏掉后者会让 <c>"C:\Dir Name\"</c> 的尾 \" 吞掉闭引号，与后续 token 粘连。</summary>
    internal static string Quote(string arg)
    {
        if (arg.Length == 0)
        {
            return "\"\"";
        }

        bool needsQuotes = arg.Contains(' ') || arg.Contains('"') || arg.EndsWith('\\');
        if (!needsQuotes)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // argv 规则：2n 个反斜杠 + 引号 = n 个反斜杠 + 界定符；2n+1 个 = n 个反斜杠 + 字面引号。
                // 引号前需「反斜杠倍增 + 1 个转义反斜杠」（漏掉转义层会被解析成闭引号——测试实证）
                _ = sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            _ = sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        _ = sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
