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
        // 🟡 V12-F6（2026-09-14）：Trim 落在**唯一的出口**上 —— 摘要一直承诺"已 Trim"，
        // 实现却原样返回（调用方若自己 Trim，同一契约就有两处实现，迟早不一致）。
        return window.ShowDialog() == true ? window.InputBox.Text.Trim() : null;
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
