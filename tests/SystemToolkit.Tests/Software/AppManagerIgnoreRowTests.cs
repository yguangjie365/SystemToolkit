using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using SystemToolkit.Modules.AppManager;

namespace SystemToolkit.Tests.Software;

/// <summary>
/// 软件管理「建行工厂」回归锁（V11-A1，2026-09-14）。
/// <para>
/// 起因（v11 单模块审查核实）：四处建行只有 <c>LoadAsync</c> 挂齐了三件套，另三处漏挂
/// <c>HookIgnore</c> / 未回写 <c>IsIgnored</c>，症状是「右键『永久忽略』可点、点了毫无反应」
/// 与「编辑一个已忽略的软件后，它从『已忽略』视图消失（忽略清单里其实还在）」。
/// 🔴 而**全仓此前无任何测试构造 <c>WingetPackageVm</c>**（<c>Core/PackageIgnoreTests.cs</c> 只覆盖
/// 纯逻辑层 <c>PackageIgnoreList</c>）⇒ 既有 1600+ 条测试一条都抓不到。本文件把「建行三件套」钉死。
/// </para>
/// <para>
/// 断言口径：不只看 <c>IsIgnored</c> 的取值，而是**读回落盘的忽略清单** ——
/// 「命令真的把条目写进了忽略清单」才是缺陷的实质
/// （<c>IgnoreCommand</c> 是空条件调用 <c>_ignoreRequest?.Invoke(this)</c>，未挂接时**无日志、无写入**）。
/// </para>
/// </summary>
public sealed class AppManagerIgnoreRowTests : IDisposable
{
    private const string PackageId = "Test.Package.One";

    private readonly string _dir;
    private readonly EnvListService _env;
    private readonly PackageIgnoreStore _ignoreStore;
    private readonly InstallHistoryStore _historyStore;
    private readonly ILogger _logger = new NoopLogger();

    public AppManagerIgnoreRowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "appmgr-row-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _env = new EnvListService(envDir: _dir);
        _ignoreStore = new PackageIgnoreStore(envDir: _dir);
        _historyStore = new InstallHistoryStore(envDir: _dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    // ════════ ① 忽略命令真的写进忽略清单（本缺陷的实质） ════════

    [Fact]
    public void Row_IgnoreCommand_WritesIntoIgnoreStore() => RunOnUiThread(async () =>
    {
        SeedCatalog();
        WingetPackageVm row = await LoadSingleRowAsync();

        Assert.True(row.CanIgnore);   // 未忽略：右键「永久忽略」可用（挂接前的症状正是"可点"）
        Assert.False(row.IsIgnored);

        row.IgnoreCommand.Execute(null);

        // 行内态翻转
        Assert.True(row.IsIgnored);
        Assert.False(row.CanIgnore);
        Assert.True(row.CanUnignore);

        // 🔴 实质证据：读回落盘清单（未挂接 HookIgnore 时这里一条都没有）
        Assert.True(_ignoreStore.Load().IsIgnored(PackageId, "winget", null),
            "IgnoreCommand 未写入忽略清单 —— 建行时漏挂 HookIgnore（V11-A1 症状：右键可点但毫无反应）");
    });

    [Fact]
    public void Row_UnignoreCommand_RemovesFromIgnoreStore() => RunOnUiThread(async () =>
    {
        SeedCatalog();
        WingetPackageVm row = await LoadSingleRowAsync();

        row.IgnoreCommand.Execute(null);
        Assert.True(_ignoreStore.Load().IsIgnored(PackageId, "winget", null));

        row.UnignoreCommand.Execute(null);

        Assert.False(row.IsIgnored);
        Assert.False(_ignoreStore.Load().IsIgnored(PackageId, "winget", null));
    });

    // ════════ ② 已忽略态在加载/编辑替换后必须保持（工厂的 ③ 回写） ════════

    [Fact]
    public void LoadAsync_RestoresIgnoredStateFromStore() => RunOnUiThread(async () =>
    {
        SeedCatalog();
        SeedIgnored();

        WingetPackageVm row = await LoadSingleRowAsync();

        Assert.True(row.IsIgnored);
    });

    [Fact]
    public void EditSoftware_ReplacedRow_KeepsIgnoreStateAndIgnoreHooks() => RunOnUiThread(async () =>
    {
        SeedCatalog();
        SeedIgnored();

        AppManagerViewModel vm = CreateVm();
        WingetPackageVm row = await LoadSingleRowAsync(vm);
        Assert.True(row.IsIgnored);

        // 编辑（改个名）→ 行被替换为新实例
        vm.SoftwareEditRequest = _ => new SoftwareEditResult
        {
            Item = new WingetPackage { Id = PackageId, Name = "改过名的软件", Source = "winget", Category = "其他" },
        };
        vm.EditSoftwareCommand.Execute(row);

        WingetPackageVm replaced = Assert.Single(vm.ThirdPartyPackages);
        Assert.NotSame(row, replaced);
        Assert.Equal("改过名的软件", replaced.Name);

        // 漏回写 IsIgnored 时：该行会被「已忽略」视图剔除 = "我没取消忽略，它却不见了"
        Assert.True(replaced.IsIgnored, "编辑替换后忽略态丢失 —— 建行未回写 IsIgnored（V11-A1）");

        // 且忽略命令必须已挂接（否则这一行今后点忽略/取消忽略都没反应）
        replaced.UnignoreCommand.Execute(null);
        Assert.False(_ignoreStore.Load().IsIgnored(PackageId, "winget", null));
    });

    // ════════ 夹具 ════════

    /// <summary>预置一份含单个 winget 包的清单（走 Core 的公开落盘 API，不手写 JSON）。</summary>
    private void SeedCatalog()
        => _env.SaveWinget([new WingetPackage { Id = PackageId, Name = "测试软件", Source = "winget", Category = "其他" }]);

    /// <summary>预置忽略清单（永久忽略该包）。</summary>
    private void SeedIgnored()
        => _ignoreStore.Save(new PackageIgnoreList
        {
            Entries = [new PackageIgnoreEntry { Id = PackageId, Source = "winget", Scope = PackageIgnoreScope.Permanent }],
        });

    private AppManagerViewModel CreateVm()
        => new(_env, new StubWingetClient(), _logger, _ignoreStore, _historyStore);

    private async Task<WingetPackageVm> LoadSingleRowAsync(AppManagerViewModel? vm = null)
    {
        AppManagerViewModel target = vm ?? CreateVm();
        await target.LoadAsync();
        return Assert.Single(target.ThirdPartyPackages);
    }

    /// <summary>
    /// 在**单个带 Dispatcher 的 STA 线程**上跑测试体。
    /// <para>
    /// 🔴 必要性：<c>LoadAsync</c> 会经 <c>CollectionViewSource.GetDefaultView</c> 建视图，
    /// 而 <c>ListCollectionView</c> 有线程亲和性 —— 若布局/替换发生在另一个线程（xunit 默认在线程池上
    /// 续跑 async 用例），WPF 会抛「不支持从调度程序线程以外的线程对其 SourceCollection 进行更改」。
    /// 真实应用里这些调用全部发生在 UI 线程，故测试也必须模拟"单 UI 线程语义"。
    /// </para>
    /// </summary>
    private static void RunOnUiThread(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run(); // 直到 InvokeShutdown：await 的续体都回到本线程
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "测试体在 UI 线程上超时（30s）未结束");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw(); // 保留原始堆栈（断言失败信息原样可见）
        }
    }

    // ════════ 测试替身 ════════

    /// <summary>
    /// winget 客户端假件：本文件只验证**建行与忽略链路**，不碰 winget 进程。
    /// 查询类返回"未知"（不臆造安装态），执行类返回退出码 1（失败），避免用例隐式依赖真实 winget。
    /// </summary>
    private sealed class StubWingetClient : IWingetClient
    {
        public Action<string> OutputSink { get; set; } = _ => { };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<string> ListInstalledAsync(CancellationToken ct = default) => Task.FromResult("");

        public Task<string> ListUpgradesAsync(CancellationToken ct = default) => Task.FromResult("");

        public Task<WingetQueryResult> QueryAsync(string id, string? source = null, CancellationToken ct = default)
            => Task.FromResult(new WingetQueryResult(Installed: false, Version: null, AvailableVersion: null, Unknown: true));

        public Task<WingetRunResult> InstallAsync(string id, string? source = null, CancellationToken ct = default)
            => Task.FromResult(new WingetRunResult(1, false));

        public Task<WingetRunResult> UpgradeAsync(string id, string? source = null, CancellationToken ct = default)
            => Task.FromResult(new WingetRunResult(1, false));

        public Task<WingetRunResult> UninstallAsync(string id, string? source = null, CancellationToken ct = default)
            => Task.FromResult(new WingetRunResult(1, false));

        public Task<WingetRunResult> UpdateSourceAsync(CancellationToken ct = default)
            => Task.FromResult(new WingetRunResult(1, false));

        public Task<WingetRunResult> SetSourceAsync(string name, string url, CancellationToken ct = default)
            => Task.FromResult(new WingetRunResult(1, false));

        public Task<WingetRunResult> ResetSourceAsync(string name, CancellationToken ct = default)
            => Task.FromResult(new WingetRunResult(1, false));

        public Task<string> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult("");

        public Task<WingetRunResult> ExportAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new WingetRunResult(1, false));
    }
}
