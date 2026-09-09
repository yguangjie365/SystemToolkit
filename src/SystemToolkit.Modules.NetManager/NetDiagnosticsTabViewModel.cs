using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Modules.NetManager;

/// <summary>诊断链单行 VM（步骤名固定，按行对位刷新——Running 快照与终态同名）。</summary>
public partial class DiagRowVm : ObservableObject
{
    public string Step { get; }

    public DiagRowVm(string step) => Step = step;

    [ObservableProperty]
    private DiagStatus _status = DiagStatus.Pending;

    [ObservableProperty]
    private string _detail = "待检测";

    [ObservableProperty]
    private long _elapsedMs;

    public string StatusText => Status switch
    {
        DiagStatus.Pending => "○ 待检测",
        DiagStatus.Running => "● 检测中",
        DiagStatus.Success => "✓ 正常",
        DiagStatus.Failed => "✗ 异常",
        DiagStatus.Skipped => "— 跳过",
        _ => Status.ToString(),
    };
}

/// <summary>
/// 「网络诊断」Tab：一键诊断链（5 步，Running 增量刷新，不短路）+ 结论横幅 +
/// 定向排查工具卡（MTU / 端口 / hosts / DNS 查询 / DNS 优选基准 / 持续 ping）。
/// </summary>
public partial class NetDiagnosticsTabViewModel : ObservableObject
{
    private static readonly string[] StepNames = ["适配器", "网关", "公网", "DNS 解析", "丢包量化"];

    private readonly INetDiagnosticService _diagnostic;
    private readonly DnsProbeService _dnsProbe;
    private readonly ContinuousPingService _continuousPing;
    private readonly Action<string> _log;

    public NetDiagnosticsTabViewModel(
        INetDiagnosticService diagnostic,
        DnsProbeService dnsProbe,
        ContinuousPingService continuousPing,
        Action<string> log)
    {
        _diagnostic = diagnostic;
        _dnsProbe = dnsProbe;
        _continuousPing = continuousPing;
        _log = log;
        foreach (string step in StepNames)
        {
            Steps.Add(new DiagRowVm(step));
        }
    }

    public ObservableCollection<DiagRowVm> Steps { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _conclusion = "";

    /// <summary>结论横幅是否绿色（含「网络正常」）。</summary>
    public bool IsConclusionGood => Conclusion.Contains("网络正常", StringComparison.Ordinal);

    /// <summary>结论横幅是否红色（故障定性）。</summary>
    public bool IsConclusionBad => Conclusion.Contains("故障", StringComparison.Ordinal)
        || Conclusion.Contains("断网", StringComparison.Ordinal);

    partial void OnConclusionChanged(string value)
    {
        OnPropertyChanged(nameof(IsConclusionGood));
        OnPropertyChanged(nameof(IsConclusionBad));
    }

    // ── 一键诊断 ──

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunDiagnosticsAsync() => await RunDiagnosticsSafeAsync().ConfigureAwait(true);

    private bool CanRun => !IsBusy;

    /// <summary>诊断执行（供组合根在安全修复后联动调用；幂等、有忙闸）。</summary>
    public async Task RunDiagnosticsSafeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Conclusion = "";
        foreach (DiagRowVm row in Steps)
        {
            row.Status = DiagStatus.Pending;
            row.Detail = "待检测";
            row.ElapsedMs = 0;
        }

        try
        {
            // Progress<T> 封送到 UI 线程（RunAsync 内部 ConfigureAwait(false)，报告可能来自线程池）
            var progress = new Progress<DiagStepResult>(r =>
            {
                DiagRowVm row = Steps.First(s => s.Step == r.Step);
                row.Status = r.Status;
                row.Detail = r.Detail;
                row.ElapsedMs = r.ElapsedMs;
            });

            IReadOnlyList<DiagStepResult> steps =
                await _diagnostic.RunWithProgressAsync(progress).ConfigureAwait(true);
            Conclusion = _diagnostic.Conclusion;
            _log($"[诊断] 完成：{Conclusion}（{steps.Count} 步）");
        }
        catch (Exception ex)
        {
            Conclusion = "诊断执行失败：" + ex.Message;
            _log("[诊断] ❌ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 定向排查：MTU ──

    [ObservableProperty]
    private string _mtuText = "尚未探测";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ProbeMtuAsync()
    {
        MtuText = "探测中…";
        try
        {
            MtuProbeResult? result = await _diagnostic.ProbePathMtuAsync().ConfigureAwait(true);
            MtuText = result is null
                ? "无法探测（全尺寸不可通过 / 无公网路径）"
                : $"路径 MTU {result.PathMtu}，建议网卡 MTU {result.SuggestedNicMtu}";
            _log("[诊断] MTU：" + MtuText);
        }
        catch (Exception ex)
        {
            MtuText = "探测失败：" + ex.Message;
        }
    }

    // ── 定向排查：端口 ──

    [ObservableProperty]
    private string _portHost = "";

    [ObservableProperty]
    private int _portNumber = 443;

    [ObservableProperty]
    private string _portResultText = "";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TestPortAsync()
    {
        string host = PortHost.Trim();
        if (host.Length == 0 || PortNumber is < 1 or > 65535)
        {
            PortResultText = "请填写目标主机与 1-65535 的端口";
            return;
        }

        PortResultText = "测试中…";
        try
        {
            TcpProbeResult result = await _diagnostic.TestPortAsync(host, PortNumber).ConfigureAwait(true);
            PortResultText = result.Success
                ? $"✓ 可达（延迟 {result.LatencyMs} ms）"
                : $"✗ {result.Error}";
            _log($"[诊断] 端口 {host}:{PortNumber} → {PortResultText}");
        }
        catch (Exception ex)
        {
            PortResultText = "测试失败：" + ex.Message;
        }
    }

    // ── 定向排查：hosts ──

    [ObservableProperty]
    private string _hostsResultText = "";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckHostsAsync()
    {
        HostsResultText = "检查中…";
        try
        {
            HostsCheckResult result = await _diagnostic.CheckHostsAsync().ConfigureAwait(true);
            int suspicious = result.Entries.Count(e => e.Suspicious);
            HostsResultText = result.Entries.Count == 0
                ? "hosts 文件无生效条目"
                : $"共 {result.Entries.Count} 条生效条目，{suspicious} 条需留意";
            _log($"[诊断] hosts：{HostsResultText}");
            foreach (HostsEntry entry in result.Entries.Where(e => e.Suspicious))
            {
                _log($"[诊断] ⚠️ {entry.Ip} → {string.Join(' ', entry.HostNames)}");
            }
        }
        catch (Exception ex)
        {
            HostsResultText = "检查失败：" + ex.Message;
        }
    }

    // ── 定向排查：DNS 查询 ──

    [ObservableProperty]
    private string _dnsServerInput = "223.5.5.5";

    [ObservableProperty]
    private string _dnsDomainInput = "www.baidu.com";

    [ObservableProperty]
    private string _dnsLookupResultText = "";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DnsLookupAsync()
    {
        DnsLookupResultText = "查询中…";
        try
        {
            DnsLookupResult result = await _dnsProbe.LookupAsync(
                DnsServerInput.Trim(), DnsDomainInput.Trim()).ConfigureAwait(true);
            DnsLookupResultText = result.Success
                ? $"✓ {result.AnswerAddress}（{result.LatencyMs} ms）"
                : $"✗ {result.Error}";
            _log($"[诊断] DNS {DnsServerInput.Trim()} 查询 {DnsDomainInput.Trim()} → {DnsLookupResultText}");
        }
        catch (Exception ex)
        {
            DnsLookupResultText = "查询失败：" + ex.Message;
        }
    }

    // ── DNS 优选基准 ──

    public IReadOnlyList<string> BenchmarkServers { get; } =
        DnsPresets.BuiltIn.Where(p => p.Primary is not null).Select(p => p.Primary!).ToList();

    public ObservableCollection<DnsServerStats> BenchmarkResults { get; } = new();

    [ObservableProperty]
    private bool _isBenchmarking;

    [ObservableProperty]
    private string _benchmarkStatusText = "";

    /// <summary>优选基准当前选中的服务器行（仅日志呈现明细，无行选择需求）。</summary>
    [RelayCommand(CanExecute = nameof(CanBenchmark))]
    private async Task RunBenchmarkAsync()
    {
        IsBenchmarking = true;
        BenchmarkResults.Clear();
        BenchmarkStatusText = "基准测试中…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var progress = new Progress<(string Server, int Done)>(p =>
                BenchmarkStatusText = $"基准测试中… {p.Server} 第 {p.Done}/{DnsProbeService.BenchmarkQueriesPerServer} 次");
            IReadOnlyList<DnsServerStats> results =
                await _dnsProbe.BenchmarkAsync(BenchmarkServers, progress, cts.Token).ConfigureAwait(true);
            foreach (DnsServerStats stat in results)
            {
                BenchmarkResults.Add(stat);
            }

            DnsServerStats? best = results
                .Where(s => s.AvgMs is not null)
                .OrderBy(s => s.AvgMs).FirstOrDefault();
            BenchmarkStatusText = best is null ? "全部服务器均不可达" : $"推荐：{best.Server}（平均 {best.AvgMs} ms）";
            _log("[诊断] DNS 优选基准完成：" + BenchmarkStatusText);
        }
        catch (Exception ex)
        {
            BenchmarkStatusText = "基准失败：" + ex.Message;
        }
        finally
        {
            IsBenchmarking = false;
        }
    }

    private bool CanBenchmark => !IsBenchmarking;

    // ── 持续 ping ──

    [ObservableProperty]
    private string _pingTarget = "223.5.5.5";

    [ObservableProperty]
    private bool _isPinging;

    [ObservableProperty]
    private string _pingStatsText = "";

    private CancellationTokenSource? _pingCts;

    [RelayCommand(CanExecute = nameof(CanStartPing))]
    private void StartPing()
    {
        string target = PingTarget.Trim();
        if (target.Length == 0)
        {
            return;
        }

        IsPinging = true;
        PingSent = 0;
        PingReceived = 0;
        PingStatsText = "开始持续探测…";
        _pingCts = new CancellationTokenSource();
        CancellationToken token = _pingCts.Token;
        var progress = new Progress<PingSample>(s =>
        {
            PingSent++;
            if (s.Success)
            {
                PingReceived++;
            }

            PingStatsText = $"已发 {PingSent} 包，成功 {PingReceived}，丢包 {PingSent - PingReceived}"
                + (s.Success ? $"，最新 {s.LatencyMs} ms" : $"，最新失败（{s.Error}）");
        });
        _log($"[诊断] 开始持续 ping {target}（间隔 1s，点「停止」结束）");
        _ = RunPingLoopAsync(target, progress, token);
    }

    private bool CanStartPing => !IsPinging;

    private async Task RunPingLoopAsync(string target, IProgress<PingSample> progress, CancellationToken token)
    {
        try
        {
            await _continuousPing.RunAsync(target, intervalMs: 1000, progress, token).ConfigureAwait(true);
            _log($"[诊断] 持续 ping 结束：{PingStatsText}");
        }
        catch (Exception ex)
        {
            _log("[诊断] ❌ 持续 ping 异常：" + ex.Message);
        }
        finally
        {
            IsPinging = false;
            _pingCts?.Dispose();
            _pingCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopPing))]
    private void StopPing()
    {
        _pingCts?.Cancel();
        _log("[诊断] 正在停止持续 ping…");
    }

    /// <summary>审查 O7（2026-09-10）：切页/关窗时取消持续 ping，防循环与 VM 常驻泄漏。</summary>
    public void CancelPing() => _pingCts?.Cancel();

    private bool CanStopPing => IsPinging;

    partial void OnIsPingingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartPing));
        OnPropertyChanged(nameof(CanStopPing));
        StartPingCommand.NotifyCanExecuteChanged();
        StopPingCommand.NotifyCanExecuteChanged();
    }

    private int _pingSent;
    private int _pingReceived;

    private int PingSent
    {
        get => _pingSent;
        set => _pingSent = value;
    }

    private int PingReceived
    {
        get => _pingReceived;
        set => _pingReceived = value;
    }
}
