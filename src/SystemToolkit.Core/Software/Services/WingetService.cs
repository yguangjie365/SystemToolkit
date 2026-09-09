using System.Diagnostics;
using System.Text;
using SystemToolkit.Core.Software.Models;

namespace SystemToolkit.Core.Software.Services;

/// <summary>winget 命令行封装。实现 <see cref="IWingetClient"/>，消费方依赖接口以便测试替换。</summary>
public sealed partial class WingetService : IWingetClient
{
    /// <summary>写操作默认超时（安装/升级/卸载/更新源）。</summary>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// 读操作默认超时（list / upgrade / search / query）。源损坏或网络异常时 winget
    /// 可能长时间挂起；此前这些调用不传 timeout，UI 会无限等待且无法取消。
    /// </summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMinutes(3);

    /// <summary>版本探测超时（预检用，短）。</summary>
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly string _wingetPath;

    // 非 readonly：WingetService 交由 DI 容器托管为单例时，输出回调无法在构造时传入
    // （消费方 VM 尚未创建），需在构造后通过 OutputSink 挂接。
    private Action<string> _output;

    /// <summary>注入 winget 可执行路径与输出回调（测试可替换）；回调可经 OutputSink 后挂。</summary>
    public WingetService(string? wingetPath = null, Action<string>? output = null)
    {
        _wingetPath = wingetPath ?? "winget";
        _output = output ?? ((Action<string>)delegate
        {
        });
    }

    /// <summary>
    /// 输出回调。可在构造后替换：支持本服务由 DI 容器托管为单例，
    /// 消费方（如 EnvManagerViewModel）取得实例后再挂接自己的日志面板，
    /// 从而避免“回调必须在构造时传入”造成的循环依赖。
    /// 传 null 表示丢弃输出。
    /// </summary>
    public Action<string> OutputSink
    {
        get { return _output; }
        set { _output = value ?? ((Action<string>)delegate { }); }
    }

    /// <summary>查询包在本机的安装状态与版本（winget list --id），失败返回 Unknown 标记而非"未安装"。</summary>
    public async Task<WingetQueryResult> QueryAsync(string id, string? source = null, CancellationToken ct = default(CancellationToken))
    {
        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder(); // 审查 2026-09-04（P2）：stderr 不再丢弃，失败时附诊断
            int exitCode = await RunProcessAsync(_wingetPath, BuildIdArgs("list", id, source, silent: false, acceptPackageAgreements: false), delegate (string line)
            {
                stdout.AppendLine(line);
            }, delegate (string line)
            {
                stderr.AppendLine(line);
            }, ct, DefaultReadTimeout);

            // 非零退出码且无任何输出 = 查询本身失败（源损坏/网络/参数错误）。
            // 过去这种情况会被静默当成"未安装"，导致状态页满屏误判；
            // 非零但仍有输出时按内容正常解析（winget 对"包未找到"也可能返回非零）。
            if (exitCode != 0 && stdout.Length == 0)
            {
                _output($"winget 查询失败（退出码 {exitCode}）：{id}"
                    + (stderr.Length > 0 ? $"｜stderr: {stderr.ToString().Trim()}" : string.Empty));
                return new WingetQueryResult(Installed: false, null, null, Unknown: true);
            }

            (bool found, string? version, string? available) = FindPackageRow(stdout.ToString(), id);
            if (!found)
            {
                return new WingetQueryResult(Installed: false, null, null);
            }
            bool availableIsValid = !string.IsNullOrEmpty(available) && !available.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            return new WingetQueryResult(Installed: true, version, availableIsValid ? available : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 查询失败（winget 不可用/网络/源损坏）→ Unknown 标记，避免误判为"未安装"。
            // 取消必须穿透（审查 M6）：用户取消批量刷新时不得被逐个记为"状态未知"。
            _output($"（诊断）查询 {id} 状态失败：{ex.Message}");
            return new WingetQueryResult(Installed: false, null, null, Unknown: true);
        }
    }

    /// <summary>
    /// 探测 winget 是否可用（winget --version）。刷新安装状态前预检用。
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default(CancellationToken))
    {
        try
        {
            WingetRunResult result = await RunAsync(new[] { "--version" }, ct, VersionProbeTimeout);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 执行一次全量 <c>winget list</c>，返回原始输出。
    /// 调用方对输出逐行匹配各包 Id（FindPackageRow），从 N 个进程降为 1 个。
    /// 注意：全量 list 的 Available 列为空，且 msstore 包 Id 为 MSIX\ 长格式
    /// （与清单短 Id 不匹配），msstore 包需走 QueryAsync 补查。
    /// </summary>
    public async Task<string> ListInstalledAsync(CancellationToken ct = default(CancellationToken))
    {
        var stdout = new StringBuilder();
        int exitCode = await RunProcessAsync(_wingetPath, new[] { "list", "--accept-source-agreements", "--disable-interactivity" }, delegate (string line)
        {
            stdout.AppendLine(line);
        }, delegate
        {
        }, ct, DefaultReadTimeout);

        // 执行失败（非零码且无输出）必须让调用方感知：返回空串会让所有包被误判为"未安装"
        if (exitCode != 0 && stdout.Length == 0)
            throw new IOException($"winget list 执行失败（退出码 {exitCode}）");

        return stdout.ToString();
    }

    /// <summary>
    /// 执行一次 <c>winget upgrade</c>（无参），返回可升级包列表原始输出（含 Available 列）。
    /// 用于在全量刷新中恢复"可升级"判定。
    /// </summary>
    public async Task<string> ListUpgradesAsync(CancellationToken ct = default(CancellationToken))
    {
        var stdout = new StringBuilder();
        int exitCode = await RunProcessAsync(_wingetPath, new[] { "upgrade", "--accept-source-agreements", "--disable-interactivity" }, delegate (string line)
        {
            stdout.AppendLine(line);
        }, delegate
        {
        }, ct, DefaultReadTimeout);

        // 该结果只用于恢复"可升级"判定，失败时抛错由调用方降级处理（不影响已安装判定）
        if (exitCode != 0 && stdout.Length == 0)
            throw new IOException($"winget upgrade 查询失败（退出码 {exitCode}）");

        return stdout.ToString();
    }

    /// <summary>安装包（winget install --id，静默 + 自动接受协议）。</summary>
    public Task<WingetRunResult> InstallAsync(string id, string? source = null, CancellationToken ct = default(CancellationToken))
    {
        return RunAsync(BuildIdArgs("install", id, source, silent: true, acceptPackageAgreements: true), ct);
    }

    /// <summary>升级包（winget upgrade --id，静默 + 自动接受协议）。</summary>
    public Task<WingetRunResult> UpgradeAsync(string id, string? source = null, CancellationToken ct = default(CancellationToken))
    {
        return RunAsync(BuildIdArgs("upgrade", id, source, silent: true, acceptPackageAgreements: true), ct);
    }

    /// <summary>卸载包（winget uninstall --id，静默；不带 uninstall 不识别的协议选项）。</summary>
    public Task<WingetRunResult> UninstallAsync(string id, string? source = null, CancellationToken ct = default(CancellationToken))
    {
        // 注意：--accept-package-agreements 仅 install / upgrade 子命令支持，
        // uninstall 不识别该选项，带上会报“当前命令无法识别参数名称”并以非零码失败
        // （winget v1.29.290 已验证），卸载时只保留 --accept-source-agreements。
        return RunAsync(BuildIdArgs("uninstall", id, source, silent: true, acceptPackageAgreements: false), ct);
    }

    /// <summary>更新 winget 源索引（winget source update，源损坏/清单过期时修复）。</summary>
    public Task<WingetRunResult> UpdateSourceAsync(CancellationToken ct = default(CancellationToken))
    {
        // 注意：--accept-source-agreements 仅在 list / install / upgrade / uninstall / search
        // 等子命令上可用，`source update` 不识别该选项，带上会直接报
        // “当前命令无法识别参数名称” 并以非零码失败（winget v1.29.290 已验证）。
        // 源已添加后执行 update 无需重新同意源协议，禁用交互即可。
        return RunAsync(new[] { "source", "update", "--disable-interactivity" }, ct);
    }

    /// <summary>构造 <c>winget source add</c> 参数（抽出以便单元测试直接断言参数形态）。</summary>
    internal static List<string> BuildSourceAddArgs(string name, string url)
    {
        EnsureSafeWingetValue(name, "name", "非法的源名称");
        EnsureSafeWingetValue(url, "url", "非法的源地址");
        // 位置参数形态：source add <name> <arg>（name / arg 的值必需，但 --name / --arg
        // 标志本身可省略）。--trust-level trusted 等价于接受源协议，缺失时 winget
        // 会以非零码退出并提示「源需要信任」——无终端调用下表现为「点了没反应」。
        return new List<string>
        {
            "source", "add", name, url,
            "--trust-level", "trusted",
            "--disable-interactivity"
        };
    }

    /// <summary>构造 <c>winget source remove</c> 参数。</summary>
    internal static List<string> BuildSourceRemoveArgs(string name)
    {
        EnsureSafeWingetValue(name, "name", "非法的源名称");
        return new List<string> { "source", "remove", name, "--disable-interactivity" };
    }

    /// <summary>构造 <c>winget source reset</c> 参数。</summary>
    internal static List<string> BuildSourceResetArgs(string name)
    {
        EnsureSafeWingetValue(name, "name", "非法的源名称");
        // 【坑】必须带 --force：微软文档明确写着 reset 会移除源，必须用 --force 才会真正执行
        // （learn.microsoft.com/windows/package-manager/winget/source）。
        // 不带时 winget 只打印提示并以非零码退出，源不会发生任何变化——
        // 这正是「恢复官方源点了没反应」的典型成因。
        return new List<string> { "source", "reset", name, "--force", "--disable-interactivity" };
    }

    /// <summary>切换为自定义源（先移除同名旧源再添加；镜像添加失败时自动恢复原源）。</summary>
    public async Task<WingetRunResult> SetSourceAsync(string name, string url, CancellationToken ct = default(CancellationToken))
    {
        // 先移除同名源。源原本不存在时 winget 返回非零码，属预期，不能据此判定切换失败——
        // 否则首次换源（用户从未手动添加过该源）会被误报成「切换失败」。
        WingetRunResult remove = await RunAsync(BuildSourceRemoveArgs(name), ct, DefaultReadTimeout);
        if (!remove.Success)
        {
            _output($"（提示）源 {name} 未移除（退出码 {remove.ExitCode}），可能原本就不存在；继续添加镜像源…");
        }
        // 添加源要拉取并索引远端清单，耗时可能较长，走写操作超时（20 分钟）。
        WingetRunResult add = await RunAsync(BuildSourceAddArgs(name, url), ct);
        if (!add.Success && remove.Success)
        {
            // 审查 2026-09-04（P2）：镜像不可达时自动恢复被移除的原源，不让用户停留在"无源"状态
            _output($"（警告）添加源 {name} 失败（退出码 {add.ExitCode}），自动恢复原源…");
            WingetRunResult rollback = await RunAsync(BuildSourceResetArgs(name), ct, DefaultReadTimeout);
            _output(rollback.Success
                ? $"已自动恢复原源 {name}。"
                : $"自动恢复原源失败（退出码 {rollback.ExitCode}），请手动执行「恢复官方源」。");
        }
        return add;
    }

    /// <summary>恢复指定源为官方清单（winget source reset --force）。</summary>
    public Task<WingetRunResult> ResetSourceAsync(string name, CancellationToken ct = default(CancellationToken))
    {
        return RunAsync(BuildSourceResetArgs(name), ct);
    }

    /// <summary>
    /// 构造「按 Id 精确操作」的参数表（list / install / upgrade / uninstall 通用）。
    /// 用参数表而非拼接命令行字符串：id 与 source 可能来自导入的 JSON 或用户输入，
    /// 含引号或空格时字符串拼接会改变命令语义（ArgumentList 由框架负责转义）。
    /// </summary>
    /// <remarks>
    /// 参数注入防线（S6，REVIEW-2026-08-30；同日修订）：ArgumentList 只防 shell 元字符注入，
    /// 不防「值本身被 winget 当作选项」的参数注入。初版复用了导入清单的字符白名单
    /// （仅 ASCII 字母数字与 . _ -），但 <c>winget list</c> 返回的真实 Id 含中文、空格、
    /// 大括号（ARP/GUID 形态），全部被误拒——装机页「卸载/安装/升级」因此不生效（实测回归）。
    /// 修订为只拦截真实攻击面：空值、超长、以 <c>-</c> 开头（选项注入）、含引号或控制字符；
    /// 其余字符一律放行——ArgumentList 模式下这些值只是单个参数，无 shell 可注入。
    /// 目录导入的数据质量校验仍用 <see cref="EnvCatalogValidator.PackageIdPattern"/>（更严）。
    /// </remarks>
    internal static List<string> BuildIdArgs(string verb, string id, string? source, bool silent, bool acceptPackageAgreements)
    {
        EnsureSafeWingetValue(id, "id", "非法的包 Id");
        if (!string.IsNullOrWhiteSpace(source))
        {
            EnsureSafeWingetValue(source, "source", "非法的源名称");
        }
        var args = new List<string> { verb, "--id", id, "--exact" };
        if (silent)
        {
            args.Add("--silent");
        }
        if (acceptPackageAgreements)
        {
            // 仅 install / upgrade 识别该选项，uninstall 不识别（winget v1.29.290 已验证）
            args.Add("--accept-package-agreements");
        }
        args.Add("--accept-source-agreements");
        args.Add("--disable-interactivity");
        if (!string.IsNullOrWhiteSpace(source))
        {
            args.Add("--source");
            args.Add(source);
        }
        return args;
    }

    /// <summary>
    /// winget 参数值安全校验：只拦截会被解析器或进程启动利用的形态。
    /// 空白间隔、中文、大括号、反斜杠等真实 winget Id 中出现的字符全部放行。
    /// </summary>
    internal static void EnsureSafeWingetValue(string value, string paramName, string errorPrefix)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException($"{errorPrefix}（空值或超过 128 字符）", paramName);
        }
        if (value.StartsWith('-'))
        {
            // 以 - 开头的值会被 winget 解析器当作选项（如 --manifest/--file），而非待查 Id
            throw new ArgumentException($"{errorPrefix}（不能以 - 开头）：\"{value}\"", paramName);
        }
        foreach (char c in value)
        {
            // 引号在 ArgumentList 转义边界上最易出错，控制字符无合法语义，一并拒绝
            if (c == '"' || char.IsControl(c))
            {
                throw new ArgumentException($"{errorPrefix}（含引号或控制字符）：\"{value}\"", paramName);
            }
        }
    }

    private async Task<WingetRunResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            int num = await RunProcessAsync(_wingetPath, args, _output, _output, ct, timeout ?? DefaultOperationTimeout);
            return new WingetRunResult(num, num == 0);
        }
        catch (OperationCanceledException)
        {
            // 审查 O2（2026-09-10）：取消必须重新抛出——咽成退出码 -1 会让上层
            // "关窗即停"的 break 分支成死代码，剩余包仍逐项尝试（违背取消令牌红线）
            throw;
        }
        catch (TimeoutException ex)
        {
            _output(ex.Message);
            return new WingetRunResult(-1, Success: false);
        }
        catch (Exception ex2)
        {
            _output("winget 启动失败：" + ex2.Message);
            return new WingetRunResult(-1, Success: false);
        }
    }

    private static async Task<int> RunProcessAsync(string wingetPath, IReadOnlyList<string> args, Action<string> onStdout, Action<string> onStderr, CancellationToken ct, TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo(wingetPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 逐参数添加：框架负责转义，杜绝 id/查询词中的引号或空格改变命令语义
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var proc = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        proc.OutputDataReceived += delegate (object _, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                onStdout(e.Data);
            }
        };
        proc.ErrorDataReceived += delegate (object _, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                onStderr(e.Data);
            }
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using CancellationTokenSource? timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts is not null)
            timeoutCts.CancelAfter(timeout!.Value);
        CancellationToken effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            await proc.WaitForExitAsync(effectiveCt);
            // 审查 2026-09-04（P1-5）：异步重定向的尾部输出可能在进程退出后仍在 flush，
            // WaitForExitAsync 不等它——补一次同步等待排空缓冲，防止大输出的末尾行丢失
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // 取消必须终止进程树，无论成因：原实现用 `when (timeout.HasValue && !ct.IsCancellationRequested)`
            // 把「用户取消」排除在外，导致用户点取消后界面回到空闲态，
            // 而 winget 进程仍在后台把安装/卸载跑完（winget 有进程级互斥锁，
            // 残留进程还会阻塞下一次操作）。
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            if (timeout.HasValue && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"winget 操作超时（{timeout!.Value.TotalMinutes:0} 分钟），已终止进程");
            }
            throw; // 用户取消：原样传播
        }
    }

    /// <summary>
    /// 在 <c>winget list</c> 输出文本中按 Id 定位包行并解析版本/可用版本。
    /// </summary>
    /// <param name="text">winget list 全量输出或单包输出。</param>
    /// <param name="id">目标包 Id。</param>
    /// <returns>Found=是否找到该包行；Version=当前版本；Available=可用更新版本（可能为 null/空）。</returns>
    public static (bool Found, string? Version, string? Available) FindPackageRow(string text, string id)
        => FindPackageRow(text.Split('\n'), id);

    /// <summary>
    /// 数组重载：调用方对同一份输出匹配多个 Id 时先 Split 一次复用——
    /// 旧形态每包各重切全量文本（50 包 × 2 列表 ≈ 100 次全量分配与扫描，性能审查 P0-4）。
    /// </summary>
    public static (bool Found, string? Version, string? Available) FindPackageRow(string[] lines, string id)
    {
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            int idStart = line.IndexOf(id, StringComparison.OrdinalIgnoreCase);
            // ID 前后必须为空白（整体匹配），避免 "Git" 命中 "GitExtensions" 这类前缀误匹配
            if (idStart < 0 || (idStart > 0 && !char.IsWhiteSpace(line[idStart - 1])))
            {
                continue;
            }
            int idEnd = idStart + id.Length;
            if (idEnd < line.Length && !char.IsWhiteSpace(line[idEnd]))
            {
                continue;
            }
            string[] tokens = line.Substring(idEnd).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return (Found: true, Version: null, Available: null);
            }
            string version = tokens[0];
            if (version.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                version = "";
            }
            string available = "";
            foreach (string token in tokens.Skip(1))
            {
                // 跳过 winget/msstore 等源名列，首次遇到非源名 Token 即视为可用更新版本
                if (!IsKnownSource(token))
                {
                    available = token;
                    break;
                }
            }
            if (available.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                available = "";
            }
            return (Found: true,
                Version: string.IsNullOrEmpty(version) ? null : version,
                Available: string.IsNullOrEmpty(available) ? null : available);
        }
        return (Found: false, Version: null, Available: null);
    }

    private static bool IsKnownSource(string token)
    {
        if (!token.Equals("winget", StringComparison.OrdinalIgnoreCase) && !token.Equals("msstore", StringComparison.OrdinalIgnoreCase) && !token.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && !token.Equals("Store", StringComparison.OrdinalIgnoreCase))
        {
            return token.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }
}
