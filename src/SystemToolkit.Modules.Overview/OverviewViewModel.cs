using System.Collections.ObjectModel;
using System.ComponentModel;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Overview.Models;
using SystemToolkit.Core.Overview.Services;
namespace SystemToolkit.Modules.Overview;

/// <summary>
/// 本机概览页 ViewModel。
/// 数据策略：进入页面全量采集一次；每 2s 用 LiveUsageSampler + QuickPulseSampler
/// 就地更新（INPC）；切页 / 窗口失焦 Pause；无历史序列（Design/01 §3.1）。
/// </summary>
public sealed partial class OverviewViewModel : INotifyPropertyChanged, IPausableViewModel
{
    private readonly OverviewService _overviewService;
    private readonly LiveUsageSampler _liveSampler;
    private readonly QuickPulseSampler _quickSampler;
    private readonly OverviewSnapshotCache? _snapshotCache;
    private readonly ILogger _logger;

    private System.Windows.Threading.DispatcherTimer? _timer;
    private OverviewData? _data;
    private bool _busy;
    /// <summary>当前展示内容来自磁盘快照（副标题标注"采集中"，全量采集完成后清除）。</summary>
    private bool _showingSnapshot;

    private static readonly string[] StatLabels = ["处理器", "显卡", "内存", "存储"];
    private static readonly string[] StatNameEn = ["CPU", "GPU", "RAM", "DISK"];
    private static readonly string[] StatUsageLabels = ["使用率", "使用率", "内存使用", "存储已用"];

    /// <summary>顶部实时资源卡（4 张，参考图布局）。</summary>
    public ObservableCollection<StatCardVm> StatCards { get; } = new();

    /// <summary>详细信息卡（键值行）。</summary>
    public ObservableCollection<OverviewItem> DetailCards { get; } = new();

    /// <summary>系统信息面板（操作系统 / 用户与区域）。</summary>
    public ObservableCollection<OverviewItem> SystemPanels { get; } = new();

    /// <summary>已安装软件（表格数据源，受搜索过滤）。</summary>
    public ObservableCollection<InstalledProgram> InstalledPrograms { get; } = new();

    private readonly System.Windows.Data.ListCollectionView? _installedView;

    public System.Windows.Data.ListCollectionView? InstalledAppsView => _installedView;

    /// <summary>导出 Markdown 报告（2026-09-04 实装：SaveFileDialog + OverviewReportBuilder）。</summary>
    public System.Windows.Input.ICommand ExportReportCommand { get; }

    /// <summary>手动全量刷新（审查 M2：页头刷新图标按钮此前无命令绑定，纯死交互；
    /// RefreshFullAsync 内部有 _busy 闸门防重入）。</summary>
    public System.Windows.Input.ICommand RefreshFullCommand { get; }

    /// <summary>导出保存路径回调（View 注入；审查 🔴-1 采纳：VM 不直接弹对话框）。</summary>
    public Func<string?>? PickSavePath { get; set; }

    /// <summary>用户提示回调（View 注入；参数：message, title）。</summary>
    public Action<string, string>? NotifyUser { get; set; }

    public OverviewViewModel(
        OverviewService overviewService,
        LiveUsageSampler liveSampler,
        QuickPulseSampler quickSampler,
        OverviewSnapshotCache? snapshotCache = null,
        ILogger? logger = null)
    {
        _overviewService = overviewService;
        _liveSampler = liveSampler;
        _quickSampler = quickSampler;
        _snapshotCache = snapshotCache;
        _logger = logger ?? NullLogger.Instance;
        ExportReportCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportReport);
        // 审查 🟠-2：采集中禁用全量刷新（CommunityToolkit RelayCommand 经 CommandManager 自动重询）
        RefreshFullCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => RefreshFullAsync(), () => !_busy);
        _installedView = new System.Windows.Data.ListCollectionView(InstalledPrograms)
        {
            Filter = o => o is InstalledProgram p &&
                (string.IsNullOrEmpty(_filterText) ||
                 p.Name?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) == true ||
                 p.Publisher?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) == true)
        };
    }

    // ---- 绑定属性 ----

    private bool _isLive;

    public bool IsLive
    {
        get => _isLive;
        private set => SetField(ref _isLive, value);
    }

    private string _headerSubtitle = "采集中…";

    public string HeaderSubtitle
    {
        get => _headerSubtitle;
        private set => SetField(ref _headerSubtitle, value);
    }

    private string _filterText = "";

    /// <summary>已安装软件搜索关键字（名称 / 发布商）。</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value))
            {
                InstalledAppsView?.Refresh();
                OnPropertyChanged(nameof(AppCountText));
            }
        }
    }

    public string AppCountText => $"共 {InstalledAppsView?.Count ?? 0} 个应用";

    public int AppCount => InstalledAppsView?.Count ?? 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- 生命周期 ----

    /// <summary>进入页面 / 窗口重新激活：启动（或恢复）2s 刷新。首次进入做一次全量采集。</summary>
    public async Task ActivateAsync()
    {
        if (_data is null)
        {
            // 快照秒显（用户 2026-09-04）：硬件/系统信息/已安装程序是慢变量，直接上次的快照；
            // 温度与占用是快变量，进入页面即被 2s 采样器接管，不依赖快照新鲜度
            OverviewSnapshotCache.SnapshotEntry? snapshot = _snapshotCache?.TryLoad();
            if (snapshot is not null)
            {
                // 审查 2026-09-04（P1-6）：快照被外部改成含显式 null 的合法 JSON 时，RebuildFromData 可能抛异常——
                // 必须兜底降级为全量采集，否则经 MainWindow 的 fire-and-forget 调用会直接崩进程
                try
                {
                    _data = snapshot.Data;
                    _showingSnapshot = true;
                    RebuildFromData(snapshot.Data);
                    HeaderSubtitle = BuildHeaderSubtitle();
                    UpdateLiveMetrics();
                    _logger.Info($"已从磁盘快照秒显（采集于 {snapshot.CollectedAt.LocalDateTime:yyyy-MM-dd HH:mm}），后台全量采集中");
                    _ = RefreshFullAsync(); // 后台补采：完成后自动替换并更新快照
                }
                catch (Exception ex)
                {
                    _logger.Error("快照恢复失败，降级为全量采集：" + ex);
                    _data = null;
                    _showingSnapshot = false;
                    await RefreshFullAsync().ConfigureAwait(true);
                }
            }
            else
            {
                await RefreshFullAsync().ConfigureAwait(true);
            }
        }

        if (_timer is null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
            IsLive = true;
            _logger.Info("概览页刷新已启动（2s 间隔）");
        }
    }

    /// <summary>离开页面 / 窗口失焦：立即停止刷新（Design/01 §3.1 红线）。</summary>
    public void Pause()
    {
        if (_timer is { IsEnabled: true })
        {
            _timer.Stop();
            IsLive = false;
            _logger.Info("概览页刷新已暂停（切页或窗口失焦）");
        }
    }

    /// <summary>手动补一次全量采集。</summary>
    public async Task RefreshFullAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            OverviewData data = await Task.Run(_overviewService.Collect).ConfigureAwait(true);
            _data = data;
            _showingSnapshot = false;
            RebuildFromData(data);
            HeaderSubtitle = BuildHeaderSubtitle();
            UpdateLiveMetrics();
            _snapshotCache?.Save(data); // 全量采集成功即刷新快照（失败仅留痕，不影响主流程）
        }
        catch (Exception ex)
        {
            _logger.Error("概览全量采集失败：" + ex);
            // 审查 🔴-2：失败必须用户可见（🔴 不静默）；下次采集成功会被 BuildHeaderSubtitle 覆盖
            HeaderSubtitle = "⚠️ 全量采集失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- 内部 ----

    private async Task TickAsync()
    {
        if (_busy || _data is null)
        {
            return;
        }

        _busy = true;
        try
        {
            // 性能审查 P1-8：两路采样并行（原串行叠加拖长节拍）
            Task<UsageSample?> liveTask = _liveSampler.SampleAsync();
            Task<QuickSample?> quickTask = _quickSampler.SampleAsync(includeDisk: false);
            await Task.WhenAll(liveTask, quickTask).ConfigureAwait(true);
            // After WhenAll, both tasks are already completed: the double await is a synchronous continuation (AsyncGuard forbids reading the Result property)
            UsageSample? live = await liveTask.ConfigureAwait(true);
            // 审查 2026-09-04（P2）：磁盘活动率不展示（用户实测反馈），免掉每 2s 一条的 WMI 查询
            QuickSample? quick = await quickTask.ConfigureAwait(true);

            SetStat("处理器", live?.CpuPercent);
            SetStat("显卡", live?.GpuPercent);
            SetStat("内存", quick?.RamUsedPercent);
            // 存储卡不参与 2s 刷新：容量占比是慢变量（Collect 时已定），
            // 磁盘"活动率"会覆盖容量占比且常为 0 → 进度条恒空（用户 2026-09-04 实测）
            UpdateLiveMetrics();
        }
        catch (Exception ex)
        {
            _logger.Error("概览实时采样失败：" + ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>全量数据 → 视图模型（概览卡 + 详情卡 + 系统面板 + 已安装软件）。</summary>
    private void RebuildFromData(OverviewData data)
    {
        StatCards.Clear();
        for (int i = 0; i < StatLabels.Length; i++)
        {
            string label = StatLabels[i];
            OverviewItem? stat = data.HardwareStats.FirstOrDefault(s => s.Label == label);
            var card = new StatCardVm(IconFor(label), label, StatNameEn[i], StatUsageLabels[i])
            {
                Percent = stat?.Percent,
            };

            // 型号：同标签详情卡里 Key=型号 的第一行（Hardware 列表有重复 Label——
            // 如多网卡各一张"网卡"卡，禁止按 Label 建 Dictionary）
            OverviewItem? detail = data.Hardware.FirstOrDefault(h => h.Label == label);
            string? rawModel = null;
            if (detail is not null)
            {
                rawModel = detail.Rows.FirstOrDefault(r => r.Key == "型号")?.Value
                    ?? detail.Rows.FirstOrDefault(r => r.Key.Contains("容量") || r.Key.Contains("大小"))?.Value
                    ?? detail.Sub;
            }

            if (label == "处理器" || label == "显卡")
            {
                // 用户 2026-09-04：型号精简（去代际前缀 / Laptop GPU 后缀）
                card.Model = CleanModelText(rawModel, label);
            }
            else if (label == "内存")
            {
                // 用户 2026-09-04：内存型号展示「DDR5-4800 16GB×2」形态
                card.Model = BuildMemoryModel(detail) ?? CleanModelText(stat?.Sub, label);
            }
            else if (label == "存储")
            {
                // 2026-09-05 用户需求：副标题改为「类型+容量」汇总（SSD 1 TB / SSD … + HDD …）；
                // 汇总失败（介质解析不出）回退 2026-09-04 的首块盘型号形态
                card.Model = data.StorageSummary ?? CleanModelText(FirstDriveModel(detail), "存储");
            }

            // 四卡徽章统一大小；内存/存储无传感器显示「不可用」（用户 2026-09-04）
            StatCards.Add(card);
        }

        ReplaceItems(DetailCards, data.Hardware);
        ReplaceItems(SystemPanels, data.System);
        ReplaceItems(InstalledPrograms, data.InstalledPrograms);
        InstalledAppsView?.Refresh();
        OnPropertyChanged(nameof(AppCountText));
    }

    /// <summary>型号精简：去代际前缀（12th Gen）与 Laptop GPU 后缀（用户 2026-09-04）。</summary>
    private static string CleanModelText(string? raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "不可用";
        }

        string t = raw.Trim();
        if (label == "处理器")
        {
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^\d{1,2}(th|st|nd|rd) Gen\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        if (label == "显卡")
        {
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+Laptop(\s+GPU)?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return t.Length == 0 ? raw.Trim() : t;
    }

    /// <summary>内存型号组合「DDR5-4800 16GB×2」（类型/频率/条数来自内存详情行）。</summary>
    private static string? BuildMemoryModel(OverviewItem? memDetail)
    {
        if (memDetail is null)
        {
            return null;
        }

        string? type = memDetail.Rows.FirstOrDefault(r => r.Key == "类型")?.Value;
        string? speed = memDetail.Rows.FirstOrDefault(r => r.Key == "频率")?.Value;
        string? total = memDetail.Rows.FirstOrDefault(r => r.Key == "总容量")?.Value;
        string? sticks = memDetail.Rows.FirstOrDefault(r => r.Key == "条数")?.Value;

        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(speed))
        {
            string digits = new string(speed.Where(char.IsDigit).ToArray());
            if (digits.Length > 0)
            {
                parts.Add(type + "-" + digits);
            }
        }

        if (double.TryParse((total ?? "").Split(' ')[0], out double totalGb)
            && int.TryParse(sticks, out int stickCount) && stickCount > 0)
        {
            double perStick = Math.Round(totalGb / stickCount);
            parts.Add($"{perStick:0}GB×{stickCount}");
        }

        string built = string.Join(" ", parts);
        return built.Length == 0 ? null : built;
    }

    /// <summary>第一块磁盘的型号（存储行值形如「型号（931 GB）」，取括号前段）。</summary>
    private static string? FirstDriveModel(OverviewItem? storageDetail)
    {
        string? v = storageDetail?.Rows.FirstOrDefault(r => r.Key.StartsWith("存储"))?.Value;
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }

        int idx = v.IndexOf('（');
        return idx > 0 ? v[..idx].Trim() : v.Trim();
    }

    private static string IconFor(string label) => label switch
    {
        "处理器" => "\uE950",
        "显卡" => "\uE95D",
        "内存" => "\uE964",
        "存储" => "\uE958",
        _ => "\uE7C3",
    };

    private void SetStat(string label, int? percent)
    {
        if (percent is null)
        {
            return;
        }

        StatCardVm? card = StatCards.FirstOrDefault(c => c.NameZh == label);
        if (card is not null)
        {
            card.Percent = percent;
        }
    }

    /// <summary>
    /// 更新概览卡右上角温度徽章。
    /// 用户 2026-09-04 修正：无传感器显示「未检测」（中性灰），不得标「不可用」误导；
    /// 内存/存储无温度语义，不打标签；温度分级——CPU 70/90、GPU 75/90（笔记本 61°C 属正常）。
    /// </summary>
    private void UpdateLiveMetrics()
    {
        SensorSnapshot? sensors = _data?.Sensors;

        if (sensors?.CpuTempC is { } cpuT)
        {
            SetBadge("处理器", Math.Round(cpuT) + "°C", TempLevel(cpuT, 70, 90));
        }
        else
        {
            SetBadge("处理器", "未检测", 3);
        }

        if (sensors?.GpuTempC is { } gpuT)
        {
            SetBadge("显卡", Math.Round(gpuT) + "°C", TempLevel(gpuT, 75, 90));
        }
        else
        {
            SetBadge("显卡", "未检测", 3);
        }

        // 内存/硬盘温度徽章（FEAT-T1，用户 2026-09-05：徽章显示各自身上最高的温度值；
        // 内存阈值取 TSOD 临界 85，硬盘阈值取 NVMe Warning 75/临界 85 的同档口径）
        if (sensors?.MemoryTemps is { } dimms && dimms.Count > 0)
        {
            float maxDimm = dimms.Max(t => t.TempC);
            SetBadge("内存", Math.Round(maxDimm) + "°C", TempLevel(maxDimm, 70, 85));
        }
        else
        {
            SetBadge("内存", null, 3); // 非提权 / 条子无 TSOD：无温度语义，不显示徽章
        }

        if (sensors?.StorageTemps is { } drives && drives.Count > 0)
        {
            float? maxDrive = HardwareSensorProbe.SelectMaxDriveTemperature(drives);
            if (maxDrive is { } driveTemp)
            {
                SetBadge("存储", Math.Round(driveTemp) + "°C", TempLevel(driveTemp, 70, 85));
            }
            else
            {
                SetBadge("存储", null, 3);
            }
        }
        else
        {
            SetBadge("存储", null, 3); // 非提权 / 盘温不可读：不显示徽章
        }
    }

    private static int TempLevel(double t, double warn, double hot)
        => t >= hot ? 2 : (t >= warn ? 1 : 0);

}
