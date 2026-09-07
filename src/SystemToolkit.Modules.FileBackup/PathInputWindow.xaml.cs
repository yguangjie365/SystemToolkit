using System.Windows;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>路径手动输入小窗（规则编辑弹窗「手动输入」按钮触发，模态；取消返回 null）。</summary>
public partial class PathInputWindow : Window
{
    internal PathInputWindow(string title, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        InputBox.Text = defaultValue;
        Loaded += (_, _) => InputBox.Focus();
    }

    /// <summary>模态打开；确定返回输入文本（已 Trim，可为空由调用方判断），取消返回 null。</summary>
    public static string? Show(Window owner, string title, string defaultValue)
    {
        var window = new PathInputWindow(title, defaultValue) { Owner = owner };
        return window.ShowDialog() == true ? window.InputBox.Text : null;
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
