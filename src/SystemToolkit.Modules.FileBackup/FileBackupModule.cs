using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>
/// 文件备份 模块（核心模块，不可禁用）。批次一：规则管理 + 手动备份 + 快照浏览/恢复/删除。
/// 批次二：AlphaVSS VSS 提权、恢复向导、Task Scheduler 定时与补做。
/// </summary>
/// <remarks>
/// 🔴 REVIEW-3 C-1（2026-09-06）：补做功能曾整体失效——本模块以实例字段持有
/// <c>IServiceScopeFactory</c> 并由 Shell 调 <c>AttachScopeFactory</c> 注入，但该方法
/// 全仓零调用，且 Shell 补做路径经 <c>KnownModules()</c> 新建的是影子实例 → NRE 被
/// 外层 catch 吞成一条 CrashLog。修法：scopeFactory 改<b>方法参数注入</b>（组合根在调用时
/// 传入，模块不再持有任何宿主引用），实例字段与 AttachScopeFactory 整体删除；
/// <see cref="ModulesWiringGuardTests"/> 守卫防回归。
/// </remarks>
public sealed class FileBackupModule : ModuleBase
{
    public override string Id => "filebackup";

    public override string DisplayName => "文件备份";

    public override int Order => 4;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<FileBackupView>();

    public override void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<ILogger>("filebackup", new FileLogger("filebackup"));

        // 配置服务（BackupConfigService）已提升至 Shell 的共享基础设施：
        // 设置模块同样需要读写它，模块各自注册会形成两份实例（状态分裂、互相覆盖）。
        services.AddSingleton<RuleManager>();
        services.AddSingleton<ElevatedVssClient>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<RestoreService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestoreService, RestoreService>();
        services.AddSingleton<IRestorePreviewProvider>(sp => sp.GetRequiredService<RestoreService>());
        services.AddSingleton<BackupTaskSchedulerService>();

        services.AddSingleton<FileBackupViewModel>();
        services.AddSingleton<FileBackupView>();
    }

    /// <summary>执行所有「已到期未执行」的定时备份（补做）。供 Shell 启动路径与 worker 模式复用。</summary>
    /// <param name="scopeFactory">
    /// 组合根传入（<c>ServiceProvider</c> 自身实现 <see cref="IServiceScopeFactory"/>）；
    /// 参数注入而非实例字段——影子实例/DI 单例两条实例链都可用。
    /// </param>
    public async Task RunDueScheduledBackupsAsync(IServiceScopeFactory scopeFactory, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        RuleManager rules = scope.ServiceProvider.GetRequiredService<RuleManager>();
        IBackupService backup = scope.ServiceProvider.GetRequiredService<IBackupService>();
        ILogger logger = scope.ServiceProvider.GetRequiredKeyedService<ILogger>("filebackup");

        foreach (BackupRule rule in rules.All.Where(r => BackupSchedule.IsDue(r, DateTime.Now)))
        {
            logger.Info($"定时备份补做：{rule.RuleName}({rule.RuleId})");
            BackupResult result = await backup.BackupRuleAsync(rule, null, ct).ConfigureAwait(false);
            rules.MarkRun(rule.RuleId, BackupSchedule.MarkRunDate(DateTime.Now));
            logger.Info($"定时备份补做完成：{rule.RuleName} success={result.Success} files={result.FileCount}");
        }
    }
}
