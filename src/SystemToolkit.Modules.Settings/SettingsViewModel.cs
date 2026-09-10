using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.Settings;

/// <summary>
/// 设置页 VM（2026-09-07 建立，主人裁定：备份设置并入本模块统一管理）。
/// 当前承载「备份」+「外观（主题）」分区；后续模块（软件/驱动/网络）的设置可继续加分区，
/// 各分区只依赖 Core/UI.Common 服务，模块之间零引用。
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

    // ── 外观（ADR-005：双主题即时切换）──

    /// <summary>可选主题标签（下拉展示）。</summary>
    public static (string Id, string Label)[] ThemeOptions =>
        ThemeManager.Themes.Select(t => (t.Id, t.Label)).ToArray();

    [ObservableProperty]
    private string? _selectedThemeId;

    /// <summary>下拉选中项变化 → 立即应用 + 持久化（无"保存"按钮，符合 NexBox 类工具直觉）。</summary>
    partial void OnSelectedThemeIdChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == ThemeManager.CurrentThemeId)
        {
            return;
        }

        try
        {
            ThemeManager.ApplyAndPersist(value);
            StatusText = "✅ 主题已切换（立即生效，重启后保持）。";
            _logger.Info($"[Settings] 主题切换：{value}");
        }
        catch (Exception ex)
        {
            StatusText = "❌ 主题切换失败：" + ex.Message;
            _logger.Error("[Settings] 主题切换失败", ex);
        }
    }

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

        // 审查 O9（2026-09-10 二轮）：BackupConfigService.Settings 是引擎消费的同一活对象——
        // 先快照旧值，Save 失败回滚，避免"内存新值/磁盘旧值"背离
        string oldRoot = _config.Settings.BackupRoot;
        int oldSnapshots = _config.Settings.MaxSnapshots;
        int oldWorkers = _config.Settings.MaxWorkers;
        string oldPolicy = _config.Settings.DefaultConflictPolicy;

        try
        {
            _config.Settings.BackupRoot = root;
            _config.Settings.MaxSnapshots = maxSnapshots;
            _config.Settings.MaxWorkers = maxWorkers;
            _config.Settings.DefaultConflictPolicy = DefaultConflictPolicy;
            _config.Save();

            StatusText = "✅ 设置已保存（下次备份/恢复生效）。";
            _logger.Info($"备份设置已保存：root={root} maxSnapshots={maxSnapshots} maxWorkers={maxWorkers} policy={DefaultConflictPolicy}");
        }
        catch (Exception ex)
        {
            _config.Settings.BackupRoot = oldRoot;
            _config.Settings.MaxSnapshots = oldSnapshots;
            _config.Settings.MaxWorkers = oldWorkers;
            _config.Settings.DefaultConflictPolicy = oldPolicy;

            // 🟡 审查 2026-09-10（🟡-15）：UI 输入也要一并回滚——否则输入框显示新值、
            // 引擎用旧值，用户以为已生效（直到下次保存才发现）。
            BackupRootInput = oldRoot;
            MaxSnapshotsInput = oldSnapshots.ToString();
            MaxWorkersInput = oldWorkers.ToString();
            DefaultConflictPolicy = oldPolicy;

            StatusText = "❌ 保存失败：" + ex.Message;
            _logger.Error("备份设置保存失败", ex);
        }
    }

    /// <summary>解析整数并校验区间；非法返回 -1（由调用方报错，避免静默钳制）。</summary>
    private static int ParseInt(string text, int min, int max)
        => int.TryParse(text.Trim(), out int value) && value >= min && value <= max ? value : -1;
}
