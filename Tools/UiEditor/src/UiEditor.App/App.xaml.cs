using System.Windows;
using SystemToolkit.UI.Common;

namespace UiEditor.App;

/// <summary>入口：先应用主题（视图解析资源前），再开主窗。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Apply(null); // 默认 Claude.Light
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
