using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 「网络优化」Tab：TCP 全局参数（保守可解释项）+ 接口跃点数。
/// 应用前自动快照（服务内置）；还原走快照回退；解析全 null 时零写入（降级不猜值纪律）。
/// </summary>
public partial class NetOptimizeTabViewModel : ObservableObject
{
    /// <summary>自动调谐五档（真机 netsh 帮助输出取值集）。</summary>
    public static readonly string[] AutoTuningOptions =
        ["disabled", "highlyrestricted", "restricted", "normal", "experimental"];

    /// <summary>RSS / ECN 三档（null = 不修改该项）。</summary>
    public static readonly string[] TriStateOptions = ["enabled", "disabled", "default"];

    /// <summary>网络限流三档：默认 0xA / 禁用 0xFFFFFFFF / 不修改。</summary>
    public static readonly string[] ThrottlingOptions = ["默认（10）", "禁用（0xFFFFFFFF）", "不修改"];

    private readonly ITcpTuningService _tuning;
    private readonly Action<string> _log;

    public NetOptimizeTabViewModel(ITcpTuningService tuning, Action<string> log)
    {
        _tuning = tuning;
        _log = log;
    }

    /// <summary>确认对话框回调（由组合根转接）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoaded;

    // ── 当前参数展示 ──
    [ObservableProperty]
    private string _currentSummary = "尚未读取——进入本页自动读取";

    // ── 调整项 ──
    [ObservableProperty]
    private string _selectedAutoTuning = "normal";

    [ObservableProperty]
    private string _selectedRss = "default";

    [ObservableProperty]
    private string _selectedEcn = "default";

    [ObservableProperty]
    private string _selectedThrottling = "不修改";

    // ── 接口跃点数 ──
    public ObservableCollection<InterfaceMetricInfo> Interfaces { get; } = new();

    [ObservableProperty]
    private InterfaceMetricInfo? _selectedInterface;

    [ObservableProperty]
    private string _metricInput = "";

    [ObservableProperty]
    private string _metricError = "";

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            TcpGlobalSettings current = await _tuning.ReadAsync().ConfigureAwait(true);
            CurrentSummary = Describe(current);
            SelectedAutoTuning = current.AutoTuningLevel ?? "normal";
            SelectedRss = current.RssEnabled is null ? "default" : current.RssEnabled.Value ? "enabled" : "disabled";
            SelectedEcn = current.EcnEnabled is null ? "default" : current.EcnEnabled.Value ? "enabled" : "disabled";

            Interfaces.Clear();
            foreach (InterfaceMetricInfo metric in await _tuning.ListInterfaceMetricsAsync().ConfigureAwait(true))
            {
                Interfaces.Add(metric);
            }

            IsLoaded = true;
            _log("[优化] TCP 参数与接口跃点数已刷新");
        }
        catch (Exception ex)
        {
            _log("[优化] ❌ 读取失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun => !IsBusy;

    /// <summary>当前参数描述（未知项按「降级不猜值」显示「未知」）。</summary>
    private static string Describe(TcpGlobalSettings s) =>
        $"自动调谐 {s.AutoTuningLevel ?? "未知"} · RSS {BoolText(s.RssEnabled)} · ECN {BoolText(s.EcnEnabled)}"
        + $" · 限流 {ThrottlingText(s.NetworkThrottlingIndex)} · 初始RTO {s.InitialRto ?? "未知"}"
        + $" · 拥塞提供程序 {s.CongestionProvider ?? "未知"} · RSC {s.RscState ?? "未知"} · RFC1323 {s.Rfc1323Timestamps ?? "未知"}";

    private static string BoolText(bool? v) => v is null ? "未知" : v.Value ? "enabled" : "disabled";

    private static string ThrottlingText(uint? v) => v switch
    {
        null => "未知",
        TcpGlobalSettings.ThrottlingDefault => "默认（10）",
        0xFFFFFFFF => "禁用",
        _ => $"0x{v:X}",
    };

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyAsync()
    {
        uint? throttling = SelectedThrottling switch
        {
            "默认（10）" => TcpGlobalSettings.ThrottlingDefault,
            "禁用（0xFFFFFFFF）" => 0xFFFFFFFFu,
            _ => null,
        };

        bool? rss = SelectedRss switch
        {
            "enabled" => true,
            "disabled" => false,
            _ => null,
        };

        bool? ecn = SelectedEcn switch
        {
            "enabled" => true,
            "disabled" => false,
            _ => null,
        };

        var target = new TcpGlobalSettings(SelectedAutoTuning, rss, ecn, throttling);
        if (!Confirm("应用 TCP 调优",
                $"目标参数：{Describe(target)}\n\n"
                + "执行前将自动保存改前快照；只对与当前不同的项发命令。\n确定继续吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            TcpApplyResult result = await _tuning.ApplyAsync(target, _log).ConfigureAwait(true);
            _log(result.Applied.Count == 0 && result.Skipped.Count == 0
                ? "[优化] 无变化项，未发送任何命令"
                : $"[优化] 应用完成：写入 {result.Applied.Count} 项（{string.Join("、", result.Applied)}），"
                  + $"跳过 {result.Skipped.Count} 项（{string.Join("、", result.Skipped)}）");
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RestoreAsync()
    {
        if (!Confirm("还原优化快照",
                "将按最近一次改前快照逐项还原 TCP 参数与接口跃点数。\n还原本身不覆盖快照（保留后悔药）。\n确定继续吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _tuning.RestoreAsync(_log).ConfigureAwait(true);
            _log("[优化] ✅ 已按快照还原");
            await LoadAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            _log("[优化] ⚠️ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyMetricAsync()
    {
        MetricError = "";
        if (SelectedInterface is null)
        {
            MetricError = "请先选择接口";
            return;
        }

        if (!int.TryParse(MetricInput, out int metric) || metric is < 1 or > 9999)
        {
            MetricError = "跃点数必须是 1-9999 的整数";
            return;
        }

        string adapter = SelectedInterface.Name;
        if (!Confirm("设置接口跃点数",
                $"接口「{adapter}」跃点数 → {metric}。\n\n"
                + "⚠️ 若该接口当前为「自动跃点」，此设置无法通过还原快照撤销（快照只记录数值）。\n确定继续吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _tuning.ApplyInterfaceMetricAsync(adapter, metric, _log).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool Confirm(string title, string message)
        => ConfirmRequest?.Invoke(title, message) != false;
}
