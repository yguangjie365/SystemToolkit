using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Network.LanScan;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 网络管理页组合根 VM：五个 Tab（设置 / 诊断 / 修复 / 优化 / 局域网扫描）+ 模块级共享操作日志。
/// 子页业务在各自 ViewModel；本类负责依赖编排与跨页联动（修复后自动复诊断、局域网行 Ping 跳诊断页）。
/// </summary>
public partial class NetManagerViewModel : ObservableObject
{
    /// <summary>确认对话框回调（View 注入；GameManager 同款模式）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    public NetSettingsTabViewModel Settings { get; }

    public NetDiagnosticsTabViewModel Diagnostics { get; }

    public NetRepairTabViewModel Repair { get; }

    public NetOptimizeTabViewModel Optimize { get; }

    public LanScanTabViewModel Lan { get; }

    public ObservableCollection<LogLine> LogLines { get; } = new();

    /// <summary>当前 Tab（0=设置 1=诊断 2=修复 3=优化 4=局域网扫描）。</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>切回「网络诊断」Tab 的请求（View 注入：RadioButton 程序勾选，走既有 Checked 通道）。</summary>
    public Action? SwitchToDiagnosticsTabRequest { get; set; }

    public NetManagerViewModel(
        INetworkInfoService infoService,
        INetConfigService configService,
        INetworkSnapshotService snapshotService,
        INetDiagnosticService diagnosticService,
        DnsProbeService dnsProbe,
        ContinuousPingService continuousPing,
        INetRepairService repairService,
        ITcpTuningService tuningService,
        IElevationProvider elevation,
        LanScanService lanScan,
        ILogger? logger = null)
    {
        ILogger effectiveLogger = logger ?? NullLogger.Instance;
        void Log(string message) => AddLog(message);

        Settings = new NetSettingsTabViewModel(infoService, configService, snapshotService, Log);
        Diagnostics = new NetDiagnosticsTabViewModel(diagnosticService, dnsProbe, continuousPing, Log);
        Repair = new NetRepairTabViewModel(repairService, diagnosticService, Log);
        Optimize = new NetOptimizeTabViewModel(tuningService, Log);
        Lan = new LanScanTabViewModel(infoService, lanScan, Log);

        // 跨页联动：一键安全修复成功后自动复诊断（老 UI 行为，用户已习惯）
        Repair.SafeSequenceCompleted += () =>
        {
            Log("[联动] 安全修复完成，自动复诊断……");
            _ = Diagnostics.RunDiagnosticsSafeAsync();
        };

        // 跨页联动（NET-6 示意图 5️⃣）：局域网行「Ping」→ 目标写入诊断页持续 ping 并切 Tab
        Lan.PingRequested += ip =>
        {
            Diagnostics.PingTarget = ip;
            Log($"[联动] 已把 {ip} 填入持续 ping 目标");
            SwitchToDiagnosticsTabRequest?.Invoke();
        };

        Log($"网络管理模块已加载（提权运行：{(elevation.IsElevated ? "是" : "否——写操作执行时按需弹出 UAC）")}");
    }

    public void AddLog(string message) => LogFeed.Append(LogLines, message, LogFeed.DefaultMaxLines);

    [RelayCommand]
    private void ClearLog() => LogFeed.Clear(LogLines);

    /// <summary>页面 Loaded：按需加载各 Tab 首屏数据（幂等）。</summary>
    public async Task LoadAsync()
    {
        await Settings.LoadAsync().ConfigureAwait(true);
        await Optimize.LoadAsync().ConfigureAwait(true);
        await Lan.LoadAsync().ConfigureAwait(true);
    }
}
