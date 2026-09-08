using System.Windows;
using System.Windows.Controls;
using SystemToolkit.Abstractions;

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
        ["appmanager"] = "Icon_Nav_AppManager",
        ["drivermanager"] = "Icon_Nav_DriverManager",
        ["filebackup"] = "Icon_Nav_FileBackup",
        ["filetransfer"] = "Icon_Nav_FileTransfer",
        ["netmanager"] = "Icon_Nav_NetManager",
        ["recoverymanager"] = "Icon_Nav_RecoveryManager",
        ["gamemanager"] = "Icon_Nav_GameManager",
        ["musicmanager"] = "Icon_Nav_MusicManager",
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

    /// <summary>导航分组（模块 id → 组名，按展示顺序）。UI 审计采纳：体现产品架构层级。</summary>
    private static readonly (string Title, string[] ModuleIds)[] NavGroups =
    [
        ("概览", ["overview"]),
        ("系统", ["appmanager", "drivermanager", "netmanager"]),
        ("数据", ["filetransfer", "filebackup"]),
        ("恢复", ["recoverymanager"]),
        ("娱乐", ["gamemanager", "musicmanager"]),
        ("设置", ["settings"]),
    ];

    public MainWindow(IEnumerable<IModule> modules, IServiceProvider provider)
    {
        InitializeComponent();
        _provider = provider;

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

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
        FontSize = 18,
        Foreground = (System.Windows.Media.Brush)FindResource("Brush_TextMuted"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
