using System.Windows;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Modules.FileBackup;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 模块弹窗构造冒烟（2026-09-08）：真机反馈「恢复按钮点击无反应」——
/// 高嫌疑是弹窗 XAML 构造期 StaticResource 缺失抛 XamlParseException，
/// 被 AsyncRelayCommand 吞掉后表现为「点了没反应」。本类把三个弹窗的构造
/// 钉进构建期：资源缺失/绑定异常在这里当场爆出，而不是真机上无声消失。
/// 与视图冒烟同类串行——Application 全 AppDomain 单实例，必须经 EnsureApplication 复用
/// （EnsureApplication/LoadThemeWithFontsStubbed/RepoRoot 复用 ViewLoadSmokeGuardTests）。
/// </summary>
public class WindowSmokeGuardTests
{
    [Fact]
    public void RestoreDialog_ConstructsWithoutException()
    {
        Exception? captured = RunOnSta(() => new RestoreDialog("测试摘要", @"C:\Windows"));
        Assert.True(captured is null, $"RestoreDialog 构造抛异常：\n{captured}");
    }

    [Fact]
    public void RuleEditWindow_ConstructsWithoutException()
    {
        Exception? captured = RunOnSta(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), $"fb-editwin-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var config = new BackupConfigService(dir);
            config.Load();
            var vm = new FileBackupViewModel(
                config,
                new RuleManager(Path.Combine(dir, "rules"), null),
                new BackupService(config),
                new RestoreService(config),
                new RestoreService(config),
                new BackupTaskSchedulerService(new CommandRunner()));
            return new RuleEditWindow(vm);
        });
        Assert.True(captured is null, $"RuleEditWindow 构造抛异常：\n{captured}");
    }

    [Fact]
    public void PathInputWindow_ConstructsWithoutException()
    {
        Exception? captured = RunOnSta(() => new PathInputWindow("手动输入路径", ""));
        Assert.True(captured is null, $"PathInputWindow 构造抛异常：\n{captured}");
    }

    /// <summary>STA 线程执行工厂（弹窗是 Window，必须 STA + 主题资源就绪）。</summary>
    private static Exception? RunOnSta(Func<Window> factory)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                ViewLoadSmokeGuardTests.EnsureApplication()
                    .Resources.MergedDictionaries.Add(ViewLoadSmokeGuardTests.LoadThemeWithFontsStubbed());
                factory();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        return captured;
    }
}
