using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>备份域测试共享设施：隔离临时目录 + 配置环境（自旧工程 TestHelpers 迁移，裁掉仅服务未移植 VM 用例的 MakeEnv）。</summary>
public static class TestHelpers
{
    /// <summary>创建隔离临时目录与备份配置（真实磁盘但目录独立，模拟生产加载路径）。</summary>
    public static (BackupConfigService Config, string TempDir) MakeConfig(string prefix = "fb_test")
    {
        string temp = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
        var cfg = new BackupConfigService(temp, log: _ => { });
        cfg.Load();
        cfg.Settings.BackupRoot = Path.Combine(temp, "backup_root");
        cfg.Settings.MaxSnapshots = 7;
        cfg.Settings.MaxWorkers = 4;
        cfg.Save();
        return (cfg, temp);
    }
}
