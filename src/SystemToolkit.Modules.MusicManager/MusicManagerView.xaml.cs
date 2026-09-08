using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理视图（2026-09-08 OM-5 三面板布局）：左歌单面板 / 中内容区三态 / 右推荐+队列 /
/// 底部播放条 / 完整播放器覆盖层。
/// Loaded 注入文件夹选择与登录窗回调并初始化曲库（幂等）。
/// </summary>
public partial class MusicManagerView : UserControl
{
    private readonly MusicManagerViewModel _vm;

    public MusicManagerView(MusicManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 文件夹选择回调注入（对照 FileBackup.RestoreRequest 注入模式——VM 不持有窗口引用）
        _vm.PickFolder ??= () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择音乐扫描目录",
                Multiselect = false,
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
        };

        // 登录窗回调注入（OM-5）：VM 只发请求，窗口由 View 打开，Cookie 回传 VM 加密落盘
        _vm.LoginRequested ??= OnLoginRequested;

        // 订阅平衡：Loaded 订阅 / Unloaded 退订（View/VM 均为 DI 单例，Shell 切换导航会卸载重挂同一实例；
        // 不退订则隐藏中的旧实例继续消费 VM 事件）。「-= 先行」保证 Loaded 重复触发也只有一个订阅
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateDiscSpin(); // 重挂后同步黑胶状态（Unloaded 时已暂停；若正在播放需恢复旋转）

        _ = InitializeOnceAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _discSpin?.Pause(DiscHost); // 切走页面：黑胶暂停，避免不可见空转（审查 🟠-1 采纳——修正其论据后落地）
    }

    /// <summary>
    /// 曲库双击 → 播放该曲。
    /// ⚠️ 不要用 ItemsControl.ItemsControlFromItemContainer(OriginalSource) 判定——
    /// 它对容器内部元素（模板中的 TextBlock 等）返回 null，条件永远为 false（2026-09-08 实测回归）。
    /// 选中状态由单击的 SelectedItem 绑定维护；边界（点在行上但未选中）从行 DataContext 兜底。
    /// </summary>
    private void OnSongListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedSong is null
            && e.OriginalSource is System.Windows.FrameworkElement { DataContext: MusicSong hit })
        {
            _vm.SelectedSong = hit;
        }

        if (_vm.SelectedSong is not null)
        {
            _vm.PlayFromLibraryCommand.Execute(null);
        }
    }

    /// <summary>底部播放条空白区点击 → 展开完整播放器（按钮区域自行处理点击，不冒泡到这里）。</summary>
    private void OnBottomBarTap(object sender, MouseButtonEventArgs e)
        => _vm.OpenFullPlayerCommand.Execute(null);

    // ════════ OM-5 在线三面板交互 ════════

    private void OnOnlineSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SearchOnlineCommand.CanExecute(null))
        {
            _vm.SearchOnlineCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>列表双击取行 VM：SelectedItem 优先，行 DataContext 兜底（同曲库双击的实测教训）。</summary>
    private static T? RowOf<T>(object sender)
        where T : class
    {
        return sender is ListBox { SelectedItem: T selected }
            ? selected
            : (sender as ListBox)?.SelectedItem as T;
    }

    private void OnSearchListDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.PlayFromSearchCommand.Execute(RowOf<MusicManagerViewModel.OnlineResultRowVm>(sender));

    private void OnPlaylistListDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.OpenPlaylistCommand.Execute(RowOf<MusicManagerViewModel.PlaylistRowVm>(sender));

    /// <summary>我的歌单胶囊单击即打开（2026-09-09 横向胶囊化；选中态不保持，防重复触发看这条判断）。</summary>
    private void OnPlaylistListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (RowOf<MusicManagerViewModel.PlaylistRowVm>(sender) is not { } row)
        {
            return;
        }

        _vm.OpenPlaylistCommand.Execute(row);
        if (sender is ListBox listBox)
        {
            listBox.SelectedItem = null; // 胶囊不保持选中态（再次单击同一歌单仍可刷新打开）
        }
    }

    private void OnPlaylistTrackDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.PlayFromPlaylistCommand.Execute(RowOf<MusicManagerViewModel.OnlineResultRowVm>(sender));

    private void OnDailyRecommendDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.PlayFromDailyRecommendCommand.Execute(RowOf<MusicManagerViewModel.OnlineResultRowVm>(sender));

    private void OnRecommendedPlaylistDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.OpenPlaylistCommand.Execute(RowOf<MusicManagerViewModel.PlaylistRowVm>(sender));

    /// <summary>打开平台登录窗并回传 Cookie（async void + 全捕获——事件处理器模式）。</summary>
    private async void OnLoginRequested(OnlineProvider provider)
    {
        try
        {
            string? cookie = await OnlineLoginWindow.ShowAsync(Window.GetWindow(this), provider);
            await _vm.OnLoginCookieObtainedAsync(provider, cookie);
        }
        catch (Exception ex)
        {
            // 登录窗异常降级为可见状态（🔴 不静默），不打崩进程
            await _vm.OnLoginCookieObtainedAsync(provider, null);
            System.Diagnostics.Debug.WriteLine($"[Music] 登录窗异常：{ex.Message}");
        }
    }

    /// <summary>完整播放器进度条拖动结束。</summary>
    private void OnFullSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => _vm.EndSeek(FullSeekSlider.Value);

    /// <summary>
    /// 音质按钮左键弹出菜单（2026-09-09 修复"点击无反应"）：
    /// ContextMenu 默认只响应右键，左键需代码显式打开；Placement 锚定按钮底部。
    /// </summary>
    private void OnQualityMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>进度条拖动：开始（暂停位置回写）/ 结束（按百分比跳转）。</summary>
    private void OnSeekDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _vm.BeginSeek();

    private void OnSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => _vm.EndSeek(SeekSlider.Value);

    private System.Windows.Media.Animation.Storyboard? _discSpin;
    private bool _discSpinStarted;

    /// <summary>黑胶旋转：播放 → 旋转（首次 Begin，暂停后 Resume），非播放 → Pause 保持角度。</summary>
    private void UpdateDiscSpin()
    {
        if (_vm.IsPlaying)
        {
            if (!_discSpinStarted)
            {
                var spin = new System.Windows.Media.Animation.DoubleAnimation(
                    0, 360, TimeSpan.FromSeconds(24))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                };
                _discSpin = new System.Windows.Media.Animation.Storyboard();
                _discSpin.Children.Add(spin);
                System.Windows.Media.Animation.Storyboard.SetTarget(spin, DiscHost);
                // 🔴 SetTarget 必须配对 SetTargetProperty——缺 TargetProperty 时
                // Begin 的 ClockTreeWalkRecursive 直接抛 InvalidOperationException
                //（2026-09-08 真机实证：必须为 DoubleAnimation 指定 TargetProperty）
                System.Windows.Media.Animation.Storyboard.SetTargetProperty(
                    spin,
                    new System.Windows.PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
                _discSpin.Begin(DiscHost, true); // controllable
                _discSpinStarted = true;
            }
            else
            {
                _discSpin?.Resume(DiscHost);
            }
        }
        else if (_discSpinStarted)
        {
            _discSpin?.Pause(DiscHost);
        }
    }

    /// <summary>歌词高亮行变化 → 自动滚动到当前行（右栏跟随播放）。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicManagerViewModel.IsPlaying))
        {
            UpdateDiscSpin();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.ActiveLyricIndex)
            && _vm.ActiveLyricIndex >= 0
            && _vm.ActiveLyricIndex < _vm.LyricRows.Count)
        {
            // OM-6：沉浸=中央单行大字（属性驱动无需滚动）；彩胶/现代各持列表——只滚可见的
            MusicManagerViewModel.LyricRowVm row = _vm.LyricRows[_vm.ActiveLyricIndex];
            ListBox? activeList = _vm.IsModernStyle ? ModernLyricsList : FullLyricsList;
            if (activeList is { IsVisible: true })
            {
                activeList.ScrollIntoView(row);
            }
        }
    }

    private bool _initialized;

    private async Task InitializeOnceAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            // 🔴 初始化失败显式可见（曲库文件 IO 异常等），不让 Dispatcher 吞掉
            _vm.ReportInitError($"曲库初始化失败：{ex.Message}");
        }
    }
}
