using System.Windows;
using System.Windows.Controls;
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
    /// 卡片「⋯」按钮 → 左键弹出 ContextMenu（M-UI-3 落地 2026-09-05）。
    /// WPF 的 ContextMenu 默认只响应右键；左键呼出须手动置 IsOpen 并指定 PlacementTarget。
    /// 菜单项的命令/参数绑定见 XAML 内注释（走 PlacementTarget.Tag / .DataContext 桥接）。
    /// </summary>
    private void OnCardMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
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
}
