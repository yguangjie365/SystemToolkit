using System.Windows;
using System.Windows.Input;
using SystemToolkit.Abstractions;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 独立迷你播放窗（2026-09-12 v5 用户裁定，对照 QQMusic 迷你模式）：
/// 置顶无边框小窗、主窗口**外**显示；**不设 Owner**——主程序最小化时本窗独立留在桌面（Topmost）；
/// 左键拖动、× 关闭；「≡」恢复主程序窗口。数据源 = <see cref="IPlaybackBarSource"/>
/// （<see cref="MusicManagerViewModel"/> 隐式实现，构造注入）。
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private static MiniPlayerWindow? _current;
    private readonly Window _anchor;

    // internal + InternalsVisibleTo：窗口加载冒烟直构（ViewLoadSmokeGuardTests）
    internal MiniPlayerWindow(IPlaybackBarSource source, Window anchor)
    {
        InitializeComponent();
        DataContext = source;
        _anchor = anchor;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
    }

    /// <summary>左键按住空白处拖动小窗（DragMove 在鼠标释放瞬间有文档化竞态，吞掉即可）。</summary>
    private void OnWindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标释放瞬间的竞态（DragMove 文档化限制）：本次拖动放弃即可
            }
        }
    }

    /// <summary>开关：已开则关闭，未开则建窗（贴主人窗口右上角，且不出屏）。**不设 Owner**，
    /// 避免主窗口最小化联动带走迷你窗（v5 用户要求：主程序最小化时迷你窗留在桌面）。</summary>
    public static void Toggle(IPlaybackBarSource source, Window anchor)
    {
        if (_current is { IsLoaded: true })
        {
            _current.Close();
            return;
        }

        _current = new MiniPlayerWindow(source, anchor)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        _current.Show();
    }

    /// <summary>SizeToContent 完成后才有真实 Width——渲染完一次性贴到主人窗口右上角（不出屏）。</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!double.IsNaN(Width))
        {
            Left = Math.Max(_anchor.Left + 8, _anchor.Left + _anchor.ActualWidth - Width - 48);
            Top = _anchor.Top + 56;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>「≡」：恢复（可能最小化中的）主程序窗口并关闭迷你窗。</summary>
    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        _anchor.WindowState = WindowState.Normal;
        _anchor.Show();
        _anchor.Activate();
        Close();
    }
}
