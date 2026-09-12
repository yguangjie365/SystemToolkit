using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Modules.GameManager;

/// <summary>游戏管理页交互层：进入页面自动加载 Steam 库（Loaded 触发一次，无定时器）。</summary>
public partial class GameManagerView : UserControl
{
    private readonly GameManagerViewModel _vm;

    public GameManagerView(GameManagerViewModel vm, ILogger? logger = null)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.ConfirmRequest = (title, message) =>
            MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK;
        Loaded += OnViewLoaded;
        // A2 详情面板：Esc 关闭。挂 Preview 阶段（详见处理器注释）
        PreviewKeyDown += OnDetailPanelPreviewKeyDown;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm.IsLoading)
        {
            return; // 加载中重复触发（页面重入）直接跳过
        }

        _vm.LoadCommand.Execute(null);
    }

    /// <summary>
    /// 账户区按钮 → 左键弹出账户下拉（A1，2026-09-13）。与卡片 ⋯ 菜单同款手法：
    /// WPF 的 ContextMenu 默认只响应右键，左键呼出须手动置 <c>PlacementTarget</c> 并 <c>IsOpen</c>。
    /// 菜单的 DataContext 经 <c>PlacementTarget.Tag</c> 桥接（XAML 内注释），
    /// 各菜单项的命令绑到 <c>SteamAccountVm.SwitchCommand</c>（项自身，不经可视树回溯）。
    /// </summary>
    private void OnAccountMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        // 卡片 ⋯ 菜单用默认（鼠标位）合适；账户下拉必须挂在按钮正下方才是"下拉"语义
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    /// <summary>
    /// A2 详情面板：卡片封面 / 名称单击 → 打开该游戏详情（2026-09-13）。
    /// 取的参数是<b>被点元素自身</b>的 <c>DataContext</c>（卡片模板内的元素，与外层按钮同款处理）。
    /// 打开后把键盘焦点交给面板，使 Esc 立即可用（否则焦点仍留在原处，Esc 收不到）。
    /// </summary>
    private void OnCardOpenDetail(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not GameCardVm vm)
        {
            return;
        }

        _vm.OpenDetailCommand.Execute(vm);
        DetailPanel.Focus();
    }

    /// <summary>点遮罩关闭详情面板。只挂在遮罩上——面板本体不挂，因此点面板内部不会误关。</summary>
    private void OnScrimClick(object sender, MouseButtonEventArgs e)
        => _vm.CloseDetailCommand.Execute(null);

    /// <summary>
    /// Esc 关闭详情面板（2026-09-13）。用 <c>PreviewKeyDown</c> 而非 <c>KeyDown</c>：
    /// 面板内的 Button / ScrollViewer 可能先把 KeyDown 标记为已处理，Preview（隧道）阶段
    /// 才能保证「焦点在面板内任何位置按 Esc 都关」。
    /// </summary>
    private void OnDetailPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.IsDetailOpen)
        {
            _vm.CloseDetailCommand.Execute(null);
            e.Handled = true;
        }
    }
}
