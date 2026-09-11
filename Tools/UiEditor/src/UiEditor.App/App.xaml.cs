using System.Windows;
using SystemToolkit.UI.Common;

namespace UiEditor.App;

/// <summary>入口：先应用主题（视图解析资源前），再开主窗。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 工具级兜底：UI 线程任何未捕获异常都不该硬崩进程（编辑器是长会话工具），落到状态栏并吞下。
        DispatcherUnhandledException += (_, args) =>
        {
            (MainWindow as MainWindow)?.ReportUnhandled(args.Exception);
            args.Handled = true;
        };
        ThemeManager.Apply(null); // 默认 Claude.Light
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
