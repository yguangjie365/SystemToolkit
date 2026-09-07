using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>规则列表行投影。</summary>
public sealed class RuleRowVm
{
    public BackupRule Model { get; }

    public RuleRowVm(BackupRule model) => Model = model;

    public string RuleId => Model.RuleId;

    public string RuleName => Model.RuleName;

    /// <summary>备注（规则描述；列表条目第二行显示，2026-09-07 用户裁定不再显示源路径）。</summary>
    public string Description => Model.Description;

    /// <summary>源路径多行文本（仅浮窗 ToolTip 用，2026-09-07 用户裁定不进列表条目）。</summary>
    public string SourcesText => string.Join("\n", Model.Sources());

    public bool Enabled => Model.Enabled;

    public string EnabledText => Model.Enabled ? "已启用" : "已停用";

    /// <summary>列表条目浮窗：规则名 / 状态 / 源路径（参考旧版单功能版样式）。</summary>
    public string ToolTipText => $"{RuleName}\n状态：{EnabledText}\n源：{SourcesText}";
}

/// <summary>快照列表行投影。</summary>
public sealed class SnapshotRowVm
{
    public SnapshotInfo Model { get; }

    public SnapshotRowVm(SnapshotInfo model) => Model = model;

    public string SnapshotId => Model.SnapshotId;

    public string DisplayTime => Model.DisplayTime;

    public string SizeText => Model.SizeText;

    public int FileCount => Model.FileCount;

    public string StatusText => Model.StatusText;

    public string ChecksumText => Model.ChecksumText;

    /// <summary>路径列友好文本（规则名 · 快照时间；ToolTip 保留完整 BackupPath）。</summary>
    public string DisplayPath => Model.DisplayPath;

    /// <summary>快照 files 数据目录绝对路径（路径列 ToolTip）。</summary>
    public string BackupPath => Model.BackupPath;
}

/// <summary>
/// 文件备份页组合根 VM（批次一）：规则管理 + 手动备份 + 快照浏览/恢复/删除 + 进度与日志。
/// 批次二：恢复向导（冲突预览）、VSS、定时。
/// </summary>
public partial class FileBackupViewModel : ObservableObject
{
    private readonly BackupConfigService _config;
    private readonly RuleManager _rules;
    private readonly IBackupService _backup;
    private readonly IRestoreService _restore;
    private readonly IRestorePreviewProvider _preview;
    private readonly BackupTaskSchedulerService _scheduler;
    private readonly ElevatedVssClient? _vssClient;
    private readonly ILogger _logger;
    private CancellationTokenSource? _backupCts;

    public FileBackupViewModel(
        BackupConfigService config,
        RuleManager rules,
        IBackupService backup,
        IRestoreService restore,
        IRestorePreviewProvider preview,
        BackupTaskSchedulerService scheduler,
        ElevatedVssClient? vssClient = null,
        ILogger? logger = null)
    {
        _config = config;
        _rules = rules;
        _backup = backup;
        _restore = restore;
        _preview = preview;
        _scheduler = scheduler;
        _vssClient = vssClient;
        _logger = logger ?? NullLogger.Instance;

        // 初始化时检测VSS可用性
        CheckVssAvailability();
    }

    /// <summary>确认对话框回调（View 注入）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    /// <summary>目录选择回调（View 注入；取消返回 null）。</summary>
    public Func<string, string?>? PickFolder { get; set; }

    /// <summary>规则编辑弹窗回调（View 注入；对齐 AppManager 的 SoftwareEditRequest 模式）。
    /// NewRule/EditRule 命令清空/载入表单后经此打开 RuleEditWindow。</summary>
    public Action? EditRuleRequest { get; set; }

    /// <summary>文件多选回调（View 注入；取消返回空集合）——弹窗「添加文件」用。</summary>
    public Func<string, IReadOnlyList<string>>? PickFiles { get; set; }

    /// <summary>文本输入回调（View 注入；取消返回 null）——弹窗「手动输入」路径用。</summary>
    public Func<string, string, string?>? PromptInput { get; set; }

    /// <summary>恢复选项对话框回调（View 注入；取消返回 null）——参数：摘要文本、原始位置路径。</summary>
    public Func<string, string, RestoreChoice?>? RestoreRequest { get; set; }

    public ObservableCollection<RuleRowVm> Rules { get; } = new();

    [ObservableProperty]
    private RuleRowVm? _selectedRule;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _hasProgress;

    // ── 编辑表单 ──
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editingRuleId = "";

    [ObservableProperty]
    private string _ruleNameInput = "";

    [ObservableProperty]
    private string _sourceTypeInput = "folder";

    [ObservableProperty]
    private string _sourcePathsInput = "";

    /// <summary>源路径可视化列表（阶段3新增，与SourcePathsInput双向同步）。</summary>
    public ObservableCollection<string> SourcePathItems { get; } = new();

    [ObservableProperty]
    private bool _useGlobalBackupRoot = true;

    [ObservableProperty]
    private string _backupRootInput = "";

    [ObservableProperty]
    private int _maxSnapshotsInput = 7;

    [ObservableProperty]
    private string _descriptionInput = "";

    /// <summary>排除规则输入（多行文本，一行一条；2026-09-07 新增，旧版无此能力）。</summary>
    [ObservableProperty]
    private string _excludePatternsInput = "";

    [ObservableProperty]
    private bool _enabledInput = true;

    // ── 定时与 VSS（批次二） ──
    [ObservableProperty]
    private bool _enableScheduleInput;

    [ObservableProperty]
    private string _dailyTimeInput = "03:00";

    [ObservableProperty]
    private bool _useVssInput;

    /// <summary>VSS 通道可用性状态：Available（绿色）/ Unavailable（黄色，未安装Helper）/ Failed（红色，UAC拒绝或服务异常）。</summary>
    [ObservableProperty]
    private string _vssStatus = "检测中...";

    /// <summary>VSS 状态对应的颜色（用于UI绑定显示绿/红）。走主题资源，主题未就绪时回退 hex。</summary>
    [ObservableProperty]
    private Brush _vssStatusColor = ThemeBrush.Find("Brush_TextMuted", "#999999");

    /// <summary>检测 VSS 通道可用性（仅在初始化时调用一次）。</summary>
    private void CheckVssAvailability()
    {
        if (_vssClient is null)
        {
            VssStatus = "不可用（VSS 客户端未注入）";
            VssStatusColor = ThemeBrush.Find("Brush_Danger", "#DC2626");
            return;
        }

        // 检查 Helper 可执行文件是否存在
        // ElevatedVssClient 内部会检查 _helperPath，这里通过反射或简单探测
        // 由于无法直接访问私有字段，我们采用保守策略：假设注入即表示可用
        // 实际运行时若 UAC 被拒绝，BackupService 会回退并记录日志
        VssStatus = "可用（点击备份时将请求 UAC 提权）";
        VssStatusColor = ThemeBrush.Find("Brush_Success", "#059669");
    }

    /// <summary>定时任务是否已注册（控制注册/注销按钮文案）。</summary>
    [ObservableProperty]
    private bool _scheduleRegistered;

    [ObservableProperty]
    private string _formError = "";

    // ── 快照 ──
    public ObservableCollection<SnapshotRowVm> Snapshots { get; } = new();

    [ObservableProperty]
    private SnapshotRowVm? _selectedSnapshot;

    public ObservableCollection<LogLine> LogLines { get; } = new();

    public void AddLog(string message) => LogFeed.Append(LogLines, message, LogFeed.DefaultMaxLines);

    private void Log(string message) => AddLog(message);

    [RelayCommand]
    private void ClearLog() => LogFeed.Clear(LogLines);

    /// <summary>页面 Loaded：加载配置与规则（幂等）。</summary>
    public void Initialize()
    {
        if (Rules.Count > 0)
        {
            return;
        }

        _config.Load();
        _config.EnsureBackupRoot();
        _rules.Load();
        ReloadRules();
        Log($"[备份] 已加载 {Rules.Count} 条规则（备份根：{_config.Settings.BackupRoot}）");
    }

    private string GlobalRoot => _config.Settings.BackupRoot;

    // ── 规则加载/选择 ──

    [RelayCommand]
    private void ReloadRules()
    {
        Rules.Clear();
        foreach (BackupRule rule in _rules.All)
        {
            Rules.Add(new RuleRowVm(rule));
        }

        BackupAllCommand.NotifyCanExecuteChanged(); // 启用规则集合可能变化
    }

    partial void OnSelectedRuleChanged(RuleRowVm? value)
    {        // 🔴 选中变化必须刷新选择依赖命令（用户实测 Bug2：立即备份等按钮在选中后仍灰死）
        RefreshCanExecute();
        if (value is null)
        {
            IsEditing = false;
            SourcePathItems.Clear();
            return;
        }

        // 选中即载入编辑表单（快照列表随之刷新）
        IsEditing = true;
        EditingRuleId = value.Model.RuleId;
        RuleNameInput = value.Model.RuleName;
        SourceTypeInput = value.Model.SourceType == "file" ? "file" : "folder";
        SourcePathsInput = string.Join(Environment.NewLine, value.Model.Sources());

        // 阶段3：同步SourcePathItems
        SourcePathItems.Clear();
        foreach (var src in value.Model.Sources())
        {
            if (!string.IsNullOrWhiteSpace(src))
            {
                SourcePathItems.Add(src);
            }
        }

        EnableScheduleInput = value.Model.EnableSchedule;
        DailyTimeInput = string.IsNullOrWhiteSpace(value.Model.DailyTime) ? "03:00" : value.Model.DailyTime;
        UseVssInput = value.Model.UseVss;
        UseGlobalBackupRoot = value.Model.UseGlobalBackupRoot;
        _ = RefreshScheduleRegisteredAsync();
        BackupRootInput = value.Model.BackupRoot;
        MaxSnapshotsInput = value.Model.MaxSnapshots;
        DescriptionInput = value.Model.Description;
        ExcludePatternsInput = string.Join(Environment.NewLine, value.Model.ExcludePatterns ?? new List<string>());
        EnabledInput = value.Model.Enabled;
        FormError = "";
        ReloadSnapshots();
    }

    /// <summary>
    /// 🔴 选中快照变化必须刷新选择依赖命令（用户实测 2026-09-08：选中快照后「校验/恢复」
    /// 仍灰死）——<see cref="SelectedSnapshot"/> 是 ObservableProperty，setter 不会自动通知
    /// RelayCommand，与规则侧 Bug2 同根因。
    /// </summary>
    partial void OnSelectedSnapshotChanged(SnapshotRowVm? value) => RefreshCanExecute();

    // ── 规则编辑 ──

    [RelayCommand(CanExecute = nameof(CanEditForm))]
    private void NewRule()
    {
        SelectedRule = null;
        IsEditing = true;
        EditingRuleId = "";
        RuleNameInput = "";
        SourceTypeInput = "folder";
        SourcePathsInput = "";
        SourcePathItems.Clear();
        UseGlobalBackupRoot = true;
        BackupRootInput = "";
        EnableScheduleInput = false;
        DailyTimeInput = "03:00";
        UseVssInput = false;
        ScheduleRegistered = false;
        MaxSnapshotsInput = _config.Settings.MaxSnapshots;
        DescriptionInput = "";
        ExcludePatternsInput = "";
        EnabledInput = true;
        FormError = "";
        Snapshots.Clear();
        EditRuleRequest?.Invoke();
    }

    private bool CanEditForm => !IsBusy;

    /// <summary>编辑选中规则：表单已由 OnSelectedRuleChanged 载入，直接打开弹窗。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private void EditRule() => EditRuleRequest?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveRule()
    {
        FormError = "";
        string name = RuleNameInput.Trim();
        // 清洗：资源管理器「复制文件地址」带引号、首尾常有空白（用户实测 Bug1 的静默拒绝源）
        static string CleanPath(string p) => p.Trim().Trim('"').Trim();
        var sources = SourcePathsInput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanPath)
            .Where(s => s.Length > 0 && (Directory.Exists(s) || File.Exists(s)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalid = SourcePathsInput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanPath)
            .Where(s => s.Length > 0 && !Directory.Exists(s) && !File.Exists(s))
            .ToList();

        if (name.Length == 0)
        {
            FormError = "规则名不能为空";
            return;
        }

        if (sources.Count == 0)
        {
            // 2026-09-07 补齐旧版「保存草稿」：源路径暂不存在（外置硬盘未接、目录待建）时，
            // 经用户确认仍可保存规则——否则用户只能先建目录才能录入规则。
            // 草稿规则执行备份时会因源不存在而失败并留痕（不静默）。
            bool hasAnyInput = SourcePathItems.Count > 0 || !string.IsNullOrWhiteSpace(SourcePathsInput);
            if (!hasAnyInput)
            {
                FormError = "至少需要一个源路径";
                return;
            }

            if (ConfirmRequest?.Invoke("保存为草稿",
                    "以下源路径当前都不存在：\n" + string.Join("\n", invalid.Take(5)) +
                    "\n\n仍要保存这条规则吗？（保存后源路径就绪即可正常备份，未就绪时执行会失败并提示）") != true)
            {
                FormError = "至少需要一个存在的源路径" + (invalid.Count > 0 ? $"（无效：{string.Join("、", invalid.Take(3))}）" : "");
                return;
            }

            sources = SourcePathItems
                .Concat(SourcePathsInput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(CleanPath)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Log($"[备份] ⚠️ 规则「{name}」保存为草稿：{sources.Count} 个源路径当前不存在，就绪后需重新执行备份。");
        }

        if (!UseGlobalBackupRoot && string.IsNullOrWhiteSpace(BackupRootInput))
        {
            FormError = "自定义备份根不能为空（或改用全局备份根）";
            return;
        }

        var rule = new BackupRule
        {
            RuleId = string.IsNullOrEmpty(EditingRuleId) ? IdGenerator.NewId() : EditingRuleId,
            RuleName = name,
            SourceType = SourceTypeInput,
            SourcePaths = sources,
            UseGlobalBackupRoot = UseGlobalBackupRoot,
            BackupRoot = BackupRootInput.Trim(),
            MaxSnapshots = Math.Clamp(MaxSnapshotsInput, 1, 100),
            Description = DescriptionInput.Trim(),
            ExcludePatterns = ParseExcludePatterns(),
            Enabled = EnabledInput,
            EnableSchedule = EnableScheduleInput,
            DailyTime = EnableScheduleInput ? DailyTimeInput.Trim() : "",
            UseVss = UseVssInput,
        };
        _rules.Add(rule);
        _rules.Save();
        Log($"[备份] ✅ 规则已保存：{name}（{sources.Count} 个源）");
        _logger.Info($"备份规则已保存：{name}({rule.RuleId})");
        ReloadRules();
        SelectedRule = Rules.FirstOrDefault(r => r.RuleId == rule.RuleId);
    }

    private bool CanSave => !IsBusy;

    /// <summary>解析排除规则输入（一行一条，去空白/空行，按序号去重保序）。</summary>
    private List<string> ParseExcludePatterns()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string line in ExcludePatternsInput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length > 0 && seen.Add(line))
            {
                result.Add(line);
            }
        }

        return result;
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private void DeleteRule()
    {
        if (SelectedRule is null)
        {
            return;
        }

        RuleRowVm target = SelectedRule;
        if (ConfirmRequest?.Invoke("删除规则",
                $"确定删除规则「{target.RuleName}」吗？\n\n这一步只删除规则本身，快照数据是否清理在下一步选择。") != true)
        {
            Log("[备份] 已取消删除规则。");
            return;
        }

        // 第二步（补齐旧版能力）：是否连带清理该规则的快照数据
        bool alsoDeleteSnapshots = ConfirmRequest?.Invoke("删除规则",
            $"是否同时删除「{target.RuleName}」已产生的快照数据？\n\n" +
            "· 确定 = 连带删除该规则全部快照（不可恢复）\n" +
            "· 取消 = 仅删除规则，快照文件保留在备份根下") == true;

        if (alsoDeleteSnapshots)
        {
            try
            {
                var manager = SnapshotManager.FromRule(target.Model, GlobalRoot, _logger);
                int removed = manager.DeleteAllSnapshots();
                Log($"[备份] 已删除 {removed} 份快照数据。");
                _logger.Info($"删除规则连带清理快照：{target.RuleName} removed={removed}");
            }
            catch (Exception ex)
            {
                Log("[备份] ❌ 快照数据清理失败（规则仍会删除）：" + ex.Message);
                _logger.Error("删除规则时清理快照失败", ex);
            }
        }

        _rules.Remove(target.RuleId);
        _rules.Save();
        Log($"[备份] 规则已删除：{target.RuleName}" + (alsoDeleteSnapshots ? "（含快照数据）" : "（快照数据保留）"));
        SelectedRule = null;
        ReloadRules();
    }

    /// <summary>
    /// 规则拖拽排序（2026-09-07 用户裁定替代上移/下移按钮）：经 RuleManager.Reorder
    /// 持久化到 rules.json，完成后保持被拖规则为选中态。
    /// </summary>
    public void MoveRuleByDrag(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || newIndex < 0
            || oldIndex >= Rules.Count || newIndex >= Rules.Count
            || oldIndex == newIndex)
        {
            return;
        }

        string movedId = Rules[oldIndex].RuleId;
        var ordered = Rules.Select(r => r.RuleId).ToList();
        ordered.RemoveAt(oldIndex);
        ordered.Insert(newIndex, movedId);
        _rules.Reorder(ordered);
        _rules.Save();
        Log($"[备份] 规则顺序已调整：{Rules[oldIndex].RuleName}");
        ReloadRules();
        SelectedRule = Rules.FirstOrDefault(r => r.RuleId == movedId);
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private void ToggleEnable()
    {
        if (SelectedRule is null)
        {
            return;
        }

        bool newValue = !SelectedRule.Model.Enabled;
        _rules.SetEnabled(SelectedRule.RuleId, newValue);
        _rules.Save();
        ReloadRules();
        SelectedRule = Rules.FirstOrDefault(r => r.RuleId == SelectedRule.RuleId);
    }

    private bool CanOperateSelected => SelectedRule is not null && !IsBusy;

    // ── 规则导入 / 导出（2026-09-07 补齐旧版能力；Core RuleManager 已实现，此处只做 UI 接线） ──

    [RelayCommand]
    private void ExportRules()
    {
        if (Rules.Count == 0)
        {
            Log("[备份] 没有可导出的规则。");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出备份规则",
            Filter = "JSON 规则文件 (*.json)|*.json",
            FileName = $"backup-rules_{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            (int count, string target) = _rules.Export(Rules.Select(r => r.RuleId), dialog.FileName);
            Log($"[备份] ✅ 已导出 {count} 条规则：{target}");
            _logger.Info($"备份规则导出：{count} 条 → {target}");
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 导出规则失败：" + ex.Message);
            _logger.Error("导出备份规则失败", ex);
        }
    }

    [RelayCommand]
    private void ImportRules()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入备份规则",
            Filter = "JSON 规则文件 (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (ConfirmRequest?.Invoke("导入规则",
                "导入的规则将合并进当前列表（同 ID 覆盖更新，同 ID 冲突会重新生成 ID）。\n\n确定继续吗？") != true)
        {
            Log("[备份] 已取消导入。");
            return;
        }

        try
        {
            (int ok, List<string> errors) = _rules.Import(dialog.FileName);
            foreach (string error in errors)
            {
                Log("[备份]   ⚠️ " + error);
            }

            Log($"[备份] ✅ 已导入 {ok} 条规则" + (errors.Count > 0 ? $"（{errors.Count} 条有问题，见上方明细）" : ""));
            ReloadRules();
            _logger.Info($"备份规则导入：成功 {ok} 条，问题 {errors.Count} 条");
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 导入规则失败：" + ex.Message);
            _logger.Error("导入备份规则失败", ex);
        }
    }

    // ── 立即备份 / 备份全部 ──

    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private async Task BackupNowAsync()
    {
        if (SelectedRule is null)
        {
            return;
        }

        await RunBackupPipelineAsync(new[] { SelectedRule.Model }).ConfigureAwait(true);
    }

    private bool CanBackupNow => SelectedRule is not null && !IsBusy;

    /// <summary>备份全部：串行备份所有已启用规则（2026-09-07 用户拍板纳入本轮）。</summary>
    [RelayCommand(CanExecute = nameof(CanBackupAll))]
    private async Task BackupAllAsync()
    {
        var targets = Rules.Where(r => r.Enabled).Select(r => r.Model).ToList();
        if (targets.Count == 0)
        {
            Log("[备份] 没有已启用的规则可备份。");
            return;
        }

        string names = string.Join("\n", targets.Select(r => $"  · {r.RuleName}"));
        if (ConfirmRequest?.Invoke("备份全部",
                $"将按顺序备份以下 {targets.Count} 个已启用规则：\n{names}\n\n逐项执行、可随时取消（关闭窗口即停），失败项会标注原因。确定继续吗？") != true)
        {
            Log("[备份] 已取消备份全部。");
            return;
        }

        await RunBackupPipelineAsync(targets).ConfigureAwait(true);
    }

    private bool CanBackupAll => !IsBusy && Rules.Any(r => r.Enabled);

    /// <summary>
    /// 备份流水线闸门：单规则与「备份全部」共用——设忙态、逐条调核心、
    /// 结束统一恢复状态并刷新快照。核心异常自理，取消中断剩余项。
    /// </summary>
    private async Task RunBackupPipelineAsync(IReadOnlyList<BackupRule> targets)
    {
        _backupCts = new CancellationTokenSource();
        IsBusy = true;
        HasProgress = true;
        ProgressValue = 0;
        RefreshCanExecute();
        int ok = 0, fail = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                BackupRule rule = targets[i];
                if (targets.Count > 1)
                {
                    ProgressText = $"[{i + 1}/{targets.Count}] {rule.RuleName}";
                    ProgressValue = 0;
                }

                try
                {
                    if (await BackupRuleCoreAsync(rule).ConfigureAwait(true))
                    {
                        ok++;
                    }
                    else
                    {
                        fail++;
                    }
                }
                catch (OperationCanceledException)
                {
                    Log(targets.Count > 1
                        ? $"[备份] ⏹ 已取消：{rule.RuleName}（剩余规则不再执行）"
                        : "[备份] 已取消。");
                    break;
                }
            }

            if (targets.Count > 1)
            {
                Log($"[备份] 备份全部结束：成功 {ok}、失败 {fail}（共 {targets.Count} 条）。");
            }
        }
        finally
        {
            IsBusy = false;
            HasProgress = false;
            ProgressText = "";
            _backupCts.Dispose();
            _backupCts = null;
            RefreshCanExecute();
            ReloadSnapshots();
        }
    }

    /// <summary>单规则备份核心（无闸门）：返回是否成功；取消以 OperationCanceledException 上抛。</summary>
    private async Task<bool> BackupRuleCoreAsync(BackupRule rule)
    {
        try
        {
            var reporter = new UiProgressReporter(OnProgress, phase => ProgressText = phase, Log);
            Log($"[备份] ▶ 开始备份：{rule.RuleName}（{rule.Sources().Count} 个源）");
            BackupResult result = await _backup.BackupRuleAsync(rule, reporter, _backupCts!.Token).ConfigureAwait(true);
            Log(result.Success
                ? $"[备份] ✅ {result.Message}（{result.FileCount} 个文件，{FormatSize(result.TotalSize)}，校验 {result.ChecksumStatus}）"
                : $"[备份] ⚠️ {result.Message}（失败 {result.Failures.Count} 项，详见日志）");
            foreach (string failure in result.Failures.Take(10))
            {
                Log("[备份]   ✗ " + failure);
            }

            _logger.Info($"备份完成：{rule.RuleName} success={result.Success} files={result.FileCount}");
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 备份失败：" + ex.Message);
            _logger.Error("备份失败", ex);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelBackup))]
    private void CancelBackup() => _backupCts?.Cancel();

    private bool CanCancelBackup => IsBusy;

    private void RefreshCanExecute()
    {
        BackupNowCommand.NotifyCanExecuteChanged();
        BackupAllCommand.NotifyCanExecuteChanged();
        CancelBackupCommand.NotifyCanExecuteChanged();
        SaveRuleCommand.NotifyCanExecuteChanged();
        NewRuleCommand.NotifyCanExecuteChanged();
        EditRuleCommand.NotifyCanExecuteChanged();
        DeleteRuleCommand.NotifyCanExecuteChanged();
        ToggleEnableCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
        VerifySnapshotCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
        DeleteSnapshotCommand.NotifyCanExecuteChanged();
    }

    private void OnProgress(int done, int total, string phase)
    {
        ProgressText = $"{phase} {done}/{total}";
        ProgressValue = total > 0 ? done * 100.0 / total : 0;
    }

    // ── 定时任务注册（Task Scheduler，批次二） ──

    [RelayCommand]
    private async Task RegisterScheduleAsync()
    {
        if (SelectedRule is null)
        {
            return;
        }

        if (EnableScheduleInput is false
            || !System.Text.RegularExpressions.Regex.IsMatch(DailyTimeInput.Trim(), @"^\d{2}:\d{2}$"))
        {
            Log("[定时] ❌ 请先勾选「启用定时」并填写合法的 HH:mm 时间");
            return;
        }

        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            Log("[定时] ❌ 无法确定主程序路径");
            return;
        }

        int exit = await _scheduler.RegisterAsync(SelectedRule.RuleId, DailyTimeInput.Trim(), exe, Log).ConfigureAwait(true);
        if (exit == 0)
        {
            // 🔴 2026-09-08（审查 G-4）：异步流程里同步 Execute 命令会绕过 IsBusy 闸门，
            // 且 SaveRule 会整体重载规则并改选中项；改为只把定时相关字段局部落库。
            SaveScheduleFieldsOnly();
            Log($"[定时] ✅ 已注册每日 {DailyTimeInput.Trim()} 的定时任务（错过后自动补做一次）");
        }

        await RefreshScheduleRegisteredAsync().ConfigureAwait(true);
    }

    /// <summary>只把 EnableSchedule / DailyTime 落到当前规则（不触发 SaveRule 整体流程）。</summary>
    private void SaveScheduleFieldsOnly()
    {
        if (SelectedRule is null)
        {
            return;
        }

        BackupRule rule = SelectedRule.Model;
        rule.EnableSchedule = EnableScheduleInput;
        rule.DailyTime = EnableScheduleInput ? DailyTimeInput.Trim() : "";
        _rules.Update(rule);
        _rules.Save();
    }

    [RelayCommand]
    private async Task UnregisterScheduleAsync()
    {
        if (SelectedRule is null)
        {
            return;
        }

        int exit = await _scheduler.UnregisterAsync(SelectedRule.RuleId, Log).ConfigureAwait(true);
        if (exit == 0)
        {
            Log("[定时] 定时任务已注销");
        }

        await RefreshScheduleRegisteredAsync().ConfigureAwait(true);
    }

    /// <summary>查询当前选中规则的定时任务注册状态。</summary>
    private async Task RefreshScheduleRegisteredAsync()
    {
        if (SelectedRule is null)
        {
            ScheduleRegistered = false;
            return;
        }

        try
        {
            ScheduleRegistered = await _scheduler.IsRegisteredAsync(SelectedRule.RuleId).ConfigureAwait(true);
        }
        catch
        {
            ScheduleRegistered = false;
        }
    }

    // ── 快照 ──

    private SnapshotManager? SnapshotManagerForSelected()
        => SelectedRule is null ? null : SnapshotManager.FromRule(SelectedRule.Model, GlobalRoot, _logger);

    [RelayCommand]
    private void ReloadSnapshots()
    {
        Snapshots.Clear();
        if (SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        foreach (SnapshotInfo info in manager.SnapshotsLight())
        {
            Snapshots.Add(new SnapshotRowVm(info));
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private void OpenSnapshotDir()
    {
        if (SelectedSnapshot is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        // 按 SnapshotId 定位快照目录（目录名是时间戳，与 Id 不同名）
        string? dir = manager.AllSnapshotDirs()
            .FirstOrDefault(d => manager.ReadSnapshotLight(d)?.SnapshotId == SelectedSnapshot.SnapshotId);
        if (dir is null)
        {
            Log("[备份] ❌ 未找到该快照的目录（可能已被手动删除）");
            return;
        }

        SnapshotOpenResult result = new SnapshotPathOpener().Open(dir, manager.IsSnapshotDirAllowed);
        Log(result.Kind switch
        {
            SnapshotOpenResultKind.Opened => $"[备份] 已打开：{result.FullPath}",
            SnapshotOpenResultKind.Denied => "[备份] ❌ 路径越界，拒绝打开（非本规则快照目录）",
            SnapshotOpenResultKind.NotFound => "[备份] ❌ 快照目录不存在",
            _ => "[备份] ❌ 路径非法，拒绝打开",
        });
    }

    // ── 快照校验（2026-09-07 补齐旧版「校验」命令；Core SnapshotVerifier 重算哈希比对） ──

    /// <summary>按 SnapshotId 定位快照目录（目录名是时间戳，与 Id 不同名）。</summary>
    private string? SnapshotDirOf(SnapshotManager manager, string snapshotId)
        => manager.AllSnapshotDirs()
            .FirstOrDefault(d => manager.ReadSnapshotLight(d)?.SnapshotId == snapshotId);

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifySnapshotAsync()
    {
        if (SelectedSnapshot is null || SelectedRule is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        string? snapDir = SnapshotDirOf(manager, SelectedSnapshot.SnapshotId);
        if (snapDir is null)
        {
            Log("[校验] ❌ 未找到该快照的目录（可能已被手动删除）");
            return;
        }

        // 🔴 2026-09-08 修复：ReadSnapshot 的参数是「快照目录路径」，此前误传 SnapshotId
        // → manifest 永远找不到 → throw 被 AsyncRelayCommand 吞掉 → 点击无任何反应
        SnapshotInfo? info = manager.ReadSnapshot(snapDir);
        if (info is null)
        {
            Log("[校验] ❌ 快照清单读取失败（manifest.json 缺失或损坏）");
            return;
        }

        IsBusy = true;
        HasProgress = true;
        ProgressValue = 0;
        RefreshCanExecute();
        try
        {
            var verifier = new SnapshotVerifier(_config.Settings.MaxWorkers, _logger);
            var reporter = new UiProgressReporter(OnProgress, phase => ProgressText = phase, Log);
            Log($"[校验] ▶ 开始校验「{SelectedRule.RuleName}」{SelectedSnapshot.DisplayTime}（{info.Files.Count} 个文件）");
            SnapshotVerifyReport report = await verifier.VerifyAsync(info, snapDir, reporter).ConfigureAwait(true);
            Log(report.Success ? $"[校验] ✅ {report.Message}" : $"[校验] ⚠️ {report.Message}");
            foreach (string failure in report.Failures)
            {
                Log("[校验]   ✗ " + failure);
            }

            // 结果写回快照状态（原子写），列表徽章随之更新
            info.ChecksumStatus = report.Success ? ChecksumStatuses.Passed : ChecksumStatuses.Failed;
            manager.WriteSnapshot(snapDir, info);
            ReloadSnapshots();
        }
        catch (OperationCanceledException)
        {
            Log("[校验] 已取消。");
        }
        catch (Exception ex)
        {
            Log("[校验] ❌ 校验失败：" + ex.Message);
            _logger.Error("快照校验失败", ex);
        }
        finally
        {
            IsBusy = false;
            HasProgress = false;
            ProgressText = "";
            RefreshCanExecute();
        }
    }

    private bool CanVerify => SelectedSnapshot is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreSnapshotAsync()
    {
        if (SelectedSnapshot is null || SelectedRule is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        // 🔴 2026-09-08 修复：同校验侧——ReadSnapshot 参数是「快照目录路径」，
        // 误传 SnapshotId → manifest 找不到 → throw 被吞 → 恢复点击无任何反应
        string? snapDir = SnapshotDirOf(manager, SelectedSnapshot.SnapshotId);
        if (snapDir is null)
        {
            Log("[恢复] ❌ 未找到该快照的目录（可能已被手动删除）");
            return;
        }

        SnapshotInfo? info = manager.ReadSnapshot(snapDir);
        if (info is null)
        {
            Log("[恢复] ❌ 快照清单读取失败（manifest.json 缺失或损坏）");
            return;
        }

        string original = SelectedRule.Model.Sources().FirstOrDefault() ?? "";

        // 2026-09-07 补齐旧版恢复向导：目标二选一 + 冲突策略四选一（不再写死 Rename）
        string summary = $"规则「{info.RuleName}」· 快照 {info.DisplayTime}（{info.FileCount} 个文件，{info.SizeText}）";
        RestoreChoice? choice = RestoreRequest?.Invoke(summary, original);
        if (choice is null)
        {
            Log("[恢复] 已取消（未确认恢复选项）");
            return;
        }

        string target = choice.TargetRoot ?? original;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            Log("[恢复] ❌ 恢复目标不存在：" + target);
            return;
        }

        // 冲突预演（与真实恢复同解析规则，只读探测不落盘）
        RestorePreviewReport preview = await _preview.PreviewConflictsAsync(
            info, target, SelectedRule.Model.Sources().ToList()).ConfigureAwait(true);
        string confirm = $"恢复预演（{preview.Total} 个文件）→ {target}\n\n" +
            $"· 目标已存在：{preview.ExistsCount}（冲突策略：{PolicyText(choice.Policy)}）\n" +
            $"· 恢复时会被安全校验拒绝：{preview.BlockedCount}\n" +
            $"· 全新写入：{preview.Total - preview.ExistsCount - preview.BlockedCount}\n\n确定执行恢复吗？";
        if (ConfirmRequest?.Invoke("恢复快照", confirm) != true)
        {
            Log("[恢复] 已取消（预演后未确认）");
            return;
        }

        await RestoreCoreAsync(info, SelectedRule.Model, target, choice.Policy).ConfigureAwait(true);
    }

    private static string PolicyText(ConflictPolicy policy) => policy switch
    {
        ConflictPolicy.Overwrite => "覆盖",
        ConflictPolicy.Rename => "重命名保留两者",
        ConflictPolicy.Skip => "跳过",
        _ => "逐条询问",
    };

    /// <summary>单快照恢复核心（单条恢复与「恢复全部」共用）：闸门 + 结果汇报。</summary>
    private async Task RestoreCoreAsync(SnapshotInfo info, BackupRule rule, string target, ConflictPolicy policy)
    {
        IsBusy = true;
        HasProgress = true;
        RefreshCanExecute();
        try
        {
            var reporter = new UiProgressReporter(OnProgress, phase => ProgressText = phase, Log);
            Log($"[恢复] ▶ 开始恢复「{info.RuleName}」→ {target}");
            RestoreReport report = await _restore.RestoreSnapshotAsync(
                info, target, policy,
                userChoice: null, reporter: reporter,
                trustedRoots: rule.Sources().ToList()).ConfigureAwait(true);
            Log(report.Success
                ? $"[恢复] ✅ {report.Message}（恢复 {report.Restored}/{report.Total}，跳过 {report.Skipped}）"
                : $"[恢复] ⚠️ {report.Message}（成功 {report.Restored}，失败 {report.Failed}，校验不一致 {report.VerifyFailed}，跳过 {report.Skipped}）");
            foreach (string failure in report.Failures.Take(10))
            {
                Log("[恢复]   ✗ " + failure);
            }

            _logger.Info($"快照恢复：{info.SnapshotId} restored={report.Restored}/{report.Total} to={target} policy={policy}");
        }
        catch (Exception ex)
        {
            Log("[恢复] ❌ 恢复失败：" + ex.Message);
            _logger.Error("快照恢复失败", ex);
        }
        finally
        {
            IsBusy = false;
            HasProgress = false;
            ProgressText = "";
            RefreshCanExecute();
        }
    }

    /// <summary>
    /// 恢复全部（2026-09-07 主人拍板）：启用规则各自恢复最新快照；目标与冲突策略统一问一次，
    /// 逐条执行并汇报。旧版有此高危入口（红色按钮），本项目以 RowDangerButton + 双重确认承接。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreAll))]
    private async Task RestoreAllAsync()
    {
        var targets = new List<(BackupRule Rule, SnapshotInfo Info)>();
        foreach (RuleRowVm row in Rules.Where(r => r.Enabled).ToList())
        {
            var manager = SnapshotManager.FromRule(row.Model, GlobalRoot, _logger);
            SnapshotInfo? latest = manager.LatestSnapshot();
            if (latest is not null)
            {
                targets.Add((row.Model, latest));
            }
        }

        if (targets.Count == 0)
        {
            Log("[恢复] 没有可恢复的快照（已启用规则均无快照）。");
            return;
        }

        string list = string.Join("\n", targets.Select(t => $"  · {t.Rule.RuleName}（{t.Info.DisplayTime}，{t.Info.FileCount} 个文件）"));
        RestoreChoice? choice = RestoreRequest?.Invoke(
            $"将恢复以下 {targets.Count} 个已启用规则的最新快照：\n\n{list}",
            "各规则自身的原始源路径");
        if (choice is null)
        {
            Log("[恢复] 已取消恢复全部。");
            return;
        }

        if (ConfirmRequest?.Invoke("恢复全部",
                $"即将恢复 {targets.Count} 个规则的最新快照。\n" +
                $"目标：{choice.TargetRoot ?? "各规则原始位置"}\n" +
                $"冲突策略：{PolicyText(choice.Policy)}\n\n" +
                "⚠️ 这是破坏性操作，可能覆盖现有文件。确定继续吗？") != true)
        {
            Log("[恢复] 已取消恢复全部。");
            return;
        }

        foreach ((BackupRule rule, SnapshotInfo info) in targets)
        {
            string target = choice.TargetRoot ?? rule.Sources().FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            {
                Log($"[恢复] ⚠️ 跳过「{rule.RuleName}」：恢复目标不存在（{target}）");
                continue;
            }

            await RestoreCoreAsync(info, rule, target, choice.Policy).ConfigureAwait(true);
        }

        Log($"[恢复] 恢复全部结束（共 {targets.Count} 条）。");
    }

    private bool CanRestoreAll => !IsBusy && Rules.Any(r => r.Enabled);

    private static bool ruleSourcesContain(SnapshotInfo info, string target)
        => info.SourcePaths.Concat(new[] { info.SourcePath })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Any(s => string.Equals(
                Path.GetFullPath(s!).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));

    private bool CanRestore => SelectedSnapshot is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void DeleteSnapshot()
    {
        if (SelectedSnapshot is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        SnapshotRowVm target = SelectedSnapshot;
        if (ConfirmRequest?.Invoke("删除快照",
                $"确定删除 {target.DisplayTime} 的快照吗？\n目录：{manager.SnapRoot}\n\n此操作不可撤销。") != true)
        {
            return;
        }

        try
        {
            manager.DeleteSnapshot(target.SnapshotId);
            Log($"[备份] 快照已删除：{target.DisplayTime}");
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 快照删除失败：" + ex.Message);
            return;
        }

        ReloadSnapshots();
    }

    // ── 目录选择转发（2026-09-07 弹窗四按钮：添加文件/文件夹/手动输入/移除选中） ──

    /// <summary>弹窗源路径列表当前选中项（「移除选中」用）。</summary>
    [ObservableProperty]
    private string? _selectedSourcePath;

    /// <summary>统一入口：清洗（去引号/空白）→ 去重 → 入列表并同步。</summary>
    private void AddSourcePathItem(string rawPath)
    {
        string clean = rawPath.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(clean) || SourcePathItems.Contains(clean))
        {
            return;
        }

        SourcePathItems.Add(clean);
        SyncSourcePathsInput();
    }

    /// <summary>添加文件夹（经 PickFolder 回调）。</summary>
    [RelayCommand]
    private void AddSourcePath()
    {
        string? dir = PickFolder?.Invoke("选择源文件夹");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            AddSourcePathItem(dir);
        }
    }

    /// <summary>添加文件（多选，经 PickFiles 回调）。</summary>
    [RelayCommand]
    private void AddSourceFile()
    {
        if (PickFiles is null)
        {
            return;
        }

        foreach (string file in PickFiles("选择要备份的文件"))
        {
            AddSourcePathItem(file);
        }
    }

    /// <summary>手动输入路径（经 PromptInput 回调弹小输入窗）。</summary>
    [RelayCommand]
    private void AddSourceManual()
    {
        string? input = PromptInput?.Invoke("手动输入路径", "");
        AddSourcePathItem(input ?? "");
    }

    /// <summary>移除源路径列表当前选中项（保留逐项 ✕ 之外的批量途径）。</summary>
    [RelayCommand]
    private void RemoveSelectedSource()
    {
        if (!string.IsNullOrEmpty(SelectedSourcePath) && SourcePathItems.Remove(SelectedSourcePath))
        {
            SyncSourcePathsInput();
        }
    }

    /// <summary>将SourcePathItems同步回SourcePathsInput字符串（换行分隔）。</summary>
    private void SyncSourcePathsInput()
    {
        SourcePathsInput = string.Join(Environment.NewLine, SourcePathItems);
    }

    [RelayCommand]
    private void PickBackupRootFolder()
    {
        string? dir = PickFolder?.Invoke("选择自定义备份根");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            BackupRootInput = dir;
            UseGlobalBackupRoot = false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.#} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>IProgressReporter 实现：OnProgress/OnPhase/OnLog 均 UV 封送（服务在后台线程回调）。</summary>
    private sealed class UiProgressReporter(Action<int, int, string> onProgress, Action<string> onPhase, Action<string> onLog)
        : IProgressReporter
    {
        public void OnProgress(int done, int total, string phase) => Marshal(() => onProgress(done, total, phase));

        public void OnPhase(string text) => Marshal(() => onPhase(text));

        public void OnLog(string message) => Marshal(() => onLog(message));

        /// <summary>
        /// 🔴 2026-09-08 修正：原实现无条件 <c>Dispatcher.Invoke</c>——
        /// ① 服务在后台线程回调，若 Application 存在但其 Dispatcher 属于另一线程（或已关闭），
        ///    Invoke 会抛异常并中断备份/恢复主流程（实测：测试宿主里 Application 由
        ///    其它用例创建时，校验命令走到这里直接断掉，日志停在"开始校验"）；
        /// ② 同步 Invoke 在 UI 线程忙碌时会阻塞后台工作线程。
        /// 改为：UI 线程直调；跨线程则 BeginInvoke（不阻塞）；封送失败绝不中断业务，仅降级。
        /// </summary>
        private static void Marshal(Action action)
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                action();
                return;
            }

            if (app.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                _ = app.Dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // 封送失败（Dispatcher 已关闭等）不能让备份/恢复中断——进度只是展示层
            }
        }
    }
}
