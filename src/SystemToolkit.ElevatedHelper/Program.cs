using System.Diagnostics;
using System.Text;
using SystemToolkit.Core.Drivers;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.ElevatedHelper;

/// <summary>
/// 特权操作执行进程（UAC 提权入口）。
/// 协议（02 分册 §四：可序列化，禁止委托/接口实例）：
///   SystemToolkit.ElevatedHelper.exe --out &lt;结果文件&gt; pnputil &lt;args…&gt; [-- pnputil &lt;args…&gt; …]
/// - 以 "--" 分段，每段一条 pnputil 子命令；单次 UAC 覆盖整批操作（避免逐包弹 UAC）。
/// - 全部输出（stdout+stderr）写入 --out 指定文件（UTF-8），进程退出码即整批结果：
///   0 = 全部成功；非 0 = 最后一个失败子命令的退出码。
/// 主进程经 verb=runas 启动本进程（UAC 弹窗），用户拒绝即安全终止、无副作用（03 设计 §6）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // --dry-run：预览类操作，不产生副作用（02 分册 §四，保留旧约定）
        if (args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (args.Length < 2 || !args[0].Equals("--out", StringComparison.OrdinalIgnoreCase))
        {
            return WriteError("缺少 --out <结果文件> 参数");
        }

        string outFile = args[1];

        // REVIEW-3 S-1：提权进程不得向任意路径写文件——out 文件强制限于当前用户 %TEMP%
        // （调用方 ElevatedPnpUtilClient/ElevatedVssClient 本就以 Path.GetTempPath() 构造 out 路径，
        // 功能兼容）。未约束时，任意低权限进程可借本进程的 UAC 确认向系统目录写半可控内容。
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string outFull = Path.GetFullPath(outFile);
        if (!outFull.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetDirectoryName(outFull), tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            return WriteError("拒绝执行：--out 结果文件必须位于当前用户临时目录");
        }

        // 安全硬化：out 结果文件所在目录不得为 reparse point（symlink/junction）——
        // 攻击者可在 %TEMP% 预建 junction 指向系统目录，借提权进程向系统路径写文件（S-1 残余防御）。
        string? outDir = Path.GetDirectoryName(outFull);
        if (outDir is not null && Directory.Exists(outDir)
            && (File.GetAttributes(outDir) & FileAttributes.ReparsePoint) != 0)
        {
            return WriteError("拒绝执行：--out 结果文件目录不能是符号链接/联接点");
        }

        // 子命令分派：classnames = 翻译设备类 GUID（方案甲，读中文类名需提权）
        if (args.Length >= 3 && args[2].Equals("classnames", StringComparison.OrdinalIgnoreCase))
        {
            return RunClassNames(args[3..], outFile);
        }

        // 子命令分派：net = 网络写命令段（netsh/ipconfig/arp，白名单见 NetshTokenRules）
        if (args.Length >= 3 && args[2].Equals("net", StringComparison.OrdinalIgnoreCase))
        {
            return await RunNetAsync(args[3..], outFile).ConfigureAwait(false);
        }

        // 子命令分派：throttling = HKLM NetworkThrottlingIndex 单值写（窄白名单：仅此键此值）
        if (args.Length >= 3 && args[2].Equals("throttling", StringComparison.OrdinalIgnoreCase))
        {
            return RunThrottling(args[3..], outFile);
        }

        // 子命令分派：osver = 批量远程 WMI 读 OS 名+版本号（NET-6 精确识别；只读、双端同源 IPv4 白名单）
        if (args.Length >= 3 && args[2].Equals("osver", StringComparison.OrdinalIgnoreCase))
        {
            return await OsVerRunner.RunAsync(args[3..], outFile).ConfigureAwait(false);
        }

        // 子命令分派：vss = 卷影快照创建/删除（AlphaVSS，备份用途；删除幂等，快照生命周期归调用方）
        if (args.Length >= 3 && args[2].Equals("vss", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 4)
            {
                return WriteError("vss 子命令需要动作参数：create <卷根路径> 或 delete <ShadowId>");
            }

            string[] vssArgs = args[4..];
            if (args[3].Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                return await VssRunner.CreateAsync(vssArgs, outFile).ConfigureAwait(false);
            }

            if (args[3].Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                return await VssRunner.DeleteAsync(vssArgs, outFile).ConfigureAwait(false);
            }

            // REVIEW-3 S-1：不回显调用方原始参数
            return WriteError("未知的 vss 动作（应为 create/delete）");
        }

        List<List<string>> segments = SplitSegments(args, startIndex: 2);
        if (segments.Count == 0)
        {
            return WriteError("没有可执行的命令段（应为 pnputil …）");
        }

        // 🔴 段内容白名单（纵深防御，规则与 Core 端 PnpUtilTokenRules 同源）：
        // 本进程以管理员身份运行，不信任任何调用方拼好的参数——任何一段不合法即整批拒绝（零执行）。
        foreach (List<string> segment in segments)
        {
            if (!ValidateSegment(segment))
            {
                // REVIEW-3 S-1：不回显调用方原始参数
                return WriteError("拒绝执行非法命令段（pnputil 白名单外）");
            }
        }

        string pnputilPath = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        var output = new StringBuilder();
        int lastBadExitCode = 0;

        foreach (List<string> segment in segments)
        {
            string description = string.Join(' ', segment);
            int exitCode = await RunOneAsync(pnputilPath, segment, output).ConfigureAwait(false);
            output.AppendLine($"[exit {exitCode}] pnputil {description}");
            if (exitCode != 0)
            {
                lastBadExitCode = exitCode;
            }
        }

        try
        {
            await File.WriteAllTextAsync(outFile, output.ToString(), Encoding.UTF8).ConfigureAwait(false);
        }
        catch
        {
            // 输出文件写不进去时结果只能走退出码，无法补救
        }

        return lastBadExitCode;
    }

    /// <summary>子命令 classnames：逐 GUID 翻译成中文类名，写 "guid=中文名" 到结果文件。
    /// 失败 GUID 记空（主进程回退其它来源）。退出码 0。</summary>
    private static int RunClassNames(string[] guids, string outFile)
    {
        var output = new StringBuilder();
        foreach (string g in guids)
        {
            string? name = DeviceClassNameReader.GetClassName(g);
            output.AppendLine($"{g.Trim()}={name ?? ""}");
        }

        try
        {
            File.WriteAllText(outFile, output.ToString(), Encoding.UTF8);
            return 0;
        }
        catch
        {
            return WriteError("classnames 结果写入失败");
        }
    }

    private static async Task<int> RunOneAsync(string exe, List<string> args, StringBuilder output)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Console.OutputEncoding,
            StandardErrorEncoding = Console.OutputEncoding,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = new() { StartInfo = psi };
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

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync().ConfigureAwait(false);
        // 排空异步重定向尾部缓冲（教训见 WingetService/PnpUtilService 同款修复）
        proc.WaitForExit();
        return proc.ExitCode;
    }

    /// <summary>段白名单：首 token 限 /delete-driver|/export-driver|/add-driver；
    /// delete 目标限 oem 包发布名、export 目标限 INF 文件名、add 目标限已存在的 .inf 绝对路径。
    /// 规则与 Core 端 PnpUtilTokenRules 同源（本进程直引 Core，避免两份规则漂移）。</summary>
    private static bool ValidateSegment(List<string> segment)
    {
        if (segment.Count < 2)
        {
            return false;
        }

        return segment[0] switch
        {
            "/delete-driver" => PnpUtilTokenRules.IsDeletablePublishedName(segment[1]),
            "/export-driver" => PnpUtilTokenRules.IsValidInfFileName(segment[1]),
            "/add-driver" => PnpUtilTokenRules.IsValidInfPathForAdd(segment[1]),
            _ => false,
        };
    }

    /// <summary>按字面量 "--" 分段。</summary>
    private static List<List<string>> SplitSegments(string[] args, int startIndex)
    {
        var segments = new List<List<string>>();
        var current = new List<string>();
        for (int i = startIndex; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                if (current.Count > 0)
                {
                    segments.Add(current);
                    current = new List<string>();
                }

                continue;
            }

            current.Add(args[i]);
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        // 🔴 容错：段首残留 "pnputil" 命令字则剥离。Core 端历史上只在整串开头发一次
        // "pnputil"，协议要求每段带前缀——实测（2026-09-05）首段被执行成
        // "pnputil.exe pnputil /export-driver …" → 打印用法 exit 1，整批判失败（首个勾选的包必挂）。
        foreach (List<string> segment in segments)
        {
            if (segment.Count > 1 && segment[0].Equals("pnputil", StringComparison.OrdinalIgnoreCase))
            {
                segment.RemoveAt(0);
            }
        }

        return segments;
    }

    /// <summary>
    /// 子命令 net：网络写命令段执行。每段 = <c>&lt;exe&gt; &lt;参数串&gt;</c>（参数串为单个 argv token），
    /// exe 限 netsh / ipconfig / arp。🔴 双端同源白名单复核（纵深防御，与 pnputil 段白名单同原则）：
    /// Core 端 <c>ElevatingCommandRunner</c> 已判一次，本进程以管理员运行不信任调用方——
    /// <see cref="NetshTokenRules.IsElevatedWrite"/> 再判一次，任何一段不合法即整批拒绝（零执行）。
    /// 输出经 NetRunner 原始字节择优解码后写结果文件；退出码 = 最后一个失败段的退出码。
    /// </summary>
    private static async Task<int> RunNetAsync(string[] args, string outFile)
    {
        List<List<string>> segments = SplitSegments(args, 0);
        if (segments.Count == 0)
        {
            return WriteError("net 通道没有命令段");
        }

        foreach (List<string> segment in segments)
        {
            if (segment.Count != 2)
            {
                // REVIEW-3 S-1：不回显调用方原始参数
                return WriteError("net 命令段格式非法（应为 <exe> <参数串>）");
            }

            string exe = NormalizeExe(segment[0]);
            if (exe is not ("netsh" or "ipconfig" or "arp"))
            {
                // REVIEW-3 S-1：不回显调用方原始参数
                return WriteError("拒绝白名单外的可执行名");
            }

            if (!NetshTokenRules.IsElevatedWrite(exe, segment[1]))
            {
                // REVIEW-3 S-1：不回显调用方原始参数
                return WriteError("拒绝白名单外的写命令");
            }
        }

        var output = new StringBuilder();
        int lastBadExitCode = 0;
        foreach (List<string> segment in segments)
        {
            string exePath = Path.Combine(Environment.SystemDirectory, NormalizeExe(segment[0]) + ".exe");
            string description = $"{segment[0]} {segment[1]}";
            (int exitCode, string text) = await NetRunner.RunAsync(exePath, segment[1]).ConfigureAwait(false);
            if (text.Length > 0)
            {
                output.AppendLine(text);
            }

            output.AppendLine($"[exit {exitCode}] {description}");
            if (exitCode != 0)
            {
                lastBadExitCode = exitCode;
            }
        }

        try
        {
            await File.WriteAllTextAsync(outFile, output.ToString(), Encoding.UTF8).ConfigureAwait(false);
        }
        catch
        {
            // 输出文件写不进去时结果只能走退出码，无法补救
        }

        return lastBadExitCode;
    }

    /// <summary>
    /// 子命令 throttling：写 HKLM <c>SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\NetworkThrottlingIndex</c>。
    /// 🔴 窄白名单：Helper 提供的注册表写能力仅此一键一值（值须可解析为 uint，兼容 0x 前缀十六进制），
    /// 其余任何注册表路径一律拒绝——提权面越小越好。
    /// </summary>
    private static int RunThrottling(string[] args, string outFile)
    {
        if (args.Length != 1)
        {
            return WriteError("throttling 需要 <值> 参数");
        }

        string raw = args[0].Trim();
        bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? raw[2..] : raw, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out uint value))
        {
            // REVIEW-3 S-1：不回显调用方原始参数
            return WriteError("非法的 NetworkThrottlingIndex 值");
        }

        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", writable: true);
            key.SetValue("NetworkThrottlingIndex", unchecked((int)value), Microsoft.Win32.RegistryValueKind.DWord);
            File.WriteAllText(outFile, $"[注册表] NetworkThrottlingIndex = 0x{value:X}", Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(outFile, $"[注册表] NetworkThrottlingIndex 写入失败：{ex.Message}", Encoding.UTF8);
            }
            catch
            {
                // 结果文件写不进去只能走退出码
            }

            return 1;
        }
    }

    /// <summary>可执行名归一化：去路径与扩展名、转小写。</summary>
    private static string NormalizeExe(string raw)
    {
        string name = raw;
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        return name.ToLowerInvariant();
    }

    private static int WriteError(string message)
    {
        Console.Error.WriteLine(message);
        return -2;
    }
}
