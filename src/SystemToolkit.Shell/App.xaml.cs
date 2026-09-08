using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Logging;

namespace SystemToolkit.Shell;

/// <summary>
/// 宿主入口。规则 6：全仓只有 Shell / ElevatedHelper / Worker 可以 BuildServiceProvider。
/// </summary>
public partial class App : Application
{
    /// <summary>单实例互斥量名（Global\ 前缀：应用以管理员权限运行，需跨会话可见）。</summary>
    private const string SingleInstanceMutexName = @"Global\SystemToolkit_SingleInstance";

    private ServiceProvider? _services;

    /// <summary>单实例互斥量（持有至进程退出；为 null 表示未取得，进程即将退出）。</summary>
    private Mutex? _singleInstanceMutex;

    public ServiceProvider Services => _services
        ?? throw new InvalidOperationException("服务容器尚未初始化");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 单实例守卫（2026-09-07 补齐旧版能力）：防两个实例并发写 rules.json ──
        // 必须在日志初始化之前尝试，但提示需要日志——故先取互斥量，拿到后再建日志。
        if (!TryAcquireSingleInstance())
        {
            System.Windows.MessageBox.Show(
                "SystemToolkit 已经在运行中。\n\n为避免两个实例同时读写配置与备份规则造成数据损坏，本次启动已退出。",
                "SystemToolkit", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // ── 日志系统初始化：必须最早，之后任何失败才有地方留痕 ──
        // 落点：分模块文件 + 汇总（文本 + JSONL），带滚动与保留期（防 736MB 事故重演）
        AppLog.UseDefaultFileSinks();
        bool diag = e.Args.Contains("--diag", StringComparer.OrdinalIgnoreCase);
        AppLog.MinimumLevel = diag ? LogLevel.Trace : LogLevel.Info;
        _shellLogger = AppLog.CreateLogger("shell");
        _firstChanceThrottle = new ExceptionLogThrottle(_shellLogger);
        _shellLogger.Info($"=== 启动 === 级别={AppLog.MinimumLevel} 参数={string.Join(' ', e.Args)}");

        // --export-diag：只导出诊断包，不启动主窗口（用户可直接在终端执行）
        if (e.Args.Contains("--export-diag", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string path = DiagnosticsExporter.Export();
                _shellLogger.Info($"诊断包已导出：{path}");
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex)
            {
                CrashLog.Write("导出诊断包失败", ex);
            }

            Shutdown();
            return;
        }

        // 🔴 首次异常**默认常开**（不像旧实现那样只在 --diag 下记录）：
        //    「崩溃了但日志一片空白」是本次关闭崩溃事故中最拖时间的一环——
        //    异常在被 catch 的瞬间就落盘，才谈得上取证。靠 ExceptionLogThrottle 去重控量，
        //    --diag 额外把级别提到 TRACE 以输出更细的正常流程日志。
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashLog.Write("AppDomain.UnhandledException", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        var services = new ServiceCollection();
        foreach (IModule module in KnownModules())
        {
            module.RegisterServices(services);
            services.AddSingleton(module);
            // 🔴 同时按具体类型注册同一实例：AddSingleton(module) 的服务类型是 IModule，
            // 只注册它会导致 GetRequiredService<FileBackupModule>() 这类按具体类型解析失败
            // （2026-09-08：定时补做改走 DI 解析时踩到，补做会静默失效）。
            services.AddSingleton(module.GetType(), module);
        }
        RegisterSharedInfrastructure(services);
        services.AddSingleton<IServiceProvider>(sp => sp);
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        // --backup-worker <ruleId>：headless 定时备份模式（Task Scheduler 触发），不启动主窗口
        int workerIdx = Array.IndexOf(e.Args, "--backup-worker");
        if (workerIdx >= 0 && e.Args.Length > workerIdx + 1)
        {
            string ruleId = e.Args[workerIdx + 1];
            _ = RunBackupWorkerAsync(ruleId);
            return;
        }

        // 正常启动：错过的定时备份补做（关机期间错过的规则，启动后自动补做一次）
        _ = RunDueScheduledBackupsSafeAsync();

        CrashLog.Info("服务容器构建完成，正在解析 MainWindow...");
        MainWindow window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        CrashLog.Info("window.Show() 已执行，启动完成");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 🔴 顺序很重要（2026-09-06 实测崩溃修复）：先释放，最后才取消订阅。
        //    旧实现第一行就 `DispatcherUnhandledException -= …`，随后 _services.Dispose() 抛
        //    InvalidOperationException（FileWebServer 只实现 IAsyncDisposable）时已无人接管
        //    → 未处理异常 → 退出码 0xE0434352。关闭程序时崩溃，日志一片空白，极难定位。
        FlushSwallowWindow(); // 收尾：否则最后一个窗口里被吞的异常计数永久丢失
        _firstChanceThrottle?.FlushSummary(); // 被限流吞掉的异常，退出前补一条汇总（有几类、各多少次）
        _shellLogger?.Info("=== 退出 ===");
        try
        {
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            // 退出路径的最后一道兜底：任何服务的释放异常都不该演变成"关闭即崩溃"。
            // 进程本来就要结束，这里的目的只是留痕。
            CrashLog.Write("释放服务容器失败", ex);
        }

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    /// <summary>
    /// 单实例守卫：尝试取得全局互斥量。已被占用（另一实例在运行）返回 false。
    /// 目的（补齐旧版 Mutex 能力）：两个实例同时写 rules.json / settings.json 会互相覆盖，
    /// 备份规则与快照元数据损坏代价高——宁可拒绝第二次启动。
    /// </summary>
    private bool TryAcquireSingleInstance()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            _singleInstanceMutex = mutex;
            return true;
        }
        catch (Exception)
        {
            // 互斥量不可用（权限/会话隔离等）不应阻断启动——最坏情况退回旧行为，
            // 但要保证不因守卫本身让应用打不开
            return true;
        }
    }

    /// <summary>
    /// 共享基础设施注册（模块白名单不含 Infrastructure，由宿主组合根统一注册）。
    /// 🔴 DiRegistrationGuardTests 按模块建容器做解析校验时也必须调用本方法——单一事实来源。
    /// </summary>
    public static void RegisterSharedInfrastructure(IServiceCollection services)
    {
        services.AddSingleton<SystemToolkit.Core.FileTransfer.Services.IFileWebServer,
            SystemToolkit.Infrastructure.FileTransfer.FileWebServer>();
        // MUSIC-4：音乐播放引擎（Infrastructure 实现，Core 契约；模块经 DI 延迟解析）。
        // 引擎构造要非 keyed ILogger——工厂直接给音乐引擎专属 FileLogger（走 AppLog 总线）。
        services.AddSingleton<SystemToolkit.Core.Music.Services.IMusicPlaybackEngine>(_ =>
            new SystemToolkit.Infrastructure.Music.NAudioMusicPlayerEngine(
                new SystemToolkit.Core.Contracts.FileLogger("musicplayer")));
        services.AddSingleton<SystemToolkit.Core.Network.Services.ICommandRunner,
            SystemToolkit.Core.Network.Services.CommandRunner>();

        // 2026-09-07：备份配置提升为共享单例——备份模块与设置模块都要读写同一份
        // settings.json，各自注册会形成两个实例（内存状态分裂、互相覆盖）。
        services.AddSingleton<SystemToolkit.Core.Backup.Services.BackupConfigService>();
    }

    /// <summary>headless 定时备份：执行单条规则 → 记账 LastRunDate → 退出进程。</summary>
    private async Task RunBackupWorkerAsync(string ruleId)
    {
        CrashLog.Info($"backup-worker 启动：{ruleId}");
        try
        {
            RuleManager rules = _services!.GetRequiredService<RuleManager>();
            SystemToolkit.Core.Backup.Models.BackupRule? rule = rules.Get(ruleId);
            if (rule is null || !SystemToolkit.Core.Backup.Services.BackupSchedule.IsDue(rule, DateTime.Now))
            {
                CrashLog.Info($"backup-worker：规则 {ruleId} 不存在或未到期，退出");
                Shutdown();
                return;
            }

            IBackupService backup = _services!.GetRequiredService<IBackupService>();
            SystemToolkit.Core.Backup.Contracts.BackupResult result =
                await backup.BackupRuleAsync(rule, null, CancellationToken.None).ConfigureAwait(false);
            if (result.Success && !result.Canceled)
            {
                rules.MarkRun(ruleId, SystemToolkit.Core.Backup.Services.BackupSchedule.MarkRunDate(DateTime.Now));
                CrashLog.Info($"backup-worker 完成：{ruleId} success={result.Success} files={result.FileCount}");
            }
            else
            {
                CrashLog.Info($"backup-worker 未记账（失败或取消，留待下次重试）：{ruleId} success={result.Success} canceled={result.Canceled}");
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("backup-worker 失败", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>正常启动时补做所有到期的定时备份（fire-and-forget，失败留痕不拖垮启动）。</summary>
    private async Task RunDueScheduledBackupsSafeAsync()
    {
        try
        {
            // REVIEW-3 C-1：scopeFactory 参数注入（旧实现依赖 AttachScopeFactory 字段注入——
            // 该方法全仓零调用 + KnownModules 影子实例，导致补做必抛 NRE 静默失败）
            // 2026-09-08（审查 S-4）：模块也从 DI 解析，不再经 KnownModules()——
            // 组合根应只有 DI 一个实例来源，避免"两条实例链"隐患。
            SystemToolkit.Modules.FileBackup.FileBackupModule fileBackupModule =
                _services!.GetRequiredService<SystemToolkit.Modules.FileBackup.FileBackupModule>();
            await fileBackupModule
                .RunDueScheduledBackupsAsync(_services!.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLog.Write("定时备份补做失败", ex);
        }
    }

    private static IReadOnlyList<IModule>? _knownModules;

    /// <summary>模块清单：按导航顺序排列。新增模块须同步 DependencyGuard 白名单。
    /// 🔴 REVIEW-3 A-2：静态单例缓存——旧实现每次调用 new 一批，OnStartup 至少调两次，
    /// DI 注册的单例与补做路径的新实例并存（影子实例），模块持有状态时必有一链拿不到注入。</summary>
    public static IReadOnlyList<IModule> KnownModules() => _knownModules ??=
    [
        new SystemToolkit.Modules.Overview.OverviewModule(),
        new SystemToolkit.Modules.AppManager.AppManagerModule(),
        new SystemToolkit.Modules.DriverManager.DriverManagerModule(),
        new SystemToolkit.Modules.FileBackup.FileBackupModule(),
        new SystemToolkit.Modules.FileTransfer.FileTransferModule(),
        new SystemToolkit.Modules.NetManager.NetManagerModule(),
        new SystemToolkit.Modules.RecoveryManager.RecoveryManagerModule(),
        new SystemToolkit.Modules.GameManager.GameManagerModule(),
        new SystemToolkit.Modules.MusicManager.MusicManagerModule(),
        new SystemToolkit.Modules.Settings.SettingsModule(),
    ];

    private int _inFirstChanceLogger;
    private SystemToolkit.Core.Contracts.ILogger? _shellLogger;
    private ExceptionLogThrottle? _firstChanceThrottle;

    private void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        // 防递归：日志写入自身失败会产生新异常
        if (System.Threading.Interlocked.Exchange(ref _inFirstChanceLogger, 1) == 1)
        {
            return;
        }

        try
        {
            // 只记与本项目相关的异常，过滤框架噪音
            if (e.Exception.StackTrace?.Contains("SystemToolkit", StringComparison.Ordinal) != true)
            {
                return;
            }

            // 去重：同类型同首帧只完整记录首次，后续仅计数（否则渲染循环类异常会刷爆磁盘）
            if (_firstChanceThrottle?.ShouldLog(e.Exception) == false)
            {
                return;
            }

            CrashLog.WriteFirstChance(e.Exception);
        }
        finally
        {
            System.Threading.Volatile.Write(ref _inFirstChanceLogger, 0);
        }
    }

    // 审查 2026-09-04（P1-7）：异常风暴熔断。渲染循环类异常若被无条件吞掉，
    // 会每帧重复触发并把日志刷爆（旧工程 736MB 日志事故同款模式）。
    // 短窗口内超限后放行异常让其终止进程——CrashLog 已留痕，宁可崩溃留尸也不无限吞。
    //
    // 🔴 S-2（审查 2026-09-06 修正）：原实现「每条都 CrashLog.Write」本身就是风暴源——
    //    5s × 100 条 = 每 5 秒 100 次同步磁盘写，每条带堆栈 2-4KB → 3 分钟 8-15MB，UI 线程同步阻塞。
    //    改为「窗口首条完整 + 中段只计频 + 窗口结束写一条汇总（含末条堆栈）」：
    //    落盘量从 100 条/5s 降到 2 条/5s（降 50×），可诊断性不丢（首末条堆栈都在）。
    //
    //    ⚠️ 刻意**不做** CrashLog 异步批写：崩溃日志的价值恰在于「进程马上要死」时仍能落盘，
    //    改异步会在最需要的时刻丢日志。降频已经解决了风暴问题，不必再引入这个风险。
    private static readonly TimeSpan SwallowWindow = TimeSpan.FromSeconds(Core.Configuration.AppConstants.ExceptionSwallowWindowSeconds);
    private const int SwallowLimit = Core.Configuration.AppConstants.ExceptionSwallowLimit;

    private int _swallowCount;
    private DateTime _swallowWindowStart;
    private Exception? _swallowLast;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _swallowWindowStart > SwallowWindow)
        {
            FlushSwallowWindow();
            _swallowWindowStart = now;
            _swallowCount = 0;
        }

        // 窗口首条：完整落盘（保留可诊断性）
        if (_swallowCount == 0)
        {
            CrashLog.Write("DispatcherUnhandledException（窗口首条）", e.Exception);
        }

        _swallowCount++;
        _swallowLast = e.Exception;

        if (_swallowCount > SwallowLimit)
        {
            CrashLog.Info(
                $"[熔断] 5s 窗口内第 {_swallowCount} 条未处理异常，已达上限 {SwallowLimit}——" +
                "停止吞异常，放行以终止进程（避免无限吞异常空转）");
            e.Handled = false;
            return;
        }

        e.Handled = true;
    }

    /// <summary>窗口收尾：把「被吞掉的中段异常」压成一条汇总落盘（只有首条已单独写过，故从 1 起算）。</summary>
    private void FlushSwallowWindow()
    {
        int suppressed = _swallowCount - 1;
        if (suppressed <= 0 || _swallowLast is null)
        {
            return;
        }

        CrashLog.Info(
            $"[异常风暴] 本 {SwallowWindow.TotalSeconds:0}s 窗口另有 {suppressed} 条未处理异常被吞" +
            "（同源高频，仅记末条堆栈，首条见上）");
        CrashLog.Write("DispatcherUnhandledException（窗口末条）", _swallowLast);
        _swallowLast = null;
    }
}
