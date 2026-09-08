using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>
/// 文件备份视图（2026-09-07 单屏改造）：左规则列表 + 右所选规则快照表 + 底部共享日志面板；
/// 规则编辑经 RuleEditWindow 弹窗（EditRuleRequest 回调注入，对齐 SoftwareEditRequest 模式）；
/// 规则列表支持拖拽排序（MoveRuleByDrag，替代上移/下移按钮）。
/// Loaded 完成组合根接线（确认/目录选择/弹窗回调 + 配置规则加载，幂等）。
/// </summary>
public partial class FileBackupView : UserControl
{
    private FileBackupViewModel Vm => (FileBackupViewModel)DataContext;

    private bool _loaded;

    // ── 规则列表拖拽排序状态 ──
    private Point _dragStartPoint;
    private bool _isDragging;
    private RuleRowVm? _draggedRow;

    public FileBackupView(FileBackupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        Vm.ConfirmRequest = (title, message) =>
            System.Windows.MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK;
        Vm.PickFolder = title =>
        {
            var dialog = new OpenFolderDialog { Title = title };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
        };
        // 文件多选（规则编辑弹窗「添加文件」）
        Vm.PickFiles = title =>
        {
            var dialog = new OpenFileDialog { Title = title, Multiselect = true, Filter = "所有文件|*.*" };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : [];
        };
        // 手动输入路径（规则编辑弹窗「手动输入」）
        Vm.PromptInput = (title, defaultValue) =>
            PathInputWindow.Show(Window.GetWindow(this), title, defaultValue);
        // 恢复选项对话框（目标二选一 + 冲突策略四选一）
        Vm.RestoreRequest = (summary, originalPath) =>
            RestoreDialog.Show(Window.GetWindow(this), summary, originalPath);
        // 规则编辑弹窗（新建/编辑共用；表单已由命令先行填充）
        Vm.EditRuleRequest = () => RuleEditWindow.Show(Window.GetWindow(this), Vm);
        // 导出保存 / 导入打开路径（审查 🔴-3 采纳：对话框一律 View 注入，VM 不持窗口）
        Vm.PickSavePath = () =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出备份规则",
                Filter = "JSON 规则文件 (*.json)|*.json",
                FileName = $"backup-rules_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        };
        Vm.PickOpenPath = () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入备份规则",
                Filter = "JSON 规则文件 (*.json)|*.json",
                CheckFileExists = true,
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        };
        Vm.Initialize();
    }

    // ══════════ 规则列表拖拽排序 ══════════

    /// <summary>操作日志折叠开关（审查 🟠-6 采纳）。⚠️ IsChecked="True" 会在 InitializeComponent
    /// 解析期触发 Checked——此时 LogHost 尚未赋值，必须判空（XAML 构建期事件坑，2026-09-08）。</summary>
    private void OnLogToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle && LogHost is not null)
        {
            bool expanded = toggle.IsChecked == true;
            LogHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = expanded ? "▾ 操作日志" : "▸ 操作日志";
        }
    }

    private void RuleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
        // 记录按住的那一行（审查 🟠-5 采纳：不再依赖 SelectedItem——「按住就拖」不至于拖错行）
        _draggedRow = ResolveRowUnderMouse(e.OriginalSource) as RuleRowVm;
    }

    /// <summary>沿可视树向上找 ListBoxItem 取行 DataContext（FileTransfer 拖拽同款 helper）。</summary>
    private static object? ResolveRowUnderMouse(object? source)
    {
        if (source is not DependencyObject start)
        {
            return null;
        }

        DependencyObject d = start;
        while (d is not null)
        {
            if (d is ListBoxItem item)
            {
                return item.DataContext;
            }

            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }

        return null;
    }

    private void RuleList_MouseMove(object sender, MouseEventArgs e)
    {
        RuleRowVm? dragged = _draggedRow ?? RuleList.SelectedItem as RuleRowVm;
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging || dragged is null)
        {
            return;
        }

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isDragging = true;
        try
        {
            // 自定义格式存对象引用（Serializable 格式会要求 RuleRowVm 可序列化而抛异常）
            DragDrop.DoDragDrop(RuleList, new DataObject("RuleRow", dragged), DragDropEffects.Move);
        }
        finally
        {
            _isDragging = false;
        }
    }

    private void RuleList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("RuleRow"))
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        DropIndicator.Visibility = Visibility.Visible;
        DropIndicator.Margin = new Thickness(2, DropIndicatorY(e.GetPosition(RuleList).Y), 8, 0);
    }

    private void RuleList_DragLeave(object sender, DragEventArgs e)
        => DropIndicator.Visibility = Visibility.Collapsed;

    private void RuleList_Drop(object sender, DragEventArgs e)
    {
        _isDragging = false;
        DropIndicator.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent("RuleRow"))
        {
            return;
        }

        var dragged = (RuleRowVm)e.Data.GetData("RuleRow");
        // 落点索引按指示线同一套算法计算，保证「看到的线 = 实际插入位置」
        int to = InsertIndexAt(e.GetPosition(RuleList).Y);
        Vm.MoveRuleByDrag(Vm.Rules.IndexOf(dragged), to);
    }

    /// <summary>按鼠标 Y 计算插入索引（越过某条目中线即插到其后）。</summary>
    private int InsertIndexAt(double y)
    {
        int index = 0;
        for (int i = 0; i < RuleList.Items.Count; i++)
        {
            if (RuleList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement item)
            {
                continue;
            }

            double top = item.TransformToAncestor(RuleList).Transform(new Point(0, 0)).Y;
            if (y > top + item.ActualHeight / 2)
            {
                index = i + 1;
            }
        }

        return index;
    }

    /// <summary>指示线的 Y 坐标（插入点上方；末尾则落在最后一条之下）。</summary>
    private double DropIndicatorY(double y)
    {
        int index = InsertIndexAt(y);
        double lineY = 0;
        for (int i = 0; i < RuleList.Items.Count; i++)
        {
            if (RuleList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement item)
            {
                continue;
            }

            double top = item.TransformToAncestor(RuleList).Transform(new Point(0, 0)).Y;
            if (i < index)
            {
                lineY = top + item.ActualHeight;
            }
            else if (i == index)
            {
                return top;
            }
        }

        return lineY;
    }
}
