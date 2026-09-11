using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Shell;

/// <summary>导航条目基类（区分组头与模块项，UI 审计采纳：导航分组）。</summary>
public abstract record NavEntry(bool IsHeader);

/// <summary>
/// 导航条目：模块 + 展示图标。
/// 2026-09-09：图标改为**矢量优先**（<see cref="Geometry"/>），字形仅作回退——
/// 矢量不依赖系统字体（Segoe Fluent/MDL2 在精简系统可能缺字形变豆腐块）。
/// 两者都取不到时 <see cref="IconData"/> 为 null，模板退化为显示字形。
/// </summary>
public sealed record NavItem(string Glyph, IModule Module, System.Windows.Media.Geometry? IconData = null) : NavEntry(false)
{
    public string DisplayName => Module.DisplayName;
}

/// <summary>导航组头（不可选中，仅视觉分组）。</summary>
public sealed record NavGroupHeader(string Title) : NavEntry(true);

/// <summary>
/// 宿主主窗口：导航 + 页面宿主 + 概览页刷新生命周期接线。
/// 🔴 概览页刷新纪律（Design/01 §3.1）：窗口失焦立即暂停，重新激活且停留在概览页时恢复。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _provider;

    /// <summary>
    /// 当前页面的可暂停视图模型（概览页红线：窗口失焦即停采样）。
    /// 经 <see cref="IPausableViewModel"/> 抽象持有，宿主不认识具体模块类型。
    /// </summary>
    private IPausableViewModel? _pausable;

    /// <summary>
    /// 模块矢量图标映射（Icons.xaml 中的资源 key；新增模块须登记）。
    /// 找不到对应 Geometry 时回退到 <see cref="ModuleGlyphs"/> 的字形。
    /// </summary>
    private static readonly Dictionary<string, string> ModuleIconKeys = new()
    {
        ["overview"] = "Icon_Nav_Overview",
        ["appmanager"] = "Icon_Nav_Appmanager",
        ["drivermanager"] = "Icon_Nav_Drivermanager",
        ["filebackup"] = "Icon_Nav_Filebackup",
        ["filetransfer"] = "Icon_Nav_Filetransfer",
        ["netmanager"] = "Icon_Nav_Netmanager",
        ["recoverymanager"] = "Icon_Nav_Recoverymanager",
        ["gamemanager"] = "Icon_Nav_Gamemanager",
        ["musicmanager"] = "Icon_Nav_Musicmanager",
        ["settings"] = "Icon_Nav_Settings",
    };

    /// <summary>模块字形映射（Segoe MDL2 码位；矢量图标缺失时的回退，新增模块须登记）。</summary>
    private static readonly Dictionary<string, string> ModuleGlyphs = new()
    {
        ["overview"] = "\uE9D9",
        ["appmanager"] = "\uE71D",
        ["drivermanager"] = "\uE772",
        ["filebackup"] = "\uE8E5",
        ["filetransfer"] = "\uE8E6",
        ["netmanager"] = "\uE774",
        ["recoverymanager"] = "\uE72C",
        ["gamemanager"] = "\uE7FC",
        ["musicmanager"] = "\uEC4F",
        ["settings"] = "\uE713",
    };

    /// <summary>
    /// 导航分组（模块 id → 组名，按展示顺序）。UI 审计采纳：体现产品架构层级。
    /// <para>
    /// 🔴 <b>本表是导航顺序的唯一真源</b>（组顺序 = 数组顺序，组内顺序 = <c>ModuleIds</c> 顺序）。
    /// <see cref="IModule.Order"/> <b>不</b>驱动导航——它只被 <c>ModuleContractTests</c> 用来断言唯一性。
    /// 2026-09-10 事故：按直觉改了 <c>IModule.Order</c>（音乐 9→8 / 游戏 8→9）但界面顺序不变，
    /// 因为宿主从未读取过该属性。要调顺序，改这里。
    /// </para>
    /// </summary>
    private static readonly (string Title, string[] ModuleIds)[] NavGroups =
    [
        ("概览", ["overview"]),
        ("系统", ["appmanager", "drivermanager", "netmanager"]),
        ("数据", ["filetransfer", "filebackup"]),
        ("恢复", ["recoverymanager"]),
        ("娱乐", ["musicmanager", "gamemanager"]),
        ("设置", ["settings"]),
    ];

    public MainWindow(IEnumerable<IModule> modules, IServiceProvider provider)
    {
        InitializeComponent();
        _provider = provider;

        // 审查 Y1（2026-09-10）：版本由程序集驱动（Directory.Build.props <Version>），不再硬编码 v0.2 漂移
        Version? asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (asmVer is not null)
        {
            ToolkitVersionText.Text = $"DESKTOP TOOLKIT · v{asmVer.Major}.{asmVer.Minor}";
        }

        // 审查 2026-09-04（P2）：重复模块 Id 不再抛异常炸启动（后来者忽略，取首个）
        var byId = modules.GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var entries = new List<NavEntry>();
        int firstItemIndex = -1;
        foreach ((string title, string[] ids) in NavGroups)
        {
            entries.Add(new NavGroupHeader(title));
            foreach (string id in ids)
            {
                if (!byId.TryGetValue(id, out IModule? module))
                {
                    continue;
                }

                if (firstItemIndex < 0)
                {
                    firstItemIndex = entries.Count;
                }

                entries.Add(new NavItem(ModuleGlyphs.GetValueOrDefault(id, "\uE7C3"), module, ResolveIcon(id)));
            }
        }

        NavList.ItemsSource = entries;
        NavList.SelectedIndex = firstItemIndex;

        Deactivated += (_, _) => _pausable?.Pause();
        Activated += (_, _) =>
        {
            // 概览页红线（Design/01 §3.1）：重新激活且停留在可暂停页面时恢复采样。
            // 经 IPausableViewModel 抽象，宿主无需知道"这是哪一个模块"。
            if (_pausable is not null)
            {
                _ = _pausable.ActivateAsync();
            }
        };

        // 2026-09-10：主题切换后重建当前视图，做到真正"即时切换"。
        // 令牌引用已全部 DynamicResource（自动刷新），但 BasedOn 不支持 DynamicResource
        // （WPF 硬限制，实测抛 XamlParseException）——这些派生样式需重建视图才会按新包解析。
        // 视图由模块 CreateView 产生、VM 为 DI 单例 → 重建只重置 UI 局部状态，业务状态保留。
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

        // 2026-09-12（v4 用户反馈）：内嵌迷你播放条整体移除——它悬浮时遮挡页面顶栏，
        // 且在所有页面常显。迷你控制改由音乐模块的**独立迷你窗**承载（主窗口外、置顶，
        // 从全屏播放器底栏右区的「迷你窗」按钮开关）。

        // 2026-09-12（v5 用户裁定）：播放中点 × 不退出程序——最小化到任务栏，音乐不中断；
        // 恢复入口 = 任务栏图标 + 迷你窗「≡」。未播放时正常关闭（真正退出）。
        _playbackSource = provider.GetService<IPlaybackBarSource>();
        Closing += (_, args) =>
        {
            if (_playbackSource?.IsPlaying == true)
            {
                args.Cancel = true;
                WindowState = WindowState.Minimized;
            }
        };
    }

    private readonly IPlaybackBarSource? _playbackSource;
    /// <summary>主题切换 → 重新装载当前模块视图（继承主题包样式的控件随之刷新）。</summary>
    private void OnThemeChanged()
    {
        if (!IsLoaded)
        {
            return; // 启动期应用主题（App 在窗口之前调用）无需重建
        }

        LoadSelectedModule();
    }

    /// <summary>
    /// 按模块 id 取矢量图标（来自 Icons.xaml 合并进 App 资源字典的 Geometry）。
    /// 取不到返回 null → 模板自动回退到字形，不会变成空白。
    /// </summary>
    private static System.Windows.Media.Geometry? ResolveIcon(string moduleId)
    {
        if (!ModuleIconKeys.TryGetValue(moduleId, out string? key))
        {
            return null;
        }

        return Application.Current?.TryFindResource(key) as System.Windows.Media.Geometry;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelectedModule();

    /// <summary>
    /// 为当前选中的导航项装载视图（导航切换与主题切换后重建共用）。
    /// </summary>
    private void LoadSelectedModule()
    {
        if (NavList.SelectedItem is not NavItem nav)
        {
            return;
        }

        // 离开当前页前先停掉它的刷新（概览页红线）
        _pausable?.Pause();
        _pausable = null;

        // 🔴 F-1（审查 2026-09-06）：视图由模块自己创建，宿主不再按 Id 硬编码分派。
        //   旧实现是一串按模块 Id 字符串逐个比对的分支，新增/改名模块必须同步改这里，
        //   且漏改会静默退化成"建设中"占位页——三套架构守卫全都拦不住。
        object? view = nav.Module.CreateView(_provider);
        if (view is null)
        {
            PageHost.Content = BuildUnderConstruction(nav.Module.DisplayName);
            return;
        }

        PageHost.Content = view; // 视图 Loaded 事件自行完成数据加载（各模块内部接线）
        // 审查 2026-09-04（P2）：不再手动 ActivateAsync——视图重挂 ContentControl 会触发
        // Loaded → ActivateAsync，此处再调一次属双通道重复激活（靠幂等兜底，脆弱）
        _pausable = (view as FrameworkElement)?.DataContext as IPausableViewModel;
    }

    /// <summary>未落地模块的占位页（模块 CreateView 返回 null 时）。</summary>
    private TextBlock BuildUnderConstruction(string displayName) => new()
    {
        Text = displayName + " 建设中（V0.x 里程碑交付）",
        // 🟡 审查 2026-09-10（🟡-14）：走设计令牌，不再硬编码字号
        FontSize = (double)FindResource("Font_SizeBodyLg"),
        Foreground = (System.Windows.Media.Brush)FindResource("Brush_TextMuted"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
