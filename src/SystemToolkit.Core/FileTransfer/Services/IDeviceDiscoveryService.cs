using SystemToolkit.Core.FileTransfer.Models;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 设备发现服务：基于 UDP 广播在局域网内发现同样运行本程序的设备。
/// </summary>
public interface IDeviceDiscoveryService : IAsyncDisposable
{
    /// <summary>当前已发现的设备列表（快照）。</summary>
    IReadOnlyList<DiscoveredDevice> Devices { get; }

    /// <summary>设备列表变化时触发（上线/离线/更新）。</summary>
    event EventHandler<DeviceChangeEventArgs>? DeviceChanged;

    /// <summary>本机设备 ID。</summary>
    string LocalDeviceId { get; }

    /// <summary>启动广播与监听。</summary>
    Task StartAsync(TransferSettings settings, CancellationToken ct = default);

    /// <summary>停止广播与监听。</summary>
    Task StopAsync();
}

/// <summary>设备变化事件参数。</summary>
public sealed class DeviceChangeEventArgs : EventArgs
{
    /// <summary>发生变化的设备。</summary>
    public required DiscoveredDevice Device { get; init; }

    /// <summary>变化类型（新发现/更新/离线）。</summary>
    public required DeviceChangeType ChangeType { get; init; }
}

/// <summary>设备变化类型。</summary>
public enum DeviceChangeType
{
    /// <summary>新发现（首次宣告）。</summary>
    Discovered,

    /// <summary>信息更新（重复宣告刷新在线状态）。</summary>
    Updated,

    /// <summary>心跳超时离线。</summary>
    Offline,
}
