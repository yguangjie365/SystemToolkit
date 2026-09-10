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

    private readonly System.Windows.Threading.Dispatcher? _dispatcher;

    public FileBackupViewModel(
        BackupConfigService config,
        RuleManager rules,
        IBackupService backup,
        IRestoreService restore,
        IRestorePreviewProvider preview,
        BackupTaskSchedulerService scheduler,
        ElevatedVssClient? vssClient = null,
        ILogger? logger = null,
        System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _config = config;
        _rules = rules;
        _backup = backup;
        _restore = restore;
        _preview = preview;
        _scheduler = scheduler;
        _vssClient = vssClient;
        _logger = logger ?? NullLogger.Instance;
        _dispatcher = dispatcher;

        // 初始化时检测VSS可用性
        CheckVssAvailability();

        // 🔴 审查 2026-09-11（🔴-3）：VSS 状态色是**主题派生刷**（构造期 ThemeBrush.Find 定值），
        // 而本 VM 是 DI 单例（FileBackupModule.AddSingleton）、主题切换只重建视图不重建 VM——
        // 不订阅就会让状态点停在旧主题配色（深色主题下浅色的绿/黄/红压在近黑底上）。
        // 重跑检查即可重取颜色（CheckVssAvailability 幂等，不做副作用）。
        ThemeManager.ThemeChanged += CheckVssAvailability;
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

    /// <summary>导出保存路径回调（View 注入；取消返回 null）——审查 🔴-3 采纳：VM 不直接弹对话框。</summary>
    public Func<string?>? PickSavePath { get; set; }

    /// <summary>导入打开路径回调（View 注入；取消返回 null）——审查 🔴-3 采纳。</summary>
    public Func<string?>? PickOpenPath { get; set; }

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

        // 🟡 审查 2026-09-10（🟡-1）：不能"注入即报绿"——Helper exe 缺失时 VSS 通道实际不可用，
        // 报绿会让用户以为可备份，直到首次备份才失败。改为按文件存在性预检。
        if (!File.Exists(_vssClient.HelperPath))
        {
            VssStatus = "未安装 Helper（提权组件缺失，VSS 通道不可用）";
            VssStatusColor = ThemeBrush.Find("Brush_Warning", "#D97706");
            return;
        }

        VssStatus = "已配置（首次备份时将请求 UAC 提权）";
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

        // 选中即载入编辑表单（快照列表随之刷新）。
        // 🟠 注意：主屏已无内嵌表单——这些 Rule*Input/IsEditing 状态仅服务 RuleEditWindow 弹窗
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
        // 审查 🟠-3 采纳（2026-09-09）：同步命令——_rules.Save() 的磁盘/权限/序列化异常
        // 会直冲 UI 线程；就地捕获并以 FormError 呈现（不弹未处理异常对话框）
        try
        {
            _rules.Add(rule);
            _rules.Save();
        }
        catch (Exception ex)
        {
            FormError = $"保存失败：{ex.Message}";
            Log($"[备份] ❌ 规则保存失败：{ex.Message}");
            _logger.Error($"备份规则保存失败：{name}", ex);
            return;
        }

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

        // 审查 🟠-3 同类带修（2026-09-09）：与 SaveRule 同族——同步命令里的持久化异常必须就地捕获
        try
        {
            _rules.Remove(target.RuleId);
            _rules.Save();
        }
        catch (Exception ex)
        {
            Log($"[备份] ❌ 规则删除失败：{ex.Message}");
            _logger.Error($"备份规则删除失败：{target.RuleName}", ex);
            return;
        }

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
        // newIndex == Rules.Count 合法——指示线落在最后一条之下即插到末尾（审查 🔴-2 采纳：
        // 原 >= 校验把末尾插入整个拒掉，指示线会显示但顺序不落库）
        if (oldIndex < 0 || newIndex < 0
            || oldIndex >= Rules.Count || newIndex > Rules.Count
            || oldIndex == newIndex)
        {
            return;
        }

        string movedId = Rules[oldIndex].RuleId;
        string movedName = Rules[oldIndex].RuleName;
        var ordered = Rules.Select(r => r.RuleId).ToList();
        ordered.RemoveAt(oldIndex);
        if (newIndex > oldIndex)
        {
            newIndex--; // 移除后被拖规则右侧的目标索引左移一位（末尾插入 Count → ordered.Count）
        }

        ordered.Insert(Math.Clamp(newIndex, 0, ordered.Count), movedId);
        _rules.Reorder(ordered); // 🟡 审查 2026-09-10（🟡-3）：Reorder 内部已 Save，此处不再重复写盘
        Log($"[备份] 规则顺序已调整：{movedName}");
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
        _rules.SetEnabled(SelectedRule.RuleId, newValue); // 🟡-3：SetEnabled 内部已 Save，不再重复写盘
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

        string? target = PickSavePath?.Invoke();
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        try
        {
            (int count, string savedTo) = _rules.Export(Rules.Select(r => r.RuleId), target);
            Log($"[备份] ✅ 已导出 {count} 条规则：{savedTo}");
            _logger.Info($"备份规则导出：{count} 条 → {savedTo}");
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
        string? path = PickOpenPath?.Invoke();
        if (string.IsNullOrEmpty(path))
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
            (int ok, List<string> errors) = _rules.Import(path);
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

    // ── 定时任务注册（Task Scheduler，批次二） ──

    [RelayCommand]
    private async Task RegisterScheduleAsync()
    {
        if (SelectedRule is null)
        {
            return;
        }

        // 🔴 校验口径与 BackupSchedule.IsDue 一致（含范围）：此前只查 \d{2}:\d{2} 格式，
        // "25:00" 能存下但判定永远不成立 → 定时静默失效。
        if (EnableScheduleInput is false || !BackupSchedule.IsValidDailyTime(DailyTimeInput))
        {
            Log("[定时] ❌ 请先勾选「启用定时」并填写合法的 HH:mm 时间（00:00–23:59）");
            return;
        }

        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            Log("[定时] ❌ 无法确定主程序路径");
            return;
        }

        // 审查 🔴 采纳（2026-09-09）：任务计划程序服务未启动/权限不足/任务冲突都会抛——
        // 原实现无 catch，异常被 AsyncRelayCommand 吞掉，用户以为注册成功
        try
        {
            int exit = await _scheduler.RegisterAsync(SelectedRule.RuleId, DailyTimeInput.Trim(), exe, Log).ConfigureAwait(true);
            if (exit == 0)
            {
                // 🔴 2026-09-08（审查 G-4）：异步流程里同步 Execute 命令会绕过 IsBusy 闸门，
                // 且 SaveRule 会整体重载规则并改选中项；改为只把定时相关字段局部落库。
                SaveScheduleFieldsOnly();
                Log($"[定时] ✅ 已注册每日 {DailyTimeInput.Trim()} 的定时任务（错过后自动补做一次）");
            }
            else
            {
                Log($"[定时] ❌ 注册失败（退出码 {exit}）：可能是权限不足或任务计划程序服务不可用");
            }
        }
        catch (Exception ex)
        {
            Log($"[定时] ❌ 注册异常：{ex.Message}");
            _logger.Error($"定时任务注册失败（规则 {SelectedRule.RuleId}）", ex);
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

        // 审查 🔴 采纳（2026-09-09）：同注册——注销失败/异常必须可见
        try
        {
            int exit = await _scheduler.UnregisterAsync(SelectedRule.RuleId, Log).ConfigureAwait(true);
            if (exit == 0)
            {
                Log("[定时] 定时任务已注销");
            }
            else
            {
                Log($"[定时] ❌ 注销失败（退出码 {exit}）");
            }
        }
        catch (Exception ex)
        {
            Log($"[定时] ❌ 注销异常：{ex.Message}");
            _logger.Error($"定时任务注销失败（规则 {SelectedRule.RuleId}）", ex);
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
    private sealed class UiProgressReporter(
        Action<int, int, string> onProgress,
        Action<string> onPhase,
        Action<string> onLog,
        System.Windows.Threading.Dispatcher? dispatcher)
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
        /// 🔴 2026-09-08 二修（审查 🔴-1 采纳）：不再抓全局 Application.Current——改为构造注入
        /// Dispatcher（与 MusicManager 统一）；注入 null（测试宿主）直调，语义与原「app is null」一致。
        /// </summary>
        private void Marshal(Action action)
        {
            System.Windows.Threading.Dispatcher? d = dispatcher;
            if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive)
            {
                action();
                return;
            }

            if (d.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                _ = d.BeginInvoke(action);
            }
            catch (Exception)
            {
                // 封送失败（Dispatcher 已关闭等）不能让备份/恢复中断——进度只是展示层
            }
        }
    }
}
