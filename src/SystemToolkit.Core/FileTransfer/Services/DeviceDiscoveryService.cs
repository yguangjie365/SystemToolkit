using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services.Protocol;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 设备发现服务实现：基于 UDP 广播在局域网内发现同样运行本程序的设备。
/// <para>
/// 定时心跳广播 <see cref="DeviceAnnouncement"/>，同时监听广播端口；收到合法包即更新设备列表
/// （新设备 → <see cref="DeviceChangeType.Discovered"/>，已有设备 → <see cref="DeviceChangeType.Updated"/>），
/// 超过 <see cref="TransferSettings.OfflineTimeout"/> 未收到心跳则标记离线并移除。
/// </para>
/// <para>线程安全：设备列表以 <see cref="ConcurrentDictionary{TKey,TValue}"/> 存储，可被多线程并发读取。</para>
/// </summary>
public sealed class DeviceDiscoveryService : IDeviceDiscoveryService, IDisposable
{
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    private UdpClient? _broadcaster;
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _broadcastLoop;
    private Task? _listenLoop;
    private Task? _pruneLoop;

    private int _discoveryPort;
    private int _transferPort;
    private int _webPort;
    private string _deviceName = System.Environment.MachineName;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(3);
    private TimeSpan _offlineTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 本机设备 ID（格式 <c>{机器名}-{8位十六进制}</c>）。后 8 位持久化于
    /// <c>%LOCALAPPDATA%\SystemToolkit\net\device.id</c>——2026-09-06 起跨重启稳定
    /// （旧版含进程号，重启即换身份，对端无法长期记忆设备）。
    /// </summary>
    public string LocalDeviceId { get; }

    /// <inheritdoc/>
    public IReadOnlyList<DiscoveredDevice> Devices => _devices.Values.ToArray();

    /// <inheritdoc/>
    public event EventHandler<DeviceChangeEventArgs>? DeviceChanged;

    /// <summary>
    /// 构造设备发现服务（logger 可选，缺省静默；<paramref name="deviceId"/> 可覆盖本机 ID
    /// ——同机多实例测试必须显式传不同值，否则持久化 ID 相同会被彼此当作自身广播过滤）。
    /// </summary>
    public DeviceDiscoveryService(ILogger? logger = null, string? deviceId = null)
    {
        _logger = logger ?? NullLogger.Instance;
        LocalDeviceId = deviceId ?? $"{System.Environment.MachineName}-{GetOrCreatePersistedToken()}";
    }

    /// <summary>读取（或首次创建并持久化）本机 8 位十六进制身份令牌；落盘失败退化为进程级临时身份（旧版语义）。</summary>
    private static string GetOrCreatePersistedToken()
    {
        try
        {
            string path = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SystemToolkit", "net", "device.id");
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length == 8 && existing.All(char.IsAsciiHexDigit))
                    return existing;
            }

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, token);
            return token;
        }
        catch (Exception)
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(TransferSettings settings, CancellationToken ct = default)
    {
        if (_cts is not null)
            throw new InvalidOperationException("设备发现服务已在运行，请先调用 StopAsync。");

        _discoveryPort = settings.DiscoveryPort;
        _transferPort = settings.TransferPort;
        _webPort = settings.WebPort;
        _deviceName = string.IsNullOrWhiteSpace(settings.DeviceName) ? System.Environment.MachineName : settings.DeviceName;
        _heartbeatInterval = settings.HeartbeatInterval;
        _offlineTimeout = settings.OfflineTimeout;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = _cts.Token;
        UdpClient? broadcaster = null;
        UdpClient? listener = null;

        try
        {
            // 广播端：任意可用端口，开启广播选项后发往 DiscoveryPort
            broadcaster = new UdpClient();
            broadcaster.EnableBroadcast = true;
            _broadcaster = broadcaster;

            // 监听端：绑定 DiscoveryPort 接收广播，允许地址复用以便多实例共存
            listener = new UdpClient();
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _listener = listener;

            _broadcastLoop = BroadcastLoopAsync(token);
            _listenLoop = ListenLoopAsync(token);
            _pruneLoop = PruneLoopAsync(token);

            // 启动时立即广播一次，缩短首次被发现延迟
            await BroadcastOnceAsync(token).ConfigureAwait(false);
        }
        catch
        {
            // 中途失败（如 Bind 端口被占用）：释放已建资源并清空 _cts，避免服务变砖无法重启
            try
            { broadcaster?.Dispose(); }
            catch { }
            try
            { listener?.Dispose(); }
            catch { }
            try
            { await _cts.CancelAsync(); }
            catch { }
            _cts.Dispose();
            _cts = null;
            _broadcaster = null;
            _listener = null;
            _broadcastLoop = null;
            _listenLoop = null;
            _pruneLoop = null;
            throw;
        }

        _logger.Info($"设备发现已启动（UDP {_discoveryPort}，心跳 {_heartbeatInterval.TotalSeconds:F0}s，" +
                     $"离线阈值 {_offlineTimeout.TotalSeconds:F0}s，本机 ID {LocalDeviceId}）。");
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        if (cts is null)
            return;

        await cts.CancelAsync().ConfigureAwait(false);

        // 先关闭 socket 以解除 ReceiveAsync 的阻塞
        _broadcaster?.Dispose();
        _listener?.Dispose();

        await SafeAwait(_broadcastLoop).ConfigureAwait(false);
        await SafeAwait(_listenLoop).ConfigureAwait(false);
        await SafeAwait(_pruneLoop).ConfigureAwait(false);

        cts.Dispose();
        _broadcaster = null;
        _listener = null;
        _cts = null;
        _broadcastLoop = null;
        _listenLoop = null;
        _pruneLoop = null;

        _logger.Info($"设备发现已停止（清除了 {_devices.Count} 台设备）。");
        _devices.Clear();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 同步释放（宿主 <c>ServiceProvider.Dispose()</c> 走这条路径）。
    /// <para>
    /// 🔴 必须同时实现 <see cref="IDisposable"/>：只实现 <see cref="IAsyncDisposable"/> 的服务，
    /// 会让同步 <c>Dispose()</c> 抛 <c>InvalidOperationException: type only implements IAsyncDisposable</c>，
    /// 在退出路径上表现为「关闭程序即崩溃」（2026-09-06 实测，同款问题见 FileWebServer）。
    /// </para>
    /// <para>退出时当前线程可能是 UI 线程，故 Task.Run + 2s 有界等待，超时放弃——进程即将结束，由 OS 回收。</para>
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(2)))
            {
                _logger.Warn("[DeviceDiscoveryService] 同步停止超时（2s），进程退出时由 OS 回收资源。");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[DeviceDiscoveryService] 同步停止失败（进程即将退出，忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 心跳广播循环：按 <see cref="TransferSettings.HeartbeatInterval"/> 周期发送设备宣告包。
    /// </summary>
    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        // 启动先立即发一次，缩短首发现延迟
        try
        { await BroadcastOnceAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warn($"首次心跳广播失败（网络接口未就绪？）：{ex.Message}，继续按周期重试。");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            { await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try
            { await BroadcastOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { /* 网卡切换/无广播接口，下个周期重试 */ }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.Warn($"心跳广播异常：{ex.Message}，继续按周期重试。");
            }
        }
    }

    /// <summary>
    /// 发送一次设备宣告包到局域网广播地址。
    /// </summary>
    private async Task BroadcastOnceAsync(CancellationToken ct)
    {
        UdpClient? broadcaster = _broadcaster;
        if (broadcaster is null)
            return;

        var announce = new DeviceAnnouncement
        {
            Did = LocalDeviceId,
            Dn = _deviceName,
            Tp = _transferPort,
            Wp = _webPort,
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(announce);
        await broadcaster
            .SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, _discoveryPort))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 监听循环：接收 UDP 广播包，解析 DeviceAnnouncement 并更新设备列表。
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        UdpClient listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await listener.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            ProcessDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    /// <summary>
    /// 解析单个广播数据报并更新设备列表。
    /// </summary>
    private void ProcessDatagram(byte[] buffer, IPEndPoint remoteEp)
    {
        DeviceAnnouncement? announce;
        try
        {
            announce = JsonSerializer.Deserialize<DeviceAnnouncement>(buffer.AsSpan());
        }
        catch (JsonException) { return; }

        if (announce is null || !announce.IsValid)
            return;
        // 过滤自身广播，避免把自己加入设备列表
        if (string.Equals(announce.Did, LocalDeviceId, StringComparison.Ordinal))
            return;

        var device = new DiscoveredDevice
        {
            DeviceId = announce.Did,
            Name = string.IsNullOrWhiteSpace(announce.Dn) ? announce.Did : announce.Dn,
            IPAddress = remoteEp.Address,
            TransferPort = announce.Tp,
            WebPort = announce.Wp,
            // 使用本机接收时间作为 LastSeen，规避跨机时钟漂移导致的离线误判
            LastSeen = DateTimeOffset.UtcNow,
            // 同步离线阈值，使 IsOnline 与 PruneLoop 判定口径一致
            OfflineTimeout = _offlineTimeout,
        };

        bool added = _devices.TryAdd(device.DeviceId, device);
        DeviceChangeType changeType;
        DiscoveredDevice reportedDevice;

        if (added)
        {
            changeType = DeviceChangeType.Discovered;
            reportedDevice = device;
            _logger.Info($"发现新设备：{device.Name}（{device.DisplayAddress}，ID {device.DeviceId}）。");
        }
        else
        {
            // REVIEW-3 G-3：整体替换而非原位改属性——UDP 线程原位写多字段（LastSeen/OfflineTimeout
            // 等，非原子）与 UI/握手线程并发读会撕裂。ConcurrentDictionary 索引器替换是原子操作，
            // 消费方经 Devices 快照与 DeviceChanged 事件拿到的是完整一致的新实例
            //（UI 侧 FileTransferDesktopViewModel 本就按事件整体重建 RowVm，无旧引用滞留）。
            _devices[device.DeviceId] = device;
            changeType = DeviceChangeType.Updated;
            reportedDevice = device;
        }

        DeviceChanged?.Invoke(this, new DeviceChangeEventArgs
        {
            Device = reportedDevice,
            ChangeType = changeType,
        });
    }

    /// <summary>
    /// 清理循环：定期剔除心跳超时的离线设备并发出 Offline 事件。
    /// </summary>
    private async Task PruneLoopAsync(CancellationToken ct)
    {
        // 扫描周期取心跳间隔的一半，保证离线判定及时（最少 1 秒）
        TimeSpan sweepInterval = _heartbeatInterval > TimeSpan.FromSeconds(2)
            ? _heartbeatInterval / 2
            : TimeSpan.FromSeconds(1);

        using var timer = new PeriodicTimer(sweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (string key in _devices.Keys)
                {
                    if (!_devices.TryGetValue(key, out DiscoveredDevice? dev))
                        continue;
                    if (now - dev.LastSeen >= _offlineTimeout)
                    {
                        if (_devices.TryRemove(key, out DiscoveredDevice? removed))
                        {
                            _logger.Info($"设备离线：{removed.Name}（{removed.DisplayAddress}）——超过 {_offlineTimeout.TotalSeconds:F0}s 未收到心跳。");
                            DeviceChanged?.Invoke(this, new DeviceChangeEventArgs
                            {
                                Device = removed,
                                ChangeType = DeviceChangeType.Offline,
                            });
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task SafeAwait(Task? task)
    {
        if (task is null)
            return;
        try
        { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
}
