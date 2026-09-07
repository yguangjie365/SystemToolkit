using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>
/// Windows Task Scheduler 注册/注销（批次二）：每条启用定时的规则一个每日任务，
/// 触发命令 = 主程序 `--backup-worker &lt;ruleId&gt;`（headless 备份，不依赖 UI 常驻）。
/// 经 CommandRunner 直连 schtasks（当前用户任务无需管理员；越权失败经退出码与日志显式呈现）。
/// </summary>
public sealed class BackupTaskSchedulerService
{
    /// <summary>任务命名空间前缀（任务名 = 前缀 + 规则 ID 前 8 位）。</summary>
    public const string TaskNamePrefix = "SystemToolkitBackup_";

    private readonly ICommandRunner _runner;

    /// <summary>构造；runner 注入（直连 schtasks，不经提权白名单——当前用户任务无需 UAC）。</summary>
    public BackupTaskSchedulerService(ICommandRunner runner)
    {
        _runner = runner;
    }

    /// <summary>构造任务名（规则 ID 前 8 位）。</summary>
    public static string TaskNameFor(string ruleId)
        => TaskNamePrefix + ruleId.Substring(0, Math.Min(8, ruleId.Length));

    /// <summary>
    /// 注册（覆盖式）每日定时任务：每日 <paramref name="dailyTime"/>（HH:mm）以
    /// `--backup-worker &lt;ruleId&gt;` 启动主程序执行该规则。
    /// </summary>
    /// <returns>0 = 成功；非 0 = schtasks 退出码。</returns>
    public async Task<int> RegisterAsync(
        string ruleId, string dailyTime, string shellExePath, Action<string>? onLog = null, CancellationToken ct = default)
    {
        if (!BackupSchedule.IsValidDailyTime(dailyTime))
        {
            onLog?.Invoke($"[定时] ❌ 时间非法（应为 HH:mm，00:00-23:59）：{dailyTime}");
            return -1;
        }

        // /f = 同名覆盖；/sc daily /st = 每日定时；/tr = 触发命令（引号包裹含空格路径）
        string args = $"/create /f /tn \"{TaskNameFor(ruleId)}\" /sc daily /st {dailyTime} " +
                      $"/tr \"\\\"{shellExePath}\\\" --backup-worker {ruleId}\"";
        return await _runner.RunAsync("schtasks", args, onLog ?? (_ => { }), ct: ct, timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
    }

    /// <summary>注销任务（幂等：任务不存在时 schtasks 返回非 0，按成功处理并留痕）。</summary>
    public async Task<int> UnregisterAsync(string ruleId, Action<string>? onLog = null, CancellationToken ct = default)
    {
        int exit = await _runner.RunAsync(
            "schtasks", $"/delete /f /tn \"{TaskNameFor(ruleId)}\"", onLog ?? (_ => { }), ct: ct, timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        if (exit != 0)
        {
            onLog?.Invoke("[定时] 任务不存在或已注销（视为成功）");
            return 0;
        }

        return 0;
    }

    /// <summary>查询任务是否已注册（schtasks /query；存在返回 true）。</summary>
    public async Task<bool> IsRegisteredAsync(string ruleId, CancellationToken ct = default)
    {
        int exit = await _runner.RunAsync(
            "schtasks", $"/query /tn \"{TaskNameFor(ruleId)}\"", _ => { }, ct: ct, timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        return exit == 0;
    }
}
