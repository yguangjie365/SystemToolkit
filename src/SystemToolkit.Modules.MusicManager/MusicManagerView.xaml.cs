using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
        ApplyImmersionFontFamily(); // 思源宋体 Black（对照 NexBox LYRIC_FONT），失败静默回退系统字体
        if (ImmersionRoot is not null)
        {
            ImmersionRoot.SizeChanged += OnImmersionRootSizeChanged; // 字号随窗口缩放（NexBox viewH*0.11）
        }
    }

    /// <summary>沉浸歌词字体（对照 NexBox "NotoSerifSC-900"）：模块内置思源宋体 Black，加载失败回退雅黑。</summary>
    private void ApplyImmersionFontFamily()
    {
        try
        {
            var serif = new System.Windows.Media.FontFamily(
                "pack://application:,,,/SystemToolkit.Modules.MusicManager;component/Assets/Fonts/NotoSerifSC-Black.otf#Noto Serif SC");
            ImmersionLyricText.FontFamily = serif;
            ImmersionGhostText.FontFamily = serif;
        }
        catch (Exception ex)
        {
            // 字体资源缺失只降级观感，不影响功能（测试宿主/精简发布场景）
            System.Diagnostics.Debug.WriteLine($"[Music] 沉浸歌词字体加载失败：{ex.Message}");
        }
    }

    private void OnImmersionRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateImmersionFontSizes();
    }

    /// <summary>沉浸字号（对照 NexBox）：前景 = max(视口高 11%, 字号设置×1.7) 钳 30–84；重影 = 前景 ×2.6。</summary>
    private void UpdateImmersionFontSizes()
    {
        if (ImmersionLyricText is null || ImmersionRoot is null)
        {
            return;
        }

        double fromView = ImmersionRoot.ActualHeight * 0.11;
        double fromSetting = _vm.LyricFontSize * 1.7;
        double fg = Math.Clamp(Math.Max(fromView, fromSetting), 30, 84);
        ImmersionLyricText.FontSize = fg;
        ImmersionGhostText.FontSize = fg * 2.6;
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
        SetKaraokeRenderHook(false); // P0：静态 CompositionTarget.Rendering 必须随卸载解绑，防视图泄漏
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


    // ════════ OM-5 在线三面板交互 ════════

    private void OnOnlineSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SearchOnlineCommand.CanExecute(null))
        {
            _vm.SearchOnlineCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // P3a：Esc 收起搜索历史浮层
        if (e.Key == Key.Escape && SearchHistoryPopup.IsOpen)
        {
            SearchHistoryPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    /// <summary>P3a：搜索框获焦且有历史时浮出下拉（空态 = 不弹，无需占位）。</summary>
    private void OnOnlineSearchGotFocus(object sender, RoutedEventArgs e)
    {
        SearchHistoryPopup.IsOpen = _vm.HasSearchHistory;
    }

    /// <summary>P3a：点历史项复搜后收起（命令已由 VM 执行）。</summary>
    private void OnSearchHistoryItemClick(object sender, RoutedEventArgs e)
    {
        SearchHistoryPopup.IsOpen = false;
    }

    /// <summary>列表选中事件取行 VM：SelectedItem 语义（SelectionChanged 用它正确）。</summary>
    private static T? RowOf<T>(object sender)
        where T : class
    {
        return sender is ListBox { SelectedItem: T selected }
            ? selected
            : (sender as ListBox)?.SelectedItem as T;
    }

    /// <summary>审查 O8：双击定位行必须沿可视树取 ListBoxItem.DataContext（勿依赖 SelectedItem——
    /// 双击未选中行时 SelectedItem 可能滞后；ItemsControlFromItemContainer 对容器内部元素返回 null）。</summary>
    private static T? RowFromClick<T>(object sender, MouseButtonEventArgs e)
        where T : class
    {
        if (e.OriginalSource is System.Windows.DependencyObject d
            && FindAncestor<System.Windows.Controls.ListBoxItem>(d)?.DataContext is T row)
        {
            return row;
        }

        return (sender as ListBox)?.SelectedItem as T; // 兜底
    }

    private void OnSearchListDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.PlayFromSearchCommand.Execute(RowFromClick<MusicManagerViewModel.OnlineResultRowVm>(sender, e));

    /// <summary>行内播放钮单击（反馈1：单击即播，与双击等效）。</summary>
    /// <summary>在线封面加载失败（审查 P4-24）：回退行 VM 的音符占位，防破图。</summary>
    private void OnCoverImageFailed(object sender, System.Windows.ExceptionRoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: { } dc })
        {
            if (dc is MusicManagerViewModel.OnlineResultRowVm resultRow)
            {
                resultRow.MarkCoverFailed();
            }
            else if (dc is MusicManagerViewModel.PlaylistRowVm playlistRow)
            {
                playlistRow.MarkCoverFailed();
            }
        }
    }

    private void OnSearchRowPlayClick(object sender, RoutedEventArgs e)
        => _vm.PlayFromSearchCommand.Execute(RowFromButton<MusicManagerViewModel.OnlineResultRowVm>(sender));

    /// <summary>沿可视树上溯找 ListBoxItem 取行数据（按钮不在 ListBox.SelectedItem 语义内）。</summary>
    private static T? RowFromButton<T>(object sender)
        where T : class
    {
        return sender is System.Windows.DependencyObject d
            && FindAncestor<System.Windows.Controls.ListBoxItem>(d)?.DataContext is T row
            ? row
            : null;
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject? from)
        where T : System.Windows.DependencyObject
    {
        while (from is not null)
        {
            if (from is T match)
            {
                return match;
            }

            from = System.Windows.Media.VisualTreeHelper.GetParent(from);
        }

        return null;
    }

    private void OnPlaylistListDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.OpenPlaylistCommand.Execute(RowFromClick<MusicManagerViewModel.PlaylistRowVm>(sender, e));

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
        => _vm.PlayFromPlaylistCommand.Execute(RowFromClick<MusicManagerViewModel.OnlineResultRowVm>(sender, e));

    private void OnPlaylistRowPlayClick(object sender, RoutedEventArgs e)
        => _vm.PlayFromPlaylistCommand.Execute(RowFromButton<MusicManagerViewModel.OnlineResultRowVm>(sender));

    private void OnDailyRecommendDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.PlayFromDailyRecommendCommand.Execute(RowFromClick<MusicManagerViewModel.OnlineResultRowVm>(sender, e));

    // OnRecommendedPlaylistDoubleClick 已随右卡「推荐歌单」分区移除（2026-09-09 实机反馈）

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
    /// 队列按钮按下（反馈2）：弹层开着时先关掉并吞掉事件——
    /// StaysOpen=False 会在鼠标按下阶段关闭弹层，若只靠 Click 切换会"关了又开"永远关不上。
    /// </summary>
    private void OnQueueButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (QueuePopup is { IsOpen: true })
        {
            QueuePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    /// <summary>队列按钮（主栏/播放器共用）：弹 Up Next 面板（锚定到实际按下的按钮）。</summary>
    private void OnQueueButtonClick(object sender, RoutedEventArgs e)
    {
        if (QueuePopup is not null && sender is System.Windows.UIElement target)
        {
            QueuePopup.PlacementTarget = target;
            // 反馈2：高度不超主窗口（底栏在窗口底部，上弹空间=窗口高-状态行余量）
            QueuePopup.MaxHeight = Math.Max(180, ActualHeight - 130);
            _vm.LogQueueSnapshot("打开队列弹窗");
            QueuePopup.IsOpen = true;
        }
    }

    /// <summary>队列面板内单击曲目：立即播放该曲并收起面板；清空选中以便再次点同一首。</summary>
    private void OnQueuePopupSelection(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox list && list.SelectedItem is MusicSong song)
        {
            _vm.PlayQueueItemCommand.Execute(song);
            QueuePopup.IsOpen = false;
            list.SelectedItem = null; // 清空选中：允许再次单击同一首重播
        }
    }

    /// <summary>音质按钮左键（主栏/播放器共用）：弹音质菜单（锚定到实际按下的按钮）。</summary>
    private void OnQualityButtonClick(object sender, RoutedEventArgs e)
    {
        if (QualityPopup is not null && sender is System.Windows.UIElement target)
        {
            QualityPopup.PlacementTarget = target;
            QualityPopup.IsOpen = true;
        }
    }

    /// <summary>音质按钮右键（反馈2/3：右键也要能切换）。</summary>
    private void OnQualityButtonRightClick(object sender, MouseButtonEventArgs e)
        => OnQualityButtonClick(sender, e);

    /// <summary>音质菜单点选：执行切换并收起（Popup 在独立可视树，命令走 VM、关闭走本地）。</summary>
    private void OnQualityOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string key })
        {
            _vm.SetQualityCommand.Execute(key);
        }

        if (QualityPopup is not null)
        {
            QualityPopup.IsOpen = false;
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
                System.Windows.Media.Animation.Storyboard.SetTarget(spin, DiscSpinHost);
                // 🔴 SetTarget 必须配对 SetTargetProperty——缺 TargetProperty 时
                // Begin 的 ClockTreeWalkRecursive 直接抛 InvalidOperationException
                //（2026-09-08 真机实证：必须为 DoubleAnimation 指定 TargetProperty）
                System.Windows.Media.Animation.Storyboard.SetTargetProperty(
                    spin,
                    new System.Windows.PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
                _discSpin.Begin(DiscSpinHost, true); // controllable
                _discSpinStarted = true;
            }
            else
            {
                _discSpin?.Resume(DiscSpinHost);
            }
        }
        else if (_discSpinStarted)
        {
            _discSpin?.Pause(DiscSpinHost);
        }

        UpdateVinylGlowBreath();
    }

    // ════════ 彩胶盘复刻配套（NexBox VinylDisc） ════════

    private System.Windows.Media.Animation.Storyboard? _glowBreath;

    /// <summary>光晕呼吸（5s：scale 1→1.06、opacity 0.85→1；暂停时停在原地——对照 NexBox）。</summary>
    private void UpdateVinylGlowBreath()
    {
        if (DiscGlow is null)
        {
            return;
        }

        if (_vm.IsPlaying && _glowBreath is null)
        {
            var breath = new System.Windows.Media.Animation.Storyboard();
            var scale = new System.Windows.Media.Animation.DoubleAnimation(1, 1.06, System.Windows.Duration.Automatic)
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                Duration = TimeSpan.FromSeconds(2.5), // 5s 全周期（往复）
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(scale, DiscGlow);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(scale, new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            breath.Children.Add(scale);
            System.Windows.Media.Animation.DoubleAnimation scaleY = scale.Clone();
            System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, DiscGlow);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY, new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            breath.Children.Add(scaleY);
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.85, 1, System.Windows.Duration.Automatic)
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                Duration = TimeSpan.FromSeconds(2.5),
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(fade, DiscGlow);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fade, new System.Windows.PropertyPath("Opacity"));
            breath.Children.Add(fade);
            _glowBreath = breath;
            breath.Begin(DiscGlow, true);
        }
        else if (!_vm.IsPlaying && _glowBreath is not null)
        {
            _glowBreath.Pause(DiscGlow);
        }
    }

    /// <summary>accent 变化：重建光晕径向刷（accent 0.30→0.13→透明）+ 同步彩胶歌词高亮刷。</summary>
    private void SyncVinylAccentVisuals()
    {
        if (_vm.VinylAccentBrush is not null && DiscGlow is not null)
        {
            DiscGlow.Fill = CoverColorFactory.VinylGlow(_vm.VinylAccentBrush);
        }

        if (TryFindResource("VinylAccentHighlight") is SolidColorBrush highlight && _vm.VinylAccentBrush is not null)
        {
            highlight.Color = _vm.VinylAccentBrush.Color; // 可变实例：彩胶歌词高亮跟随胶片色
        }
    }

    /// <summary>歌词高亮行变化 → 自动滚动到当前行（右栏跟随播放）。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicManagerViewModel.IsPlaying))
        {
            UpdateDiscSpin();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.VinylAccentBrush))
        {
            SyncVinylAccentVisuals();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.IsImmersionStyle))
        {
            UpdateRippleFieldActive();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.IsPlaying))
        {
            SetKaraokeRenderHook(_vm.IsPlaying); // P0：播放→挂 60fps 填充钩子；暂停/停→摘
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.LyricProgress)
            || e.PropertyName == nameof(MusicManagerViewModel.ActiveLyricIndex)
            || e.PropertyName == nameof(MusicManagerViewModel.PlayerAccentBrush))
        {
            UpdateKaraokeFill();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.IsVinylStyle) && _vm.IsVinylStyle)
        {
            PlayVinylDiscEntrance();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.CurrentLyricText))
        {
            UpdateImmersionLyric();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.LyricFontSize))
        {
            UpdateImmersionFontSizes(); // 沉浸前景字号跟随 A± 设置（下限钳制内）
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.LyricsVersion))
        {
            ResetLyricScrollToTop(); // 切歌/歌词重载：列表回顶（对照 NexBox scrollTo top:0 auto）
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.ActiveLyricIndex)
            && _vm.ActiveLyricIndex >= 0
            && _vm.ActiveLyricIndex < _vm.LyricRows.Count)
        {
            // OM-6：沉浸=中央单行大字（属性驱动无需滚动）；彩胶/现代各持列表——只滚可见的
            ListBox? activeList = _vm.IsModernStyle ? ModernLyricsList : FullLyricsList;
            if (activeList is { IsVisible: true })
            {
                SmoothScrollLyricToActive(activeList);
            }
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.IsDynamicBackground)
            || e.PropertyName == nameof(MusicManagerViewModel.IsModernStyle)
            || e.PropertyName == nameof(MusicManagerViewModel.ModernBackgroundBrush))
        {
            ApplyDynamicBackground();
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

    // ════════ NexBox 播放器三风格复刻（2026-09-09）：水波场 / 沉浸歌词重影 / 彩胶入场 ════════

    private readonly System.Collections.Generic.List<System.Windows.Shapes.Ellipse> _ripples = [];
    private System.Windows.Threading.DispatcherTimer? _rippleTimer;
    private readonly Random _rippleRandom = new();
    private int _rippleId;
    private bool _rippleFirstSpawn = true;

    /// <summary>沉浸态开/关水波发射器（挂载即启动；暂停后波纹依旧存在不消失——对照 NexBox）。</summary>
    private void UpdateRippleFieldActive()
    {
        if (RippleHost is null)
        {
            return;
        }

        if (_vm.IsImmersionStyle && _rippleTimer is null)
        {
            SizeChanged -= OnViewSizeChangedForRipples;
            SizeChanged += OnViewSizeChangedForRipples;
            _rippleFirstSpawn = true;
            SpawnRipplePair(); // 首波立即出现
            _rippleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            _rippleTimer.Tick += (_, _) => SpawnRipplePair();
            _rippleTimer.Start();
        }
        else if (!_vm.IsImmersionStyle && _rippleTimer is not null)
        {
            _rippleTimer.Stop();
            _rippleTimer = null;
            RippleHost.Children.Clear();
            _ripples.Clear();
        }
    }

    private void OnViewSizeChangedForRipples(object sender, SizeChangedEventArgs e)
    {
        // 窗口尺寸变化：按比例重排现存波纹圆心（贴左右边缘、垂直居中）
        foreach (System.Windows.Shapes.Ellipse ripple in _ripples)
        {
            double size = ripple.Width;
            bool isLeft = System.Windows.Controls.Canvas.GetLeft(ripple) < 0;
            System.Windows.Controls.Canvas.SetLeft(ripple, isLeft ? -size / 2 : ActualWidth - size / 2);
            System.Windows.Controls.Canvas.SetTop(ripple, ActualHeight / 2 - size / 2);
        }
    }

    /// <summary>生成一对波纹：正弦模拟节拍强度（首播拉满），左深右浅（背景色自身 HSL 派生）。</summary>
    private void SpawnRipplePair()
    {
        if (RippleHost is null || !_vm.IsImmersionStyle)
        {
            return;
        }

        double intensity = _rippleFirstSpawn
            ? 0.95
            : Math.Min(1, Math.Max(0.15, (0.5 + 0.5 * Math.Sin(DateTime.Now.Millisecond / 640.0)) * 0.72 + _rippleRandom.NextDouble() * 0.34));
        _rippleFirstSpawn = false;

        double maxDim = Math.Max(ActualWidth, ActualHeight);
        SpawnOneRipple(isLeft: true, intensity, maxDim);
        SpawnOneRipple(isLeft: false, intensity, maxDim);
    }

    private void SpawnOneRipple(bool isLeft, double intensity, double maxDim)
    {
        if (RippleHost is null || !_vm.IsImmersionStyle)
        {
            return;
        }

        // 波环取色：沉浸色板本色自身深/浅（左深右浅，保留色相非黑白）——工厂收敛
        Brush brush = CoverColorFactory.ImmersionRippleRing(_vm.ImmersionVividColor, isLeft);

        double size = Math.Max(200, maxDim * (0.62 + intensity * 0.3 + _rippleRandom.NextDouble() * 0.15));
        double duration = 3.6 + _rippleRandom.NextDouble() * 1.6;
        double delay = _rippleRandom.NextDouble() * 0.4;

        brush.Freeze();
        var ripple = new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = brush,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.3, 0.3),
            Opacity = 0,
        };
        System.Windows.Controls.Canvas.SetLeft(ripple, isLeft ? -size / 2 : ActualWidth - size / 2);
        System.Windows.Controls.Canvas.SetTop(ripple, ActualHeight / 2 - size / 2);
        RippleHost.Children.Add(ripple);
        _ripples.Add(ripple);

        // 波纹扩散动画（对照 rippleLeft/Right 关键帧：10%→1、60%→0.92、85%→0.38、100%→0）
        var storyboard = new System.Windows.Media.Animation.Storyboard
        {
            BeginTime = TimeSpan.FromSeconds(delay),
        };
        System.Windows.Media.Animation.DoubleAnimation scaleX = new(0.3, 1, TimeSpan.FromSeconds(duration));
        System.Windows.Media.Animation.DoubleAnimation scaleY = scaleX.Clone();
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleX, ripple);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleX, new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, ripple);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY, new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);

        var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(duration),
        };
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.Zero));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromSeconds(duration * 0.10)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.92, TimeSpan.FromSeconds(duration * 0.60)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.38, TimeSpan.FromSeconds(duration * 0.85)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromSeconds(duration)));
        System.Windows.Media.Animation.Storyboard.SetTarget(fade, ripple);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(fade, new System.Windows.PropertyPath("Opacity"));
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) =>
        {
            RippleHost?.Children.Remove(ripple);
            _ripples.Remove(ripple);
        };
        storyboard.Begin(ripple);
        _rippleId++;
    }

    /// <summary>彩胶盘入场（对照 vinylDiscIn）：translate(6%,-6%) scale0.94 → 原位，0.7s。</summary>
    /// <remarks>
    /// 🔴 不能在 Completed 里把 RenderTransform 归位 Identity（2026-09-09 实测事故）：
    /// XAML 里 DiscHost 的主变换是 TranslateTransform（右上伸出偏移 X+0.36/Y-0.30），
    /// Identity 会把它覆盖丢失 → 每次切风格回彩胶，入场动画结束碟片位置突变。
    /// 正确做法：入场动画只挂在「基础变换之后」的附加层，结束后还原基础变换。
    /// </remarks>
    private bool _vinylEntranceRunning;

    private void PlayVinylDiscEntrance()
    {
        if (DiscHost is null)
        {
            return;
        }

        // 审查 O17：动画进行中重入会二次改 RenderTransform → 丢 XAML 绑定。直接跳过
        if (_vinylEntranceRunning)
        {
            return;
        }

        _vinylEntranceRunning = true;

        // 基础变换 = XAML 定义的 TranslateTransform（绑定右上偏移）；缓存引用，动画后还原
        // 审查 O17：重入时 RenderTransform 可能已是上次未完成的 TransformGroup，取其首个子项找回绑定
        TranslateTransform baseTranslate = DiscHost.RenderTransform switch
        {
            TranslateTransform t => t,
            TransformGroup g when g.Children.Count > 0 && g.Children[0] is TranslateTransform t0 => t0,
            _ => new TranslateTransform(0, 0),
        };
        var group = new TransformGroup();
        group.Children.Add(baseTranslate);                 // [0] 基础：XAML 右上偏移（动画不动它）
        group.Children.Add(new TranslateTransform(6, -6)); // [1] 入场位移
        group.Children.Add(new ScaleTransform(0.94, 0.94));// [2] 入场缩放
        DiscHost.RenderTransform = group;
        DiscHost.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        var storyboard = new System.Windows.Media.Animation.Storyboard
        {
            Duration = TimeSpan.FromSeconds(0.7),
        };
        foreach (string prop in new[] { "X", "Y" })
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                prop == "X" ? 6 : -6, 0, new System.Windows.Duration(TimeSpan.FromSeconds(0.7)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(anim, DiscHost);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new System.Windows.PropertyPath("RenderTransform.Children[1]." + prop));
            storyboard.Children.Add(anim);
        }

        foreach (string prop in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                0.94, 1, new System.Windows.Duration(TimeSpan.FromSeconds(0.7)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(anim, DiscHost);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new System.Windows.PropertyPath("RenderTransform.Children[2]." + prop));
            storyboard.Children.Add(anim);
        }

        storyboard.Completed += (_, _) =>
        {
            // 归位为基础变换（保住 XAML 右上偏移；尺寸绑定实时变化不受影响）
            DiscHost.RenderTransform = baseTranslate;
            _vinylEntranceRunning = false; // 审查 O17：复位重入闸
        };
        storyboard.Begin(DiscHost);
    }

    /// <summary>
    /// 沉浸歌词更新（对照 NexBox）：双行拆分 + 背景重影（放大灰）+ 入场动画随机二选一
    /// （短词 rotate -6°→0 / 长词 scale 0.94→1），ENTER≈860ms。
    /// </summary>
    private void UpdateImmersionLyric()
    {
        if (ImmersionLyricText is null || ImmersionGhostText is null)
        {
            return;
        }

        string raw = _vm.CurrentLyricText;
        if (string.IsNullOrWhiteSpace(raw))
        {
            ImmersionLyricText.Text = "♪";
            ImmersionGhostText.Text = string.Empty;
            ImmersionOutgoingText.Opacity = 0; // 无词：清退场层
            return;
        }

        string display = PaletteMath.SplitLyricIntoTwoLines(raw);

        // P1 退场（重做）：旧句上移淡出，与新句空间+时间分离，避免同位重叠
        string prev = ImmersionLyricText.Text;
        if (!string.IsNullOrEmpty(prev) && prev != "♪" && prev != display)
        {
            ImmersionOutgoingText.Text = prev;
            ImmersionOutgoingText.FontSize = ImmersionLyricText.FontSize;
            ImmersionOutgoingShift.Y = 0;
            ImmersionOutgoingText.Opacity = 1;
            var outFade = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
                new System.Windows.Duration(TimeSpan.FromSeconds(0.3)));
            var outRise = new System.Windows.Media.Animation.DoubleAnimation(0, -30,
                new System.Windows.Duration(TimeSpan.FromSeconds(0.3)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(outFade, ImmersionOutgoingText);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(outFade, new System.Windows.PropertyPath("Opacity"));
            System.Windows.Media.Animation.Storyboard.SetTarget(outRise, ImmersionOutgoingText);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(outRise,
                new System.Windows.PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            var outBoard = new System.Windows.Media.Animation.Storyboard();
            outBoard.Children.Add(outFade);
            outBoard.Children.Add(outRise);
            outBoard.Begin(ImmersionOutgoingText, true);
        }

        ImmersionLyricText.Text = display;
        // 重影只取句首 2-4 字（对照 NexBox：contentLen*0.35 钳 2..4，去空白），放大置于前景正后方
        string compact = display.Replace(" ", string.Empty).Replace("\n", string.Empty);
        int ghostCount = Math.Clamp((int)Math.Round(compact.Length * 0.35), 2, 4);
        ImmersionGhostText.Text = compact.Length <= ghostCount ? compact : compact[..ghostCount];
        UpdateImmersionFontSizes();

        // 入场动画：随机 rotate / spread（P1：rotate 对齐 NexBox -2.6°）+ 淡入
        bool rotate = _rippleRandom.Next(2) == 0;
        var storyboard = new System.Windows.Media.Animation.Storyboard
        {
            Duration = TimeSpan.FromSeconds(0.86),
        };
        var enter = new System.Windows.Media.Animation.DoubleAnimation(
            rotate ? -2.6 : 0.94, rotate ? 0 : 1, new System.Windows.Duration(TimeSpan.FromSeconds(0.86)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        string path = rotate
            ? "(UIElement.RenderTransform).(RotateTransform.Angle)"
            : "(UIElement.RenderTransform).(ScaleTransform.ScaleX)";
        System.Windows.Media.Animation.Storyboard.SetTarget(enter, ImmersionLyricText);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(enter, new System.Windows.PropertyPath(path));
        storyboard.Children.Add(enter);
        // 新句淡入（250ms），与旧句上移淡出错峰 → 不重叠
        var enterFade = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
            new System.Windows.Duration(TimeSpan.FromSeconds(0.25)));
        System.Windows.Media.Animation.Storyboard.SetTarget(enterFade, ImmersionLyricText);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(enterFade, new System.Windows.PropertyPath("Opacity"));
        storyboard.Children.Add(enterFade);
        storyboard.Begin(ImmersionLyricText, true);
    }

    // ════════ 卡拉OK渐变填充（反馈4：当前行按行内进度左→右点亮；行级近似，无逐字时间轴） ════════

    private System.Windows.Controls.TextBlock? _karaokeVinylBlock;
    private System.Windows.Controls.TextBlock? _karaokeModernBlock;

    /// <summary>按 LyricProgress 刷新两处歌词列表当前行的渐变填充；进度归零/无行时还原。</summary>
    private void UpdateKaraokeFill()
    {
        ResetKaraokeRow(ref _karaokeVinylBlock);
        ResetKaraokeRow(ref _karaokeModernBlock);

        if (_vm.LyricProgress <= 0 || _vm.ActiveLyricIndex < 0)
        {
            return;
        }

        ApplyKaraokeToRow(FullLyricsList, ref _karaokeVinylBlock, _vm.LyricProgress);
        ApplyKaraokeToRow(ModernLyricsList, ref _karaokeModernBlock, _vm.LyricProgress);
    }

    // ── P0：逐字填充 60fps 渲染钩子（对照 NexBox RAF 直读 currentTime，替代 100ms 事件的"格子感"）──
    private bool _karaokeRenderHooked;
    private double _lastFillP = -1;

    /// <summary>幂等挂接/摘除 CompositionTarget.Rendering（静态事件，必须成对，防视图泄漏）。</summary>
    private void SetKaraokeRenderHook(bool on)
    {
        if (on == _karaokeRenderHooked)
        {
            return;
        }

        if (on)
        {
            EventHandler h = OnKaraokeRender;
            _karaokeRenderHandler = h;
            System.Windows.Media.CompositionTarget.Rendering += h;
        }
        else
        {
            if (_karaokeRenderHandler is not null)
            {
                System.Windows.Media.CompositionTarget.Rendering -= _karaokeRenderHandler;
                _karaokeRenderHandler = null;
            }
        }

        _karaokeRenderHooked = on;
    }

    private EventHandler? _karaokeRenderHandler;

    private void OnKaraokeRender(object? sender, EventArgs e)
    {
        if (!_vm.IsPlaying || _vm.ActiveLyricIndex < 0)
        {
            return;
        }

        double p = _vm.SampleLyricFillProgress();
        if (Math.Abs(p - _lastFillP) < 0.0015)
        {
            return; // 抑制无实质变化的帧（暂停/静止时不重绘）
        }

        _lastFillP = p;
        ApplyKaraokeToRow(FullLyricsList, ref _karaokeVinylBlock, p);
        ApplyKaraokeToRow(ModernLyricsList, ref _karaokeModernBlock, p);
    }

    private static void ResetKaraokeRow(ref System.Windows.Controls.TextBlock? block)
    {
        if (block is not null)
        {
            // 审查 O2（2026-09-10）：Foreground=null 是写入"值为 null 的本地值"，本地值压过
            // Style/Trigger → 该行永久失去静音色与高亮回退（"歌词空白块"疑似残余根因）。只有 ClearValue 才移除本地值
            block.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            block = null;
        }
    }

    private void ApplyKaraokeToRow(System.Windows.Controls.ListBox? list, ref System.Windows.Controls.TextBlock? tracked, double progress)
    {
        if (list is null
            || _vm.ActiveLyricIndex < 0
            || list.ItemContainerGenerator.ContainerFromIndex(_vm.ActiveLyricIndex) is not System.Windows.Controls.ListBoxItem container)
        {
            return; // 虚拟化未实现该行：跳过本帧（滚动到位后由下一次进度刷新接管）
        }

        if (FindFirstTextBlock(container) is not { } text)
        {
            return;
        }

        Brush? baseBrush = text.Foreground; // 本地值未设 → 样式静音色
        if (_vm.PlayerAccentBrush is not SolidColorBrush accent
            || baseBrush is not SolidColorBrush muted)
        {
            return;
        }

        double p = Math.Clamp(progress, 0, 1);
        var fill = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0.5),
            EndPoint = new System.Windows.Point(1, 0.5),
        };
        fill.GradientStops.Add(new GradientStop(accent.Color, 0.0));
        fill.GradientStops.Add(new GradientStop(accent.Color, p));
        fill.GradientStops.Add(new GradientStop(muted.Color, Math.Min(1.0, p + 0.001)));
        fill.GradientStops.Add(new GradientStop(muted.Color, 1.0));
        fill.Freeze();
        text.Foreground = fill;
        tracked = text;
    }

    private static System.Windows.Controls.TextBlock? FindFirstTextBlock(System.Windows.Media.Visual root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(root, i) is not System.Windows.Media.Visual child)
            {
                continue;
            }

            if (child is System.Windows.Controls.TextBlock tb)
            {
                return tb;
            }

            if (FindFirstTextBlock(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ════════ 歌词平滑滚动 + 现代动态背景（反馈3/5，对照 NexBox） ════════

    /// <summary>NexBox 式歌词跟随：当前行平滑滚动到视口垂直居中（350ms 缓动），seek 跳转同样生效。</summary>
    private void SmoothScrollLyricToActive(System.Windows.Controls.ListBox list)
    {
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(_vm.ActiveLyricIndex) is not System.Windows.Controls.ListBoxItem item)
        {
            list.ScrollIntoView(_vm.LyricRows[_vm.ActiveLyricIndex]); // 虚拟化未实现：先粗滚到位，下一拍精调
            return;
        }

        System.Windows.Controls.ScrollViewer? sv = FindAncestor<System.Windows.Controls.ScrollViewer>(item)
            ?? FindFirstVisualChild<System.Windows.Controls.ScrollViewer>(list);
        if (sv is null)
        {
            list.ScrollIntoView(item);
            return;
        }

        double itemTop = item.TranslatePoint(new System.Windows.Point(0, 0), list).Y;
        double target = Math.Clamp(
            sv.VerticalOffset + itemTop - (sv.ViewportHeight / 2) + (item.ActualHeight / 2),
            0,
            Math.Max(0, sv.ScrollableHeight));

        // 🔴 不能用 Storyboard 动画 ScrollViewer.VerticalOffset（2026-09-09 实测事故）：
        // VerticalOffset 是只读依赖属性，Storyboard.Begin 即抛"路径包含非动画属性"，
        // 异常顶掉状态行且滚动跟随整体失效（seek 后歌词错位/列表空白）。
        // 改为 350ms 手动帧插值（cubic ease-out），与 NexBox 行为一致。
        // 手动滚动冲突处理（对照 NexBox 第 4 环节）：用户刚滚过滚轮时自动跟随暂不抢位
        if (DateTime.UtcNow - _lastManualScrollUtc < ManualScrollHold)
        {
            return;
        }

        StartSmoothScroll(sv, target);
    }

    private System.Windows.Threading.DispatcherTimer? _scrollTimer;
    private System.Windows.Controls.ScrollViewer? _scrollAnimTarget;
    private double _scrollAnimFrom;
    private double _scrollAnimTo;
    private DateTime _scrollAnimStart;

    /// <summary>用户手动滚动后的自动跟随保持窗口（窗口内自动滚动不抢位，NexBox 同语义）。</summary>
    private static readonly TimeSpan ManualScrollHold = TimeSpan.FromSeconds(4);

    private DateTime _lastManualScrollUtc = DateTime.MinValue;

    /// <summary>
    /// 歌词区滚轮接管（对照 NexBox 手动滚动冲突处理）：停掉自动跟随动画让滚轮生效，
    /// 并记录时间戳——<see cref="ManualScrollHold"/> 窗口内自动滚动不抢位，之后自动恢复跟随。
    /// 不标记 Handled：默认滚轮滚动原样生效。
    /// </summary>
    private void OnLyricPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        _scrollTimer?.Stop();
        _scrollAnimTarget = null;
        _lastManualScrollUtc = DateTime.UtcNow;
    }

    /// <summary>滚动动画单帧时长（与原 350ms 缓动一致）。</summary>
    private static readonly TimeSpan ScrollAnimDuration = TimeSpan.FromMilliseconds(350);

    private void StartSmoothScroll(System.Windows.Controls.ScrollViewer sv, double target)
    {
        if (Math.Abs(target - sv.VerticalOffset) < 0.5)
        {
            return;
        }

        _scrollAnimTarget = sv;
        _scrollAnimFrom = sv.VerticalOffset;
        _scrollAnimTo = target;
        _scrollAnimStart = DateTime.UtcNow;
        if (_scrollTimer is null)
        {
            _scrollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _scrollTimer.Tick += OnSmoothScrollTick;
        }

        _scrollTimer.Start();
    }

    /// <summary>歌词重载后：取消进行中的滚动动画并把两个歌词列表瞬时归零（对照 NexBox scrollTo top:0 auto）。</summary>
    private void ResetLyricScrollToTop()
    {
        _scrollTimer?.Stop();
        _scrollAnimTarget = null;
        foreach (System.Windows.Controls.ListBox? list in new[] { FullLyricsList, ModernLyricsList })
        {
            if (list is null)
            {
                continue;
            }

            System.Windows.Controls.ScrollViewer? sv = FindFirstVisualChild<System.Windows.Controls.ScrollViewer>(list);
            sv?.ScrollToVerticalOffset(0);
        }
    }

    private void OnSmoothScrollTick(object? sender, EventArgs e)
    {
        if (_scrollAnimTarget is null)
        {
            _scrollTimer?.Stop();
            return;
        }

        double t = (DateTime.UtcNow - _scrollAnimStart) / ScrollAnimDuration;
        if (t >= 1.0)
        {
            _scrollAnimTarget.ScrollToVerticalOffset(_scrollAnimTo);
            _scrollTimer?.Stop();
            return;
        }

        double eased = 1 - Math.Pow(1 - t, 3); // cubic ease-out
        _scrollAnimTarget.ScrollToVerticalOffset(_scrollAnimFrom + (_scrollAnimTo - _scrollAnimFrom) * eased);
    }

    private static T? FindFirstVisualChild<T>(System.Windows.DependencyObject from)
        where T : System.Windows.Media.Visual
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(from);
        for (int i = 0; i < count; i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(from, i) is not System.Windows.Media.Visual child)
            {
                continue;
            }

            if (child is T match)
            {
                return match;
            }

            if (FindFirstVisualChild<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 现代模板动态流动背景（对照 NexBox"动态"开关）：渐变随时间缓旋（16s/圈，往复）。
    /// 关闭/离开现代态时还原为 VM 渐变刷。
    /// </summary>
    private void ApplyDynamicBackground()
    {
        if (ModernBgHost is null)
        {
            return;
        }

        StopDynamicBackground();

        if (!_vm.IsModernStyle || !_vm.IsDynamicBackground
            || _vm.ModernBackgroundBrush is not System.Windows.Media.LinearGradientBrush source)
        {
            return;
        }

        System.Windows.Media.LinearGradientBrush brush = source.Clone();
        // 🔴 RotateTransform 是 Freezable 不是 FrameworkElement——不能作 Storyboard 可控目标，
        // 直接 BeginAnimation（保留引用以便停止）
        _dynamicBgTransform = new System.Windows.Media.RotateTransform(0, 0.5, 0.5);
        brush.RelativeTransform = _dynamicBgTransform;
        ModernBgHost.Background = brush;

        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 360, new System.Windows.Duration(TimeSpan.FromSeconds(16)))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        _dynamicBgTransform.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, anim);
    }

    private System.Windows.Media.RotateTransform? _dynamicBgTransform;

    private void StopDynamicBackground()
    {
        _dynamicBgTransform?.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        _dynamicBgTransform = null;
        if (ModernBgHost is not null)
        {
            ModernBgHost.SetBinding(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.Binding
            {
                Path = new System.Windows.PropertyPath(nameof(MusicManagerViewModel.ModernBackgroundBrush)),
                Mode = System.Windows.Data.BindingMode.OneWay,
            });
        }
    }
}
