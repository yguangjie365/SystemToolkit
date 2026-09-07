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
}
