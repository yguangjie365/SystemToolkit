using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Modules.Settings;

/// <summary>
/// 设置页 VM（2026-09-07 建立，主人裁定：备份设置并入本模块统一管理）。
/// 当前承载「备份」分区；后续模块（软件/驱动/网络）的设置可继续加分区，
/// 各分区只依赖 Core 服务，模块之间零引用。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly BackupConfigService _config;
    private readonly ILogger _logger;

    public SettingsViewModel(BackupConfigService config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger.Instance;
        Load();
    }

    /// <summary>目录选择回调（View 注入；取消返回 null）。</summary>
    public Func<string, string?>? PickFolder { get; set; }

    // ── 备份设置 ──

    [ObservableProperty]
    private string _backupRootInput = "";

    [ObservableProperty]
    private string _maxSnapshotsInput = "7";

    [ObservableProperty]
    private string _maxWorkersInput = "4";

    [ObservableProperty]
    private string _defaultConflictPolicy = "rename";

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>从配置载入当前值到输入项。</summary>
    [RelayCommand]
    private void Load()
    {
        _config.Load();
        BackupRootInput = _config.Settings.BackupRoot;
        MaxSnapshotsInput = _config.Settings.MaxSnapshots.ToString();
        MaxWorkersInput = _config.Settings.MaxWorkers.ToString();
        DefaultConflictPolicy = _config.Settings.DefaultConflictPolicy;
        StatusText = string.IsNullOrWhiteSpace(BackupRootInput)
            ? "备份根为空：首次备份时会自动选择剩余空间最大的固定盘。"
            : "";
    }

    /// <summary>浏览选择备份根。</summary>
    [RelayCommand]
    private void PickBackupRoot()
    {
        string? dir = PickFolder?.Invoke("选择全局备份根目录");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            BackupRootInput = dir;
        }
    }

    /// <summary>自动挑选备份根（剩余空间最大的就绪固定盘）。</summary>
    [RelayCommand]
    private void AutoPickBackupRoot()
    {
        BackupRootInput = BackupConfigService.PickDefaultBackupRoot();
        StatusText = string.IsNullOrWhiteSpace(BackupRootInput)
            ? "⚠️ 未找到可用的固定盘，请手动选择备份根。"
            : "已自动选择，点「保存」生效。";
    }

    /// <summary>保存备份设置（数值非法时明确报错并中止，不静默钳制）。</summary>
    [RelayCommand]
    private void Save()
    {
        int maxSnapshots = ParseInt(MaxSnapshotsInput, 1, 100);
        if (maxSnapshots < 0)
        {
            StatusText = "❌ 默认快照保留数必须是 1-100 的整数。";
            return;
        }

        int maxWorkers = ParseInt(MaxWorkersInput, 1, 8);
        if (maxWorkers < 0)
        {
            StatusText = "❌ 并发线程数必须是 1-8 的整数。";
            return;
        }

        string root = BackupRootInput.Trim();
        if (root.Length > 0 && !Directory.Exists(root))
        {
            StatusText = "❌ 备份根路径不存在：" + root;
            return;
        }

        _config.Settings.BackupRoot = root;
        _config.Settings.MaxSnapshots = maxSnapshots;
        _config.Settings.MaxWorkers = maxWorkers;
        _config.Settings.DefaultConflictPolicy = DefaultConflictPolicy;
        _config.Save();

        StatusText = "✅ 设置已保存（下次备份/恢复生效）。";
        _logger.Info($"备份设置已保存：root={root} maxSnapshots={maxSnapshots} maxWorkers={maxWorkers} policy={DefaultConflictPolicy}");
    }

    /// <summary>解析整数并校验区间；非法返回 -1（由调用方报错，避免静默钳制）。</summary>
    private static int ParseInt(string text, int min, int max)
        => int.TryParse(text.Trim(), out int value) && value >= min && value <= max ? value : -1;
}
