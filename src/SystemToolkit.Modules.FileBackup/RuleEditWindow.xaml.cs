using System.Windows;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>
/// 规则编辑弹窗（2026-09-07 单屏改造，用户拍板）：对齐软件管理页 SoftwareEditWindow 范式。
/// DataContext = FileBackupViewModel（表单字段沿用主 VM），保存走 SaveRuleCommand——
/// 校验失败（FormError 非空）时弹窗不关闭，错误就地显示。
/// </summary>
public partial class RuleEditWindow : Window
{
    private FileBackupViewModel Vm => (FileBackupViewModel)DataContext;

    internal RuleEditWindow(FileBackupViewModel vm)
    {
        InitializeComponent();
        // 标题栏跟随主题明暗（共享接线器：句柄就绪套一次 + 主题切换跟随 + 关闭退订）
        TitleBarThemeWiring.Attach(this);
        DataContext = vm;
    }

    /// <summary>模态打开（表单内容由调用方先行填充：EditRule 走 OnSelectedRuleChanged 载入，NewRule 走清空）。</summary>
    public static void Show(Window owner, FileBackupViewModel vm)
    {
        var window = new RuleEditWindow(vm)
        {
            Owner = owner,
            Title = string.IsNullOrEmpty(vm.EditingRuleId) ? "新建规则" : $"编辑规则 · {vm.RuleNameInput}",
        };
        window.ShowDialog();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 🟡 C-🟡-3 v11~v14 后续批次：SaveRule 内部虽已 try 包住 _rules.Add / Save，但**前置段**
        //（Directory.Exists 系列、SourcePathItems 拼接、IdGenerator.NewId 等）在 try 之外，
        // 网络盘掉线等场景仍会抛；从 Execute 冒泡到本事件处理器后**不经任何兜底**
        // ⇒ 弹窗既不关闭也无提示（FormError 仍为空），用户以为"点了保存没反应"。
        // 弹窗类文件允许直弹 MessageBox（RecurringDefectGuardTests 只拦 VM 直弹）。
        try
        {
            Vm.SaveRuleCommand.Execute(null);
            if (string.IsNullOrEmpty(Vm.FormError))
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "保存失败：" + ex.Message, "保存",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
