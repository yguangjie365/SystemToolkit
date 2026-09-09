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

    /// <summary>队列按钮（图3 对齐）：弹出 Up Next 面板（Up Next 已从右卡移除）。</summary>
    private void OnQueueButtonClick(object sender, RoutedEventArgs e)
    {
        if (QueuePopup is not null)
        {
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

        if (e.PropertyName == nameof(MusicManagerViewModel.IsVinylStyle) && _vm.IsVinylStyle)
        {
            PlayVinylDiscEntrance();
        }

        if (e.PropertyName == nameof(MusicManagerViewModel.CurrentLyricText))
        {
            UpdateImmersionLyric();
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
    private void PlayVinylDiscEntrance()
    {
        if (DiscHost is null)
        {
            return;
        }

        var group = new TransformGroup();
        group.Children.Add(new TranslateTransform(6, -6));
        group.Children.Add(new ScaleTransform(0.94, 0.94));
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
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new System.Windows.PropertyPath($"RenderTransform.Children[0].{prop}"));
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
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new System.Windows.PropertyPath($"RenderTransform.Children[1].{prop}"));
            storyboard.Children.Add(anim);
        }

        storyboard.Completed += (_, _) =>
        {
            // 归位为主变换（尺寸绑定实时变化，入场用完即弃）
            DiscHost.RenderTransform = Transform.Identity;
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
            return;
        }

        string display = PaletteMath.SplitLyricIntoTwoLines(raw);
        ImmersionLyricText.Text = display;
        ImmersionGhostText.Text = display;

        // 入场动画：随机 rotate / spread
        bool rotate = _rippleRandom.Next(2) == 0;
        var storyboard = new System.Windows.Media.Animation.Storyboard
        {
            Duration = TimeSpan.FromSeconds(0.86),
        };
        var enter = new System.Windows.Media.Animation.DoubleAnimation(
            rotate ? -6 : 0.94, rotate ? 0 : 1, new System.Windows.Duration(TimeSpan.FromSeconds(0.86)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        string path = rotate
            ? "(UIElement.RenderTransform).(RotateTransform.Angle)"
            : "(UIElement.RenderTransform).(ScaleTransform.ScaleX)";
        System.Windows.Media.Animation.Storyboard.SetTarget(enter, ImmersionLyricText);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(enter, new System.Windows.PropertyPath(path));
        storyboard.Children.Add(enter);
        storyboard.Begin(ImmersionLyricText, true);
    }
}
