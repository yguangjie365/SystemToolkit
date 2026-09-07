using System.Windows;

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
        Vm.SaveRuleCommand.Execute(null);
        if (string.IsNullOrEmpty(Vm.FormError))
        {
            Close();
        }
    }
}
