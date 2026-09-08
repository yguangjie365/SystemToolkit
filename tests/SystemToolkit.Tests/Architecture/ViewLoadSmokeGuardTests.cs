using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Drivers;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Infrastructure.FileTransfer;
using SystemToolkit.Modules.DriverManager;
using SystemToolkit.Modules.FileBackup;
using SystemToolkit.Modules.FileTransfer;
using SystemToolkit.Modules.GameManager;
using SystemToolkit.Core.Music.Services;
using SystemToolkit.Modules.MusicManager;
using SystemToolkit.Modules.NetManager;
using Microsoft.Extensions.DependencyInjection;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 视图加载守卫（2026-09-06 事故沉淀：驱动管理页上线即闪退，退出码 0xE0434352）。
/// <para>
/// 事故根因：WPF 的 <c>Run.Text</c> 依赖属性 <c>BindsTwoWayByDefault=true</c>，
/// 而 <c>DriverPackageVm.ClassName</c> 是只读派生属性（仅 getter）→ 模板绑定求值阶段抛
/// <c>InvalidOperationException</c>（无法对只读属性进行 TwoWay 绑定）→ 未处理异常 → 进程崩溃。
/// 编译期与既有静态守卫全部无法发现：XAML 编译不校验绑定，静态扫描也读不到属性可写性。
/// </para>
/// <para>
/// 本守卫两层防御：
/// ① 静态：所有 <c>&lt;Run Text="{Binding …}"&gt;</c> 必须显式 <c>Mode=OneWay</c>（快，全量覆盖）；
/// ② 运行时：STA 线程构造模块 View + 填充数据 + 强制布局，捕获模板绑定求值期的真实异常（深）。
/// </para>
/// </summary>
public class ViewLoadSmokeGuardTests
{
    // ================= ① 静态层 =================

    /// <summary>匹配 &lt;Run Text="{Binding …}" 的绑定内容（捕获组 1 = 绑定表达式）。</summary>
    private static readonly Regex RunTextBinding =
        new(@"<Run\s+Text=""\{Binding([^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void XamlGuard_RunTextBinding_MustDeclareOneWay()
    {
        var violations = new List<string>();
        foreach (string xaml in EnumerateModuleXamls())
        {
            foreach (Match m in RunTextBinding.Matches(File.ReadAllText(xaml)))
            {
                // Run.Text 默认双向；只读属性（ClassName/SignerText 等派生属性）会被 TwoWay 绑定炸掉
                if (!m.Groups[1].Value.Contains("Mode=OneWay", StringComparison.Ordinal))
                {
                    violations.Add($"{Relative(xaml)}: <Run Text=\"{{Binding{m.Groups[1].Value}\"");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Run.Text 绑定必须显式 Mode=OneWay（Run.Text 默认双向，绑定只读属性会在模板求值时抛异常导致页面闪退）：\n"
            + string.Join("\n", violations));
    }

    // ================= ② 运行时层 =================

    [Fact]
    public void DriverManagerView_LoadsWithData_WithoutException()
    {
        Exception? captured = null;
        string stage = "init";

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct VM + View";
                var vm = new DriverManagerViewModel(null!, null!, null!);
                var view = new DriverManagerView(vm);

                // 填充数据后模板才求值——只读属性被 TwoWay 绑定的崩溃正发生在此之后
                stage = "fill Packages";
                vm.Packages.Add(MakePackage("oem12.inf", DriverPackageState.Current));
                vm.Packages.Add(MakePackage("oem18.inf", DriverPackageState.OldVersion));
                vm.Packages.Add(MakePackage("oem24.inf", DriverPackageState.SystemCritical));
                vm.PackagesView.Refresh();

                stage = "measure + arrange";
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                // M-UI-3 新增路径：抽屉「态 A(未选中=全局操作) ↔ 态 B(选中=包详情)」双向切换。
                // 只读属性被 TwoWay 绑定的崩溃同样发生在这条路径的模板求值上——
                // 只填充数据而不切换选中态，等于没覆盖抽屉详情区（态 B 的整组绑定）。
                stage = "drawer switch: null -> package";
                vm.SelectedPackage = vm.Packages[0];
                view.UpdateLayout();

                stage = "drawer switch: package -> null";
                vm.SelectedPackage = null;
                view.UpdateLayout();

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(captured is null,
            $"驱动管理 View 加载抛异常（阶段：{stage}）：\n{captured}");
    }

    /// <summary>
    /// 文件备份视图全页加载冒烟（2026-09-06 V0.6 批次一随附）：2 Tab + 规则/快照绑定。
    /// 与其它视图用例同类串行——Application 全 AppDomain 单实例，必须经 EnsureApplication 复用。
    /// </summary>
    [Fact]
    public void FileBackupView_LoadsWithTwoTabs_WithoutException()
    {
        Exception? captured = null;
        string stage = "init";
        string cfgDir = Path.Combine(Path.GetTempPath(), $"fb-view-smoke-{Guid.NewGuid():N}");

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct services + VM + View";
                var config = new BackupConfigService(cfgDir);
                config.Load();
                var vss = new ElevatedVssClient(
                    helperPath: Path.Combine(Path.GetTempPath(), "no-such-helper.exe"));
                var vm = new FileBackupViewModel(
                    config, new RuleManager(Path.Combine(cfgDir, "rules"), null),
                    new BackupService(config, vssClient: vss), new RestoreService(config),
                    new RestoreService(config), new BackupTaskSchedulerService(
                        new SystemToolkit.Core.Network.Services.CommandRunner()));
                var view = new FileBackupView(vm);

                stage = "measure + arrange";
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        try
        {
            if (Directory.Exists(cfgDir))
            {
                Directory.Delete(cfgDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响判定
        }

        Assert.True(captured is null,
            $"文件备份 View 加载抛异常（阶段：{stage}）：\n{captured}");
    }

    /// <summary>
    /// 文件互传视图全页加载冒烟（2026-09-06 V0.5 批次一随附）：2 Tab 布局 + 组合根 VM 构造。
    /// 与其它视图用例同类串行——Application 全 AppDomain 单实例，必须经 EnsureApplication 复用。
    /// </summary>
    [Fact]
    public void FileTransferView_LoadsWithTwoTabs_WithoutException()
    {
        Exception? captured = null;
        string stage = "init";

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct services + VM + View";
                var discovery = new DeviceDiscoveryService();
                var transfer = new FileTransferService(discovery);
                // 批次二组合语义：PairingService 单例共享给 Web 通道与 VM（与 Shell 组合根一致）
                var pairing = new PairingService();
                var vm = new FileTransferViewModel(
                    discovery,
                    transfer,
                    new TransferHistoryService(Path.Combine(Path.GetTempPath(), $"ft-view-smoke-{Guid.NewGuid():N}")),
                    new FileWebServer(discovery, pairing),
                    pairing);
                var view = new FileTransferView(vm);

                stage = "measure + arrange";
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(captured is null,
            $"文件互传 View 加载抛异常（阶段：{stage}）：\n{captured}");
    }

    /// <summary>
    /// 网络管理视图全页加载冒烟（2026-09-06 V0.4 交付随附）：4 Tab 布局 + Run 绑定 +
    /// 组合根 VM 构造。与驱动用例同类串行执行——Application 全 AppDomain 单实例，
    /// 必须经 EnsureApplication 复用（ Driver 用例先行创建）。
    /// </summary>
    [Fact]
    public void NetManagerView_LoadsWithFourTabs_WithoutException()
    {
        Exception? captured = null;
        string stage = "init";
        string snapshotDir = Path.Combine(Path.GetTempPath(), $"net-view-smoke-{Guid.NewGuid():N}");

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct services + VM + View";
                var runner = new ElevatingCommandRunner(new CommandRunner(),
                    helperPath: Path.Combine(Path.GetTempPath(), "no-such-helper.exe"));
                var info = new NetworkInfoService();
                var tuning = new TcpTuningService(runner);
                var vm = new NetManagerViewModel(
                    info,
                    new NetConfigService(runner),
                    new NetworkSnapshotService(info, tuning, new NetConfigService(runner), snapshotDir),
                    new NetDiagnosticService(info, new WindowsNetProbe(), new HostsCheckService()),
                    new DnsProbeService(),
                    new ContinuousPingService(),
                    new NetRepairService(runner, info),
                    tuning,
                    new WindowsElevationProvider());
                var view = new NetManagerView(vm);

                stage = "measure + arrange";
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        try
        {
            if (Directory.Exists(snapshotDir))
            {
                Directory.Delete(snapshotDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响判定
        }

        Assert.True(captured is null,
            $"网络管理 View 加载抛异常（阶段：{stage}）：\n{captured}");
    }

    /// <summary>
    /// 游戏管理视图全页加载冒烟（2026-09-06 布局对齐参考稿随附）。
    /// <para>
    /// 本轮改动引入两个模板求值期风险模式，必须以「真实加载 + 填数据 + Measure」验证：
    /// ① 封面 hover 的 EventTrigger Storyboard 用 TargetName 引用 DataTemplate 内命名元素；
    /// ② 未安装徽章遮罩经 StaticResource 引用 UserControl.Resources 里的渐变笔刷。
    /// 用例覆盖三种卡态：有封面已安装 / 无封面（字母占位）/ 未安装（下载徽章）。
    /// 与其它视图用例同类串行——Application 全 AppDomain 单实例，必须经 EnsureApplication 复用。
    /// </para>
    /// </summary>
    [Fact]
    public void GameManagerView_LoadsWithThreeCardStates_WithoutException()
    {
        Exception? captured = null;
        string stage = "init";

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct VM + View";
                var vm = new GameManagerViewModel(null!, null!);
                var view = new GameManagerView(vm);

                stage = "fill three card states";
                vm.Games.Add(new GameCardVm(MakeGame(814380, "Sekiro", stateFlags: 4), "C:\\cover-a.jpg", vm));
                vm.Games.Add(new GameCardVm(MakeGame(1245620, "ELDEN RING", stateFlags: 4), string.Empty, vm));
                vm.Games.Add(new GameCardVm(MakeGame(2215430, "Torchlight", stateFlags: 6), string.Empty, vm));
                vm.GamesView.Refresh();

                stage = "measure + arrange";
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                stage = "header/status projections";
                // 页头副标题与状态栏统计投影依赖 Games 集合（NotifySummary 由 LoadAsync 触发，
                // 测试直接构造集合——此处手动触发以覆盖绑定路径）
                typeof(GameManagerViewModel)
                    .GetMethod("NotifySummary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(vm, null);
                view.UpdateLayout();

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(captured is null,
            $"游戏管理 View 加载抛异常（阶段：{stage}）：\n{captured}");
    }

    private static SteamGame MakeGame(uint appId, string name, uint stateFlags) => new()
    {
        AppId = appId,
        Name = name,
        InstallDir = name,
        LibraryPath = "D:\\SteamLibrary",
        SizeOnDisk = 25_400_000_000,
        StateFlags = stateFlags,
        PlaytimeMinutes = 5120,
        LastPlayed = 1_757_068_800,
    };

    /// <summary>
    /// 取得（或首次创建）本 AppDomain 唯一 <see cref="Application"/>。
    /// 🔴 System.Windows.Application 全 AppDomain 仅允许一个实例（实测「不能在同一 AppDomain
    /// 中创建多个 Application 实例」）——本类多个运行时用例必须复用。
    /// </summary>
    /// <summary>
    /// 音乐管理视图加载冒烟（MUSIC-6，2026-09-08）：三 Tab 构造 + Measure/Arrange。
    /// 引擎未注册（GetService 返回 null）——正是引擎缺席场景的隔离验证。
    /// </summary>
    [Fact]
    public void MusicManagerView_LoadsWithThreeTabs_WithoutException()
    {
        Exception? captured = null;
        string stage = "init";
        string dir = Path.Combine(Path.GetTempPath(), $"music-view-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct services + VM + View";
                ServiceProvider services = new ServiceCollection().BuildServiceProvider();
                var store = new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json"));
                var scanner = new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger()));
                var vm = new MusicManagerViewModel(
                    store,
                    scanner,
                    new PlaybackQueueService(),
                    new NoopLogger(),
                    engineProvider: null, // 引擎缺席场景的隔离验证
                    tagReader: new TagLibMusicTagReader(new NoopLogger()),
                    dispatcher: null);
                var view = new MusicManagerView(vm);

                stage = "measure + arrange";
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(captured is null,
            $"音乐管理 View 加载抛异常（阶段：{stage}）：\n{captured}");
    }

    /// <summary>
    /// 黑胶旋转冒烟（2026-09-08 真机事故）：播放 → <c>UpdateDiscSpin</c> → Storyboard.Begin。
    /// 🔴 code-behind 自建 Storyboard 时 <c>SetTarget</c> 必须配对 <c>SetTargetProperty</c>，
    /// 缺 TargetProperty 时 Begin 的 ClockTreeWalkRecursive 直接抛 InvalidOperationException。
    /// 仅构造 View + 布局抓不到——异常只在 IsPlaying 状态变化触发旋转时发生（同 NameScope 事故教训：
    /// 模板/动画求值发生在数据填充之后，守卫必须把状态走完）。
    /// </summary>
    [Fact]
    public void MusicManagerView_DiscSpinStartsAndPauses_WithoutException()
    {
        Exception? captured = null;
        string stage = "init";
        string dir = Path.Combine(Path.GetTempPath(), $"music-disc-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct services + VM + View";
                ServiceProvider services = new ServiceCollection().BuildServiceProvider();
                var store = new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json"));
                var scanner = new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger()));
                var vm = new MusicManagerViewModel(
                    store,
                    scanner,
                    new PlaybackQueueService(),
                    new NoopLogger(),
                    engineProvider: null,
                    tagReader: new TagLibMusicTagReader(new NoopLogger()),
                    dispatcher: null);
                var view = new MusicManagerView(vm);
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                stage = "raise Loaded（订阅 vm.PropertyChanged）";
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

                // IsPlaying 是私有 setter（引擎状态回写专用）——测试经反射触发真实 PropertyChanged 路径，
                // 不为测试放宽生产代码可访问性
                Action<object?> setPlaying = value => typeof(MusicManagerViewModel)
                    .GetProperty(nameof(MusicManagerViewModel.IsPlaying))!
                    .GetSetMethod(nonPublic: true)!
                    .Invoke(vm, [value]);

                stage = "IsPlaying=true → Storyboard.Begin";
                setPlaying(true); // 修复前：此处抛「必须为 DoubleAnimation 指定 TargetProperty」
                view.UpdateLayout();

                stage = "IsPlaying=false → Pause";
                setPlaying(false);

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(captured is null,
            $"黑胶旋转 Storyboard 抛异常（阶段：{stage}）：\n{captured}");
    }

    internal static Application EnsureApplication()
    {
        if (Application.Current is not null)
        {
            return Application.Current;
        }

        return new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    }

    // ================= 辅助 =================

    /// <summary>
    /// 加载 Claude.Light 主题包供 View 解析 StaticResource。
    /// 🔴 该包的 FontFamily 用 <c>pack://application:,,,/Resources/Fonts/…</c> 指向应用主程序集，
    /// 测试宿主无此字体 → 直接解析必失败。故仅替换字体内容、保留 x:Key（字体内容对本守卫无意义）。
    /// </summary>
    internal static ResourceDictionary LoadThemeWithFontsStubbed()
    {
        string path = Path.Combine(RepoRoot(),
            "src/SystemToolkit.UI.Common/Themes/Packs/Claude/Claude.Light.xaml");
        string xaml = File.ReadAllText(path);
        xaml = Regex.Replace(xaml,
            @"<FontFamily x:Key=""(Font_[^""]+)"">[^<]*</FontFamily>",
            @"<FontFamily x:Key=""$1"">Consolas, Microsoft YaHei UI</FontFamily>");
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }

    private static DriverPackageVm MakePackage(string inf, DriverPackageState state)
        => new(new DriverPackage
        {
            PublishedName = inf,
            OriginalName = inf,
            ClassName = "Display",
            Provider = "NVIDIA",
            Version = "31.0.15.1234",
            Date = new DateTime(2026, 5, 21),
            SignerName = "Microsoft Windows Hardware Compatibility Publisher",
            DeviceNames = ["NVIDIA GeForce RTX 4060 Laptop GPU"],
            State = state,
        });

    /// <summary>
    /// 全屏播放器三风格模板冒烟（OM-6，2026-09-08）：切换 彩胶/沉浸/现代 三模板，
    /// 每风格 Measure/Arrange 触发模板求值——防 XAML 绑定/图片刷绑定错误上线即崩。
    /// （单例 View 上 vm 状态切换即换可见模板；无引擎/无封面路径一并覆盖。）
    /// </summary>
    [Fact]
    public void MusicManagerView_ThreePlayerStyles_AllMeasureWithoutException()
    {
        Exception? captured = null;
        string stage = "init";
        string dir = Path.Combine(Path.GetTempPath(), $"music-style-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication().Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                stage = "construct services + VM + View";
                ServiceProvider services = new ServiceCollection().BuildServiceProvider();
                var store = new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json"));
                var scanner = new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger()));
                var vm = new MusicManagerViewModel(
                    store,
                    scanner,
                    new PlaybackQueueService(),
                    new NoopLogger(),
                    engineProvider: null,
                    tagReader: new TagLibMusicTagReader(new NoopLogger()),
                    dispatcher: null);
                vm.LyricRows.Add(new MusicManagerViewModel.LyricRowVm("第一行", false));
                vm.LyricRows.Add(new MusicManagerViewModel.LyricRowVm("第二行", true));
                var view = new MusicManagerView(vm);

                stage = "vinyl default";
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();

                foreach (string style in new[] { "Immersion", "Modern", "Vinyl" })
                {
                    stage = "style switch: " + style;
                    vm.SwitchPlayerStyleCommand.Execute(style);
                    view.Measure(new Size(1600, 900));
                    view.Arrange(new Rect(0, 0, 1600, 900));
                    view.UpdateLayout();
                }

                stage = "eq panel open + preset + slider refresh";
                vm.ToggleEqPanelCommand.Execute(null); // 弹出 EQ 面板（模板求值）
                vm.ApplyEqPresetCommand.Execute("流行"); // 预设 → 滑条 RefreshFrom + 高亮
                vm.EqBands[0].Gain = 3; // 手动拖动 → 清预设高亮 + 热更
                view.Measure(new Size(1600, 900));
                view.Arrange(new Rect(0, 0, 1600, 900));
                view.UpdateLayout();
                vm.ToggleEqPanelCommand.Execute(null); // 收起

                stage = "done";
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(captured is null,
            $"音乐管理三风格模板加载抛异常（阶段：{stage}）：\n{captured}");
    }

    private static IEnumerable<string> EnumerateModuleXamls()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains("/obj/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal));

    private static string Relative(string full) => Path.GetRelativePath(RepoRoot(), full).Replace('\\', '/');

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根目录");
    }
}
