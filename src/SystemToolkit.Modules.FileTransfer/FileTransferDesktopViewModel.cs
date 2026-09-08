using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>局域网已发现设备行投影。</summary>
public sealed class DiscoveredDeviceRowVm
{
    public DiscoveredDevice Model { get; }

    public DiscoveredDeviceRowVm(DiscoveredDevice model) => Model = model;

    public string Name => Model.Name;

    public string EndpointText => $"{Model.IPAddress}:{Model.TransferPort}";

    public string OnlineText => Model.IsOnline ? "在线" : "离线";
}

/// <summary>已知设备（记忆的常用对端）行 VM，可编辑并持久化。</summary>
public partial class KnownPeerRowVm : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _ip = "";

    [ObservableProperty]
    private int _port = 18889;
}

/// <summary>传输任务行投影：定时刷新速度/进度投影（TransferTask 为普通 CLR 属性）。</summary>
public sealed class TransferTaskRowVm : ObservableObject
{
    public TransferTask Model { get; }

    public TransferTaskRowVm(TransferTask model) => Model = model;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DirectionText));
    }

    public string FileName => Model.FileName;

    public string PeerEndpoint => Model.PeerEndpoint;

    public double Progress => Model.Progress;

    public string SpeedText => Model.EstimatedRemaining is null
        ? Model.SpeedText
        : $"{Model.SpeedText} · 剩余 {Model.EstimatedRemaining.Value:hh\\:mm\\:ss}";

    public string StatusText => Model.Status switch
    {
        TransferStatus.Pending => "排队中",
        TransferStatus.Negotiating => "等待确认",
        TransferStatus.Transferring => "传输中",
        TransferStatus.Paused => "已暂停",
        TransferStatus.Completed => "已完成",
        TransferStatus.Failed => "失败",
        TransferStatus.Cancelled => "已取消",
        _ => Model.Status.ToString(),
    };

    public string DirectionText => Model.Direction == TransferDirection.Send ? "↑ 发送" : "↓ 接收";

    public bool IsActive => Model.Status is TransferStatus.Pending or TransferStatus.Negotiating or TransferStatus.Transferring;
}

/// <summary>
/// 「电脑互传」Tab：传输服务启停、设备发现、已知设备、发送（多选/拖拽）、
/// 活跃任务列表（确认门弹窗 / 取消）、传输历史。配置持久化于
/// %LOCALAPPDATA%\SystemToolkit\net\filetransfer.json（原子写）。
/// </summary>
public partial class FileTransferDesktopViewModel : ObservableObject
{
    private static readonly string ConfigPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "net", "filetransfer.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly IDeviceDiscoveryService _discovery;
    private readonly FileTransferService _transfer;
    private readonly TransferHistoryService _history;
    private readonly Action<string> _log;
    private readonly ILogger _logger;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;

    public FileTransferDesktopViewModel(
        IDeviceDiscoveryService discovery,
        FileTransferService transfer,
        TransferHistoryService history,
        Action<string> log,
        ILogger logger,
        System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _discovery = discovery;
        _transfer = transfer;
        _history = history;
        _log = log;
        _logger = logger;
        _dispatcher = dispatcher;

        _transfer.TaskUpdated += OnTaskUpdated;
        _transfer.TaskCompleted += OnTaskCompleted;
        _transfer.TransferRequested += OnTransferRequested;
        _discovery.DeviceChanged += OnDeviceChanged;
    }

    /// <summary>
    /// 后台事件 → UI 线程编组（审查 🔴-1 采纳：替换 Application.Current?.Dispatcher.Invoke——
    /// 同步 Invoke 有死锁风险、Application 为 null 时静默跳过；与 MusicManager 统一为
    /// 「显式 Dispatcher 注入 + 死线程检测 + BeginInvoke」模式）。测试传 null 直执行。
    /// </summary>
    private void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? d = _dispatcher;
        if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive)
        {
            RunGuarded(action);
        }
        else if (d.CheckAccess())
        {
            RunGuarded(action);
        }
        else
        {
            d.BeginInvoke(() => RunGuarded(action));
        }
    }

    /// <summary>处理器异常不得反噬服务回调线程：落可见日志（🔴 不静默）。</summary>
    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log($"[互传] ⚠️ 界面更新异常：{ex.Message}");
            _logger.Error("[互传] UI 事件处理器异常", ex);
        }
    }

    /// <summary>确认对话框回调（由组合根转接）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    /// <summary>文件选择对话框回调（View 注入；返回 null 表示用户取消）。</summary>
    public Func<IReadOnlyList<string>?>? PickFiles { get; set; }

    /// <summary>当前发送目标（设备行 / 已知设备行二选一，发送时由 View 落定）。</summary>
    public (string Ip, int Port)? PendingSendTarget { get; set; }

    // ── 服务与配置 ──
    [ObservableProperty]
    private bool _isTransferRunning;

    [ObservableProperty]
    private string _deviceId = "";

    [ObservableProperty]
    private string _receiveDirectory = "";

    [ObservableProperty]
    private bool _requireKnownPeer = true;

    [ObservableProperty]
    private bool _requireReceiveConfirmation = true;

    public string TransferPortText => _transferPort > 0 ? _transferPort.ToString() : "—";

    [ObservableProperty]
    private bool _initialized;

    private int _transferPort;

    // ── 设备 ──
    public ObservableCollection<DiscoveredDeviceRowVm> DiscoveredDevices { get; } = new();

    [ObservableProperty]
    private DiscoveredDeviceRowVm? _selectedDevice;

    public ObservableCollection<KnownPeerRowVm> KnownPeers { get; } = new();

    [ObservableProperty]
    private KnownPeerRowVm? _selectedKnownPeer;

    // ── 任务 ──
    public ObservableCollection<TransferTaskRowVm> ActiveTasks { get; } = new();

    [ObservableProperty]
    private TransferTaskRowVm? _selectedTask;

    // ── 历史 ──
    public ObservableCollection<TransferHistoryEntry> HistoryEntries { get; } = new();

    private bool _busy;

    /// <summary>页面 Loaded：读配置、注册事件（幂等）。</summary>
    public void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;
        LoadConfig();
        ReloadHistory();
        _log($"[互传] 本机设备 ID：{_discovery.LocalDeviceId}");
    }

    // ── 服务启停 ──

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task StartTransferAsync()
    {
        _busy = true;
        RefreshToggleCanExecute();
        try
        {
            var settings = new TransferSettings
            {
                TransferPort = 18889,
                ReceiveDirectory = ReceiveDirectory,
                RequireKnownPeer = RequireKnownPeer,
                RequireReceiveConfirmation = RequireReceiveConfirmation,
            };
            // 审查 🟠-3 采纳（2026-09-09）：两步启动分别标注阶段——部分失败时用户能看出
            // 是传输服务还是设备发现服务没起来（原实现只有一条笼统异常）
            try
            {
                await _transfer.StartAsync(settings).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log($"[互传] ❌ 传输服务启动失败（TCP 18889 可能被占用）：{ex.Message}");
                _logger.Error("传输服务启动失败", ex);
                throw;
            }

            _transferPort = 18889;
            OnPropertyChanged(nameof(TransferPortText));
            try
            {
                var discoverySettings = new TransferSettings
                {
                    DiscoveryPort = 18888,
                    TransferPort = 18889,
                    HeartbeatInterval = TimeSpan.FromSeconds(3),
                    OfflineTimeout = TimeSpan.FromSeconds(10),
                };
                await _discovery.StartAsync(discoverySettings).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log($"[互传] ❌ 设备发现服务启动失败（UDP 18888 可能被占用）：{ex.Message}");
                _logger.Error("设备发现服务启动失败", ex);

                // 传输服务已起来但发现服务没起来：不回滚会留下"停止按钮不可用、服务却在跑"的僵局
                // （审查 🟠-3 的状态不一致）——这里做清理属于防御，不是新功能
                try
                {
                    await _transfer.StopAsync().ConfigureAwait(true);
                    _log("[互传] 已回滚停止传输服务（设备发现启动失败）");
                }
                catch (Exception stopEx)
                {
                    _logger.Warn($"[互传] 回滚停止传输服务失败：{stopEx.Message}");
                }

                throw;
            }

            IsTransferRunning = true;
            _log("[互传] ✅ 传输与设备发现服务已启动（TCP 18889 / UDP 18888）");
            _logger.Info("互传服务启动");
        }
        catch (Exception ex)
        {
            _log("[互传] ❌ 服务启动失败：" + ex.Message + "（端口被占用？）");
            _logger.Error("互传服务启动失败", ex);
        }
        finally
        {
            _busy = false;
            RefreshToggleCanExecute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task StopTransferAsync()
    {
        _busy = true;
        RefreshToggleCanExecute();
        try
        {
            await _discovery.StopAsync().ConfigureAwait(true);
            await _transfer.StopAsync().ConfigureAwait(true);
            DiscoveredDevices.Clear();
            ActiveTasks.Clear();
            IsTransferRunning = false;
            _log("[互传] 服务已停止");
        }
        finally
        {
            _busy = false;
            RefreshToggleCanExecute();
        }
    }

    private bool CanToggle => !_busy;

    private void RefreshToggleCanExecute()
    {
        StartTransferCommand.NotifyCanExecuteChanged();
        StopTransferCommand.NotifyCanExecuteChanged();
    }

    // ── 配置持久化 ──

    private void LoadConfig()
    {
        try
        {
            DeviceId = _discovery.LocalDeviceId;
            if (File.Exists(ConfigPath))
            {
                ModuleConfig? config = JsonSerializer.Deserialize<ModuleConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (config is not null)
                {
                    ReceiveDirectory = config.ReceiveDirectory ?? "";
                    RequireKnownPeer = config.RequireKnownPeer;
                    RequireReceiveConfirmation = config.RequireReceiveConfirmation;
                    KnownPeers.Clear();
                    foreach (KnownPeerEntry peer in config.KnownPeers)
                    {
                        KnownPeers.Add(new KnownPeerRowVm { Name = peer.Name, Ip = peer.Ip, Port = peer.Port });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log("[互传] ⚠️ 配置读取失败（使用默认值）：" + ex.Message);
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            var config = new ModuleConfig
            {
                ReceiveDirectory = ReceiveDirectory,
                RequireKnownPeer = RequireKnownPeer,
                RequireReceiveConfirmation = RequireReceiveConfirmation,
                KnownPeers = KnownPeers.Select(p => new KnownPeerEntry(p.Name, p.Ip, p.Port)).ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOpts));
            _log("[互传] 配置已保存");
        }
        catch (Exception ex)
        {
            _log("[互传] ❌ 配置保存失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void AddKnownPeer()
    {
        // 带默认名并自动选中（审查 🔴-2 采纳）：避免完全空白行漏填漏存
        var row = new KnownPeerRowVm { Name = "新设备" };
        KnownPeers.Add(row);
        SelectedKnownPeer = row;
        _log("[互传] 已添加「新设备」并选中（填写 IP 后点保存）");
    }

    [RelayCommand]
    private void RemoveKnownPeer()
    {
        if (SelectedKnownPeer is not null)
        {
            KnownPeers.Remove(SelectedKnownPeer);
            _log($"[互传] 已移除已知设备：{SelectedKnownPeer.Name}");
        }
    }

    [RelayCommand]
    private void AddDiscoveredToKnown()
    {
        if (SelectedDevice is null)
        {
            _log("[互传] ⚠️ 请先选择要加入的设备");
            return;
        }

        string ip = SelectedDevice.Model.IPAddress.ToString();
        KnownPeerRowVm? existing = KnownPeers.FirstOrDefault(p => p.Ip == ip);
        if (existing is not null)
        {
            _log($"[互传] ⚠️ 该设备已在已知列表中（{existing.Name}）");
            return;
        }

        KnownPeers.Add(new KnownPeerRowVm
        {
            Name = SelectedDevice.Model.Name,
            Ip = ip,
            Port = SelectedDevice.Model.TransferPort,
        });
        _log($"[互传] 已将 {SelectedDevice.Model.Name} 加入已知设备（记得点保存）");
    }

    // ── 设备发现 ──

    private void OnDeviceChanged(object? sender, DeviceChangeEventArgs e)
    {
        // 事件来自 UDP 回调线程 → 封送 UI 线程
        RunOnUi(() =>
        {
            DiscoveredDevices.Clear();
            foreach (DiscoveredDevice device in _discovery.Devices.OrderByDescending(d => d.IsOnline).ThenBy(d => d.Name))
            {
                DiscoveredDevices.Add(new DiscoveredDeviceRowVm(device));
            }

            if (e.ChangeType == DeviceChangeType.Discovered)
            {
                _log($"[互传] 发现设备：{e.Device.Name}（{e.Device.IPAddress}）");
            }
        });
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (IsTransferRunning)
        {
            DiscoveredDevices.Clear();
            foreach (DiscoveredDevice device in _discovery.Devices.OrderByDescending(d => d.IsOnline).ThenBy(d => d.Name))
            {
                DiscoveredDevices.Add(new DiscoveredDeviceRowVm(device));
            }

            _log($"[互传] 已刷新：{_discovery.Devices.Count} 台在线设备");
        }
        else
        {
            _log("[互传] ⚠️ 传输服务未启动——请先启动服务（同时开启设备发现）");
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    // ── 发送 ──

    /// <summary>View 调用：向指定设备发送所选文件（对话框多选 / 拖拽统一入口）。</summary>
    public async Task SendFilesToAsync(string ip, int port, IReadOnlyList<string> filePaths)
    {
        if (!IsTransferRunning)
        {
            _log("[互传] ⚠️ 传输服务未启动，请先启动服务");
            return;
        }

        foreach (string file in filePaths)
        {
            try
            {
                TransferTask task = await _transfer.SendFileAsync(file, ip, port).ConfigureAwait(true);
                _log($"[互传] 入队发送：{task.FileName} → {ip}:{port}");
            }
            catch (Exception ex)
            {
                _log($"[互传] ❌ 发送失败（{Path.GetFileName(file)}）：" + ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task SendToSelectedDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            _log("[互传] ⚠️ 请先选择目标设备");
            return;
        }

        IReadOnlyList<string>? files = PickFiles?.Invoke();
        if (files is not { Count: > 0 })
        {
            return;
        }

        await SendFilesToAsync(SelectedDevice.Model.IPAddress.ToString(), SelectedDevice.Model.TransferPort, files).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SendToKnownPeerAsync()
    {
        if (SelectedKnownPeer is null)
        {
            _log("[互传] ⚠️ 请先选择已知设备");
            return;
        }

        if (!System.Net.IPAddress.TryParse(SelectedKnownPeer.Ip, out _)
            || SelectedKnownPeer.Port is < 1 or > 65535)
        {
            _log("[互传] ⚠️ 已知设备的 IP/端口不合法");
            return;
        }

        IReadOnlyList<string>? files = PickFiles?.Invoke();
        if (files is not { Count: > 0 })
        {
            return;
        }

        await SendFilesToAsync(SelectedKnownPeer.Ip, SelectedKnownPeer.Port, files).ConfigureAwait(true);
    }

    // ── 任务 ──

    private void OnTaskUpdated(object? sender, TransferTask task)
    {
        RunOnUi(() =>
        {
            TransferTaskRowVm? row = ActiveTasks.FirstOrDefault(r => r.Model.Id == task.Id);
            if (row is null)
            {
                row = new TransferTaskRowVm(task);
                ActiveTasks.Insert(0, row);
            }
            else
            {
                row.Refresh();
            }

            if (row.IsActive)
            {
                EnsureTaskRefreshTimer();
            }
        });
    }

    private void OnTaskCompleted(object? sender, TransferTask task)
    {
        RunOnUi(() =>
        {
            TransferTaskRowVm? row = ActiveTasks.FirstOrDefault(r => r.Model.Id == task.Id);
            if (row is not null)
            {
                row.Refresh();
                ActiveTasks.Remove(row);
            }

            string direction = task.Direction == TransferDirection.Send ? "发送" : "接收";
            _log(task.Status switch
            {
                TransferStatus.Completed => $"[互传] ✅ {direction}完成：{task.FileName}",
                TransferStatus.Cancelled => $"[互传] 已取消：{task.FileName}",
                _ => $"[互传] ❌ {direction}失败：{task.FileName}（{task.ErrorMessage}）",
            });

            _history.Append(new TransferHistoryEntry
            {
                TaskId = task.Id,
                FileName = task.FileName,
                FileSize = task.FileSize,
                Direction = task.Direction,
                PeerEndpoint = task.PeerEndpoint,
                Status = task.Status,
                StartedAt = task.StartedAt,
                FinishedAt = task.FinishedAt ?? DateTimeOffset.UtcNow,
                TransferredBytes = task.TransferredBytes,
            });
            ReloadHistory();
        });
    }

    private void EnsureTaskRefreshTimer()
    {
        if (_taskTimer is not null)
        {
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        timer.Tick += (_, _) =>
        {
            foreach (TransferTaskRowVm row in ActiveTasks)
            {
                row.Refresh();
            }

            if (ActiveTasks.All(r => !r.IsActive))
            {
                timer.Stop();
                _taskTimer = null;
            }
        };
        timer.Start();
        _taskTimer = timer;
    }

    private System.Windows.Threading.DispatcherTimer? _taskTimer;

    [RelayCommand]
    private async Task CancelTaskAsync(TransferTaskRowVm? row)
    {
        TransferTaskRowVm? target = row ?? SelectedTask;
        if (target is null)
        {
            _log("[互传] ⚠️ 请先选择要取消的任务"); // 🔴 不静默（审查 🟠-5 采纳）
            return;
        }

        // 审查 🔴 采纳（2026-09-09）：任务不存在/网络错误会抛——原实现无 catch，
        // 异常被 AsyncRelayCommand 吞掉，用户点了取消却看不到任何结果
        try
        {
            await _transfer.CancelAsync(target.Model.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log($"[互传] ❌ 取消任务失败：{ex.Message}");
            _logger.Error($"取消传输任务失败（{target.Model.Id}）", ex);
        }
    }

    // ── 接收确认门 ──

    private void OnTransferRequested(object? sender, TransferRequestEventArgs e)
    {
        // 事件来自 WatsonTcp 回调线程：确认弹窗必须在 UI 线程
        RunOnUi(() =>
        {
            bool accept = ConfirmRequest?.Invoke(
                "接收文件请求",
                $"{e.PeerEndpoint} 想向你发送文件：\n\n「{e.FileName}」（{e.FileSize:N0} 字节）\n\n接受吗？"
                + "\n\n（不做任何响应则自动超时拒绝）") == true;
            // 审查 🟠-2 采纳（2026-09-09）：fire-and-forget 的响应失败原本完全无痕——
            // 断网时用户点了「接受」但对面毫无反应，排查无从下手；补日志落地
            _ = _transfer.RespondTransferAsync(e.TaskId, accept)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        string reason = t.Exception?.InnerException?.Message ?? "未知原因";
                        _log($"[互传] ⚠️ 发送接收响应失败：{reason}");
                        _logger.Warn($"[互传] 接收响应发送失败（task={e.TaskId}）：{reason}");
                    }
                }, TaskScheduler.Default);
        });
    }

    // ── 历史 ──

    private void ReloadHistory()
    {
        HistoryEntries.Clear();
        foreach (TransferHistoryEntry entry in _history.Load())
        {
            HistoryEntries.Add(entry);
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (ConfirmRequest?.Invoke("清空传输历史", $"确定清空全部 {HistoryEntries.Count} 条历史记录吗？此操作不可撤销。") != true)
        {
            return;
        }

        _history.Clear();
        ReloadHistory();
        _log("[互传] 传输历史已清空");
    }

    /// <summary>模块配置（JSON 持久化）。</summary>
    private sealed class ModuleConfig
    {
        public string? ReceiveDirectory { get; set; }

        public bool RequireKnownPeer { get; set; } = true;

        public bool RequireReceiveConfirmation { get; set; } = true;

        public List<KnownPeerEntry> KnownPeers { get; set; } = new();
    }

    private sealed record KnownPeerEntry(string Name, string Ip, int Port);
}
