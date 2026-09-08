using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理视图（2026-09-08 用户拍板单屏布局）：左曲库 / 右歌词 / 底部播放条 / 完整播放器覆盖层。
/// Loaded 注入文件夹选择回调并初始化曲库（幂等）。
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

        _ = InitializeOnceAsync();
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

    /// <summary>完整播放器进度条拖动结束。</summary>
    private void OnFullSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => _vm.EndSeek(FullSeekSlider.Value);

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
            MusicManagerViewModel.LyricRowVm row = _vm.LyricRows[_vm.ActiveLyricIndex];
            if (LyricsList.IsVisible)
            {
                LyricsList.ScrollIntoView(row);
            }

            if (FullLyricsList.IsVisible)
            {
                FullLyricsList.ScrollIntoView(row);
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
        _vm.PropertyChanged += OnViewModelPropertyChanged;
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
