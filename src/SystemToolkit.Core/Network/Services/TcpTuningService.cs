using System.Runtime.Versioning;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="ITcpTuningService"/> 实现。
/// <para>
/// 【写路径】netsh 三项（autotuninglevel / rss / ecncapability）经 <see cref="ICommandRunner"/>，
/// NetworkThrottlingIndex 走 HKLM 注册表（<c>TcpRegistry</c>，系统边界不做单测）。
/// 【快照】每次应用前把<b>当前状态</b>落 JSON（仅保留最近一份）；解析失败的项不进快照，
/// 还原时跳过——绝不拿「未知」去写系统。【降级】netsh 输出解析失败 = 该项显示「未知」且应用时跳过。
/// </para>
/// </summary>
public sealed class TcpTuningService : ITcpTuningService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>autotuninglevel 合法取值集（与 NetshTokenRules.TcpSetGlobal 白名单一致，取真机 netsh set global 帮助输出）。</summary>
    private static readonly System.Collections.Generic.HashSet<string> ValidAutoTuningLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "disabled", "highlyrestricted", "restricted", "normal", "experimental"
    };

    /// <summary>判定 autotuninglevel 是否为 netsh 白名单内的合法值；null/未知一律 false（绝不把任意串发往 netsh）。</summary>
    private static bool IsValidAutoTuningLevel(string? level)
        => level is not null && ValidAutoTuningLevels.Contains(level);

    private readonly ICommandRunner _runner;
    private readonly string _snapshotPath;
    private readonly ILogger _logger;

    /// <summary>
    /// 默认快照路径（02 §六统一配置根）。历史版本写的是 <c>%APPDATA%\FileBackupTool\net\</c>
    /// ——旧产品名 + Roaming，属 M9 整改的漏网项（🟡 审查 2026-09-10）。
    /// </summary>
    private static readonly string DefaultSnapshotPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "net", "tuning_snapshot.json");

    /// <summary>旧版快照位置——只作一次性迁移来源，不再写入。</summary>
    private static readonly string LegacySnapshotPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "FileBackupTool", "net", "tuning_snapshot.json");

    /// <summary>构造；快照路径缺省为 <c>%LOCALAPPDATA%\SystemToolkit\net\tuning_snapshot.json</c>（测试注入临时目录）。
    /// 【非提权宿主适配 2026-09-06】<paramref name="throttlingWriter"/>：HKLM 直写在新宿主（按需 UAC 架构）必失败，
    /// 注入委托时 NetworkThrottlingIndex 改走提权 Helper 通道；未注入（旧测试/旧宿主）保持直写 + 权限失败降级。</summary>
    public TcpTuningService(ICommandRunner runner, string? snapshotPath = null, Func<uint, Action<string>, Task<int>>? throttlingWriter = null, ILogger? logger = null)
    {
        _runner = runner;
        _logger = logger ?? NullLogger.Instance;
        if (snapshotPath is null)
        {
            _snapshotPath = DefaultSnapshotPath;
            MigrateLegacySnapshot(); // 仅缺省路径迁移；显式注入（测试）不触碰真实目录
        }
        else
        {
            _snapshotPath = snapshotPath;
        }

        _throttlingWriter = throttlingWriter;
    }

    /// <summary>
    /// 一次性迁移（🟡-6，范式对齐 <c>RuleManager.MigrateLegacyRulesFile</c>）：
    /// 新位置缺失且旧位置存在时原样复制，保留老用户「升级前调优状态」的还原能力；
    /// 新位置已有数据则不动，旧文件保留不删。
    /// </summary>
    /// <remarks>
    /// 刻意不抛：本类构造期没有日志出口（<c>onLine</c> 是方法参数），而迁移失败只意味着
    /// 「本次会话看不到历史快照」——<see cref="HasSnapshot"/> 会如实返回 false，UI 显示
    /// 「无可还原快照」，不构成假成功；调优功能本身完全不受影响。
    /// </remarks>
    private void MigrateLegacySnapshot()
    {
        try
        {
            if (File.Exists(_snapshotPath) || !File.Exists(LegacySnapshotPath))
            {
                return;
            }

            string? dir = System.IO.Path.GetDirectoryName(_snapshotPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(LegacySnapshotPath, _snapshotPath);
        }
        catch (Exception)
        {
            // 见 remarks：失败退化为"无历史快照"，不阻断构造
        }
    }

    private readonly Func<uint, Action<string>, Task<int>>? _throttlingWriter;

    /// <inheritdoc cref="ITcpTuningService.HasSnapshot"/>
    public bool HasSnapshot => File.Exists(_snapshotPath);

    /// <inheritdoc cref="ITcpTuningService.ReadAsync"/>
    public async Task<TcpGlobalSettings> ReadAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        // show global 只读不写，无需管理员；输出逐行收集后交给解析器（缺行/乱码降级 Unknown）
        await _runner.RunAsync("netsh", "interface tcp show global", lines.Add, ct, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        TcpGlobalSettings parsed = TcpSettingsParser.Parse(lines);
        // 运行时守卫（01 分册：Core 内 Windows API 用 IsWindows 守卫，不用 [SupportedOSPlatform]）
        uint? throttling = OperatingSystem.IsWindows() ? TcpRegistry.ReadNetworkThrottlingIndex() : null;
        return parsed with { NetworkThrottlingIndex = throttling };
    }

    /// <inheritdoc cref="ITcpTuningService.ApplyAsync"/>
    public async Task<TcpApplyResult> ApplyAsync(TcpGlobalSettings target, Action<string> onLine, CancellationToken ct = default)
    {
        LogTiming timing = _logger.Time("TcpTuningApply");
        // 改前快照先行：哪怕后面某一步失败，用户手里也有一份可还原的原始状态
        //（快照同时记录 TCP 参数与全部接口 metric——「一份改前快照」完整语义）
        TcpGlobalSettings current = await ReadAsync(ct).ConfigureAwait(false);
        IReadOnlyList<InterfaceMetricInfo> currentMetrics = await ListInterfaceMetricsAsync(ct).ConfigureAwait(false);
        SaveSnapshot(current, currentMetrics, onLine);

        // 【审查修复】与快照侧同一原则：当前值未知（解析失败）→ 跳过该项，绝不把
        // 「没读到」当成可比较的值去写。否则新版 Windows 的 show global 措辞一旦
        // 变化导致解析全 null，用户应用任何一项都会把全部非 null 目标重写一遍，
        // 且快照里也没有这些项 → 无法还原。
        // 【核实报告 N12】写入/跳过项进 Applied/Skipped 摘要，随返回值交 VM——
        // 「确认 4 项、实际写 2 项」的不一致在 UI 可见。
        var applied = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>(); // 审查 O4/O10：写入失败项单独聚合，不再假计入 applied

        if (target.AutoTuningLevel is not null)
        {
            if (!IsValidAutoTuningLevel(target.AutoTuningLevel))
            {
                onLine("[调优] ⚠️ 自动调谐级别不是白名单值，已跳过——绝不盲写");
                skipped.Add("自动调谐级别（非法值）");
            }
            else if (current.AutoTuningLevel is null)
            {
                onLine("[调优] ⚠️ 自动调谐级别当前值未知（show global 解析失败），已跳过——绝不盲写");
                skipped.Add("自动调谐级别（当前值未知）");
            }
            else if (!string.Equals(target.AutoTuningLevel, current.AutoTuningLevel, StringComparison.OrdinalIgnoreCase))
            {
                if (await SetGlobalAsync($"autotuninglevel={target.AutoTuningLevel}", onLine, ct).ConfigureAwait(false))
                {
                    applied.Add($"自动调谐级别 → {target.AutoTuningLevel}");
                }
                else
                {
                    failed.Add("自动调谐级别");
                }
            }
        }

        if (target.RssEnabled is not null)
        {
            if (current.RssEnabled is null)
            {
                onLine("[调优] ⚠️ RSS 当前值未知（show global 解析失败），已跳过——绝不盲写");
                skipped.Add("RSS（当前值未知）");
            }
            else if (current.RssEnabled != target.RssEnabled)
            {
                if (await SetGlobalAsync($"rss={(target.RssEnabled == true ? "enabled" : "disabled")}", onLine, ct).ConfigureAwait(false))
                {
                    applied.Add($"RSS → {(target.RssEnabled == true ? "启用" : "禁用")}");
                }
                else
                {
                    failed.Add("RSS");
                }
            }
        }

        if (target.EcnEnabled is not null)
        {
            if (current.EcnEnabled is null)
            {
                onLine("[调优] ⚠️ ECN 当前值未知（show global 解析失败），已跳过——绝不盲写");
                skipped.Add("ECN（当前值未知）");
            }
            else if (current.EcnEnabled != target.EcnEnabled)
            {
                if (await SetGlobalAsync($"ecncapability={(target.EcnEnabled == true ? "enabled" : "disabled")}", onLine, ct).ConfigureAwait(false))
                {
                    applied.Add($"ECN → {(target.EcnEnabled == true ? "启用" : "禁用")}");
                }
                else
                {
                    failed.Add("ECN");
                }
            }
        }

        if (target.NetworkThrottlingIndex is not null)
        {
            if (current.NetworkThrottlingIndex is null)
            {
                onLine("[调优] ⚠️ NetworkThrottlingIndex 当前值未知（注册表读取失败），已跳过——绝不盲写");
                skipped.Add("网络限流（当前值未知）");
            }
            else if (current.NetworkThrottlingIndex != target.NetworkThrottlingIndex)
            {
                // 审查 O3（2026-09-10）：只有写入成功才计入 applied
                if (await WriteThrottlingIndexAsync(target.NetworkThrottlingIndex.Value, onLine).ConfigureAwait(false))
                {
                    applied.Add($"网络限流 → 0x{target.NetworkThrottlingIndex.Value:X}");
                }
                else
                {
                    skipped.Add("网络限流（提权被拒绝或写入失败）");
                }
            }
        }

        timing.Complete(failed.Count == 0 ? LogResult.Success : LogResult.Failed,
            failed.Count == 0 ? LogLevel.Info : LogLevel.Warn,
            $"TCP 调优应用：写入 {applied.Count}、跳过 {skipped.Count}、失败 {failed.Count}");
        return new TcpApplyResult(applied, skipped, failed);
    }

    /// <inheritdoc cref="ITcpTuningService.RestoreAsync"/>
    public async Task RestoreAsync(Action<string> onLine, CancellationToken ct = default)
    {
        LogTiming timing = _logger.Time("TcpTuningRestore");
        if (!HasSnapshot)
        {
            timing.Complete(LogResult.Rejected, LogLevel.Warn, "无优化快照可还原");
            throw new InvalidOperationException("没有可还原的优化快照——从未应用过更改，或快照文件已被清理");
        }

        TuningSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<TuningSnapshot>(File.ReadAllText(_snapshotPath), JsonOptions);
        }
        catch (JsonException ex)
        {
            timing.Complete(LogResult.Rejected, LogLevel.Warn, $"优化快照文件损坏，拒绝还原：{ex.Message}");
            throw new InvalidOperationException($"优化快照文件损坏（{ex.Message}），为安全起见拒绝还原", ex);
        }

        if (snapshot is null)
        {
            timing.Complete(LogResult.Rejected, LogLevel.Warn, "优化快照内容为空，拒绝还原");
            throw new InvalidOperationException("优化快照为空，拒绝还原");
        }

        onLine($"[调优] 还原 {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss} 的改前快照");

        // 🟠 审查 2026-09-10（🟠-3）：逐项聚合失败——此前本方法把 SetGlobalAsync 的 bool
        // 返回值丢弃、结尾无条件打「✅ 已还原」，用户拒 UAC（netsh 每项弹一次）时实际
        // 未还原却报成功（O4 同款假成功；NetworkSnapshotService 已修，此处漏掉）。
        var failed = new List<string>();

        // 依快照逐项还原：快照里没有的项（当时解析失败）跳过，绝不拿「未知」去写系统
        if (snapshot.NetshValues.TryGetValue("autotuninglevel", out string? level))
        {
            if (!IsValidAutoTuningLevel(level))
            {
                onLine("[调优] ⚠️ 快照中的自动调谐级别非法，已跳过还原（绝不盲写）");
            }
            else
            {
                if (!await SetGlobalAsync($"autotuninglevel={level}", onLine, ct).ConfigureAwait(false))
                {
                    failed.Add("自动调谐级别");
                }
            }
        }

        if (TryGetSwitch(snapshot.NetshValues, "rss", out bool rss))
        {
            if (!await SetGlobalAsync($"rss={(rss ? "enabled" : "disabled")}", onLine, ct).ConfigureAwait(false))
            {
                failed.Add("RSS");
            }
        }

        if (TryGetSwitch(snapshot.NetshValues, "ecncapability", out bool ecn))
        {
            if (!await SetGlobalAsync($"ecncapability={(ecn ? "enabled" : "disabled")}", onLine, ct).ConfigureAwait(false))
            {
                failed.Add("ECN");
            }
        }

        if (snapshot.NetworkThrottlingIndex is uint throttling)
        {
            uint? currentThrottling = OperatingSystem.IsWindows() ? TcpRegistry.ReadNetworkThrottlingIndex() : null;
            if (currentThrottling == throttling)
            {
                onLine($"[调优] NetworkThrottlingIndex 已是快照值（0x{throttling:X}），无需写入");
            }
            else if (!await WriteThrottlingIndexAsync(throttling, onLine, restore: true).ConfigureAwait(false))
            {
                failed.Add("NetworkThrottlingIndex");
            }
        }

        if (snapshot.InterfaceMetrics is { Count: > 0 } metrics)
        {
            // 【M6c P1-6】接口跃点数还原：只写与当前不同的项；权限失败降级不穿透
            IReadOnlyList<InterfaceMetricInfo> currentMetrics = await ListInterfaceMetricsAsync(ct).ConfigureAwait(false);
            foreach (KeyValuePair<string, int> target in metrics)
            {
                InterfaceMetricInfo? cur = currentMetrics.FirstOrDefault(x => x.Name == target.Key);
                if (cur is null)
                {
                    onLine($"[调优] 接口「{target.Key}」已不存在，跳过其跃点数还原");
                    continue;
                }

                if (cur.Metric == target.Value)
                {
                    continue;
                }

                if (!await WriteInterfaceMetric(target.Key, target.Value, onLine, ct).ConfigureAwait(false))
                {
                    failed.Add($"接口「{target.Key}」跃点数");
                }

                onLine("[调优] ⚠️ 若该接口原本为「自动跃点」，如需恢复请在系统设置中改回自动");
            }
        }

        // 🟠-3：按实际写入结果汇报，不再无条件打 ✅（假成功是本次审查点名的反模式族）
        onLine(failed.Count == 0
            ? "[调优] ✅ 已还原改前快照（此项操作不覆盖快照，可重复执行）"
            : $"[调优] ⚠️ 还原未完全成功：{failed.Count} 项未写入（{string.Join("、", failed)}）——"
              + "常见原因是 UAC 提权被拒，其余项不受影响；本操作不覆盖快照，可重复执行");
        timing.Complete(failed.Count == 0 ? LogResult.Success : LogResult.Failed,
            failed.Count == 0 ? LogLevel.Info : LogLevel.Warn,
            $"TCP 调优还原：{snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss} 快照，未写入 {failed.Count} 项");
    }

    /// <inheritdoc cref="ITcpTuningService.ListInterfaceMetricsAsync"/>
    public async Task<IReadOnlyList<InterfaceMetricInfo>> ListInterfaceMetricsAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        await _runner.RunAsync("netsh", "interface ipv4 show interfaces", lines.Add, ct, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return InterfaceTableParser.Parse(lines);
    }

    /// <inheritdoc cref="ITcpTuningService.ApplyInterfaceMetricAsync"/>
    public async Task ApplyInterfaceMetricAsync(string adapter, int metric, Action<string> onLine, CancellationToken ct = default)
    {
        LogTiming timing = _logger.Time("ApplyInterfaceMetric");
        IReadOnlyList<InterfaceMetricInfo> currentMetrics = await ListInterfaceMetricsAsync(ct).ConfigureAwait(false);
        TcpGlobalSettings currentTcp = await ReadAsync(ct).ConfigureAwait(false);
        SaveSnapshot(currentTcp, currentMetrics, onLine);

        InterfaceMetricInfo? cur = currentMetrics.FirstOrDefault(m => m.Name == adapter);
        if (cur is null)
        {
            onLine($"[调优] ❌ 接口「{adapter}」不存在（可能已断开/被移除），未执行");
            timing.Complete(LogResult.Rejected, LogLevel.Warn, $"跃点数设置被拒：接口「{adapter}」不存在");
            return;
        }

        if (cur.Metric == metric)
        {
            onLine($"[调优] 「{adapter}」跃点数已是 {metric}，无需修改");
            timing.Complete(LogResult.Success, LogLevel.Info, $"跃点数无需修改：「{adapter}」已是 {metric}");
            return;
        }

        bool written = await WriteInterfaceMetric(adapter, metric, onLine, ct).ConfigureAwait(false);
        timing.Complete(written ? LogResult.Success : LogResult.Failed,
            written ? LogLevel.Info : LogLevel.Warn,
            $"跃点数设置「{adapter}」→ {metric}：{(written ? "已写入" : "未写入（提权被拒或命令失败）")}");
    }

    /// <summary>
    /// 写接口跃点数（权限失败降级为日志提示，与 WriteThrottlingIndex 同模式）。
    /// 【审查修复 R1】消除 sync-over-async：改为 async Task，取消全程 await + CancellationToken 透传。
    /// </summary>
    /// <returns>写入是否成功（供还原路径聚合失败，🟠 审查 2026-09-10）。</returns>
    private async Task<bool> WriteInterfaceMetric(string adapter, int metric, Action<string> onLine, CancellationToken ct = default)
    {
        // 🟡 审查 2026-09-10（🟡-7）：回显与执行共用同一构造——原先回显是手工拼的，
        // 与 NetshArgs.Name() 的引号卫生不一致，用户看到的命令可能与实际执行的不同。
        string netshArgs = NetshArgs.SetInterfaceMetric(adapter, metric);
        onLine($"$ netsh {netshArgs}");
        try
        {
            int exit = await _runner.RunAsync("netsh", netshArgs, onLine, ct, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            onLine(exit switch
            {
                0 => $"[调优] ✅ 「{adapter}」跃点数已设为 {metric}",
                1223 => $"[调优] ⚠️ 「{adapter}」跃点数未设置：用户拒绝 UAC 提权，已安全终止（无副作用）",
                _ => $"[调优] ❌ 「{adapter}」跃点数设置失败（退出码 {exit}）",
            });
            return exit == 0;
        }
        catch (Exception ex)
        {
            onLine($"[调优] ❌ 「{adapter}」跃点数设置异常：{ex.Message}");
            return false;
        }
    }

    // ─────────────────────── 内部 ───────────────────────

    /// <summary>
    /// 写 NetworkThrottlingIndex（【真机修复】注册表写入包权限降级：
    /// 非提权时 UnauthorizedAccessException → 日志提示，绝不穿透到 VM；
    /// restore 语义时先经调用方比较，避免无谓写入）。
    /// 【非提权宿主适配】注入了 <c>throttlingWriter</c> 时走提权 Helper 通道（见构造器注）。
    /// </summary>
    /// <returns>写入是否成功（false = UAC 拒绝/Helper 失败——调用方不得计入 applied）。</returns>
    private async Task<bool> WriteThrottlingIndexAsync(uint value, Action<string> onLine, bool restore = false)
    {
        onLine($"$ [注册表] HKLM\\…\\Tcpip\\Parameters\\NetworkThrottlingIndex = 0x{value:X}");
        if (_throttlingWriter is not null)
        {
            // 审查 O3（2026-09-10）：退出码曾被委托签名 Task 擦除——1223/失败仍报"已写入"。
            // 改按退出码分支：0=成功；1223=用户拒绝 UAC（安全终止）；其余=失败。
            int exit = await _throttlingWriter(value, onLine).ConfigureAwait(false);
            if (exit == 0)
            {
                onLine(restore
                    ? $"[调优] NetworkThrottlingIndex 已还原为 0x{value:X}"
                    : $"[调优] NetworkThrottlingIndex 已写入 0x{value:X}");
                return true;
            }

            onLine(exit == 1223
                ? $"[调优] ⚠️ NetworkThrottlingIndex 写入：用户拒绝 UAC 提权，已安全终止（无副作用）"
                : $"[调优] ❌ NetworkThrottlingIndex 写入失败（退出码 {exit}）");
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            onLine("[调优] ⚠️ 当前平台不支持注册表写入，且未配置提权写入通道——已跳过");
            return false;
        }

        try
        {
            TcpRegistry.WriteNetworkThrottlingIndex(value);
            onLine(restore
                ? $"[调优] NetworkThrottlingIndex 已还原为 0x{value:X}"
                : $"[调优] NetworkThrottlingIndex 已写入 0x{value:X}");
            return true;
        }
        catch (System.Security.SecurityException ex)
        {
            onLine($"[调优] ⚠️ 注册表写入被拒绝（{ex.Message}）——该项需要管理员权限，其余项不受影响");
        }
        catch (UnauthorizedAccessException ex)
        {
            onLine($"[调优] ⚠️ 注册表写入被拒绝（{ex.Message}）——该项需要管理员权限，其余项不受影响");
        }

        return false;
    }

    private async Task<bool> SetGlobalAsync(string setting, Action<string> onLine, CancellationToken ct = default)
    {
        onLine($"$ netsh interface tcp set global {setting}");
        int exit = await _runner.RunAsync("netsh", $"interface tcp set global {setting}", onLine, ct, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        // 🟠 审查 2026-09-10（🟠-4）：补 1223 分支——红线要求「1223 显式识别为安全终止，
        // 所有写入口一致」（口径与 WriteThrottlingIndexAsync 相同）。此前 1223 被并进
        // 通用失败文案，用户会误以为是程序 bug，而非自己拒了 UAC。
        onLine(exit switch
        {
            0 => $"[调优] ✅ {setting} 已应用",
            1223 => $"[调优] ⚠️ {setting} 未应用：用户拒绝 UAC 提权，已安全终止（无副作用）",
            _ => $"[调优] ❌ {setting} 应用失败（退出码 {exit}）——若提示拒绝访问，请以管理员身份运行",
        });
        return exit == 0; // 审查 O4/O10：回传成功与否供聚合
    }

    private void SaveSnapshot(TcpGlobalSettings current, IReadOnlyList<InterfaceMetricInfo> metrics, Action<string> onLine)
    {
        var values = new Dictionary<string, string>();
        if (current.AutoTuningLevel is not null)
        {
            values["autotuninglevel"] = current.AutoTuningLevel;
        }

        if (current.RssEnabled is not null)
        {
            values["rss"] = current.RssEnabled == true ? "enabled" : "disabled";
        }

        if (current.EcnEnabled is not null)
        {
            values["ecncapability"] = current.EcnEnabled == true ? "enabled" : "disabled";
        }

        Dictionary<string, int>? metricMap = metrics.Count == 0
            ? null
            : metrics.ToDictionary(m => m.Name, m => m.Metric);
        var snapshot = new TuningSnapshot(DateTime.Now, values, current.NetworkThrottlingIndex, metricMap);
        string? dir = Path.GetDirectoryName(_snapshotPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 【核实报告 W3/N6】快照是调优的「后悔药」，半截 JSON = 丢后悔药——走原子写入
        SystemToolkit.Core.Utilities.AtomicFile.WriteAllText(_snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        onLine($"[调优] 已保存改前快照（仅保留最近一份）：{values.Count} 项 netsh 参数 + NetworkThrottlingIndex{(current.NetworkThrottlingIndex is null ? "（未知，跳过）" : "")} + {metrics.Count} 项接口跃点数");
    }

    private static bool TryGetSwitch(Dictionary<string, string> values, string key, out bool value)
    {
        value = false;
        if (!values.TryGetValue(key, out string? text))
        {
            return false;
        }

        switch (text)
        {
            case "enabled":
                value = true;
                return true;
            case "disabled":
                value = false;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// HKLM NetworkThrottlingIndex 读写（系统边界不做单测；VM / 服务层经参数与回显可测）。
/// 默认值 10（0xA，出厂）；0xFFFFFFFF = 解除网络限流。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TcpRegistry
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

    public static uint? ReadNetworkThrottlingIndex()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KeyPath);
        // 【真机修复】值可能根本不存在（= 系统默认行为 10）：显示 0xA 而非「未知」，
        // 且应用差异比较时不会把「默认」误判成「未知」而拒绝写入
        return key?.GetValue("NetworkThrottlingIndex") is int value
            ? unchecked((uint)value)
            : TcpGlobalSettings.ThrottlingDefault;
    }

    public static void WriteNetworkThrottlingIndex(uint value)
    {
        using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(KeyPath);
        key.SetValue("NetworkThrottlingIndex", unchecked((int)value), Microsoft.Win32.RegistryValueKind.DWord);
    }
}
