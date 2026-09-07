using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.FileTransfer.Services.Protocol;

namespace SystemToolkit.Tests;

/// <summary>
/// 设备发现服务单测：真实 UDP 回环验证监听/发现/更新/过滤/离线路径。
/// 广播发送侧由测试用 UdpClient 模拟（向 127.0.0.1:{port} 发送合法/非法宣告包），
/// 不依赖局域网环境；初始广播用例复用 SO_REUSEADDR 在同端口挂测试监听端验证。
/// 自旧工程移植，L15 英文命名；新增持久化 DeviceId 契约用例。
/// </summary>
public class DeviceDiscoveryServiceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    /// <summary>取一个空闲 UDP 端口（绑定 0 后读出再释放，存在极小竞态，可接受）。</summary>
    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(0);
        return ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static TransferSettings MakeSettings(int port) => new()
    {
        DiscoveryPort = port,
        TransferPort = 1,
        WebPort = 2,
        DeviceName = "测试机",
        HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        OfflineTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>向本机回环地址发送一条设备宣告。</summary>
    private static async Task SendAnnouncementAsync(int port, DeviceAnnouncement announce)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(announce, JsonOpts);
        using var sender = new UdpClient();
        await sender.SendAsync(payload, payload.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
    }

    private static DeviceAnnouncement ValidAnnouncement(string did = "device-A", string name = "甲设备") => new()
    {
        Did = did,
        Dn = name,
        Tp = 18889,
        Wp = 18890,
    };

    [Fact]
    public async Task LocalDeviceId_MatchesMachineNamePlusEightHex()
    {
        await using var svc = new DeviceDiscoveryService();
        Assert.Matches(@"^.+-[0-9a-fA-F]{8}$", svc.LocalDeviceId);
    }

    [Fact]
    public async Task LocalDeviceId_PersistedAcrossInstances()
    {
        // 2026-09-06 新契约：身份令牌持久化，同机多实例（跨重启）ID 稳定，对端可长期记忆
        await using var first = new DeviceDiscoveryService();
        await using var second = new DeviceDiscoveryService();
        Assert.Equal(first.LocalDeviceId, second.LocalDeviceId);
    }

    [Fact]
    public async Task Ctor_DeviceIdOverride_Respected()
    {
        // 同机双实例测试必须显式传不同 ID，否则持久化 ID 相同会被彼此当作自身广播过滤
        await using var svc = new DeviceDiscoveryService(deviceId: "override-1");
        Assert.Equal("override-1", svc.LocalDeviceId);
    }

    [Fact]
    public async Task Devices_EmptyBeforeStart()
    {
        await using var svc = new DeviceDiscoveryService();
        Assert.Empty(svc.Devices);
    }

    [Fact]
    public async Task StartAsync_Twice_ThrowsInvalidOperation()
    {
        int port = FreeUdpPort();
        await using var svc = new DeviceDiscoveryService();
        await svc.StartAsync(MakeSettings(port));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(MakeSettings(port)));
        await svc.StopAsync();
    }

    [Fact]
    public async Task ValidAnnouncement_RaisesDiscovered_WithAllFields()
    {
        int port = FreeUdpPort();
        await using var svc = new DeviceDiscoveryService();
        var tcs = new TaskCompletionSource<DeviceChangeEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.DeviceChanged += (_, e) => tcs.TrySetResult(e);

        await svc.StartAsync(MakeSettings(port));
        await SendAnnouncementAsync(port, ValidAnnouncement());

        DeviceChangeEventArgs e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DeviceChangeType.Discovered, e.ChangeType);
        Assert.Equal("device-A", e.Device.DeviceId);
        Assert.Equal("甲设备", e.Device.Name);
        Assert.Equal(System.Net.IPAddress.Loopback, e.Device.IPAddress);
        Assert.Equal(18889, e.Device.TransferPort);
        Assert.Equal(18890, e.Device.WebPort);
        Assert.Single(svc.Devices);
    }

    [Fact]
    public async Task DuplicateAnnouncement_RaisesUpdated_NoDuplicateEntry()
    {
        int port = FreeUdpPort();
        await using var svc = new DeviceDiscoveryService();
        var types = new List<DeviceChangeType>();
        var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.DeviceChanged += (_, e) =>
        {
            types.Add(e.ChangeType);
            if (e.ChangeType == DeviceChangeType.Discovered)
            {
                discovered.TrySetResult();
            }
        };

        await svc.StartAsync(MakeSettings(port));
        await SendAnnouncementAsync(port, ValidAnnouncement());
        await discovered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 第二次广播同名设备 → Updated；稍等片刻确认没有再发 Discovered
        await SendAnnouncementAsync(port, ValidAnnouncement());
        await Task.Delay(800);

        Assert.Equal(DeviceChangeType.Discovered, types[0]);
        Assert.Contains(DeviceChangeType.Updated, types);
        Assert.DoesNotContain(types.Skip(1), t => t == DeviceChangeType.Discovered);
        Assert.Single(svc.Devices);
    }

    [Fact]
    public async Task BadMagicAndBrokenJson_Ignored_ListenerSurvives()
    {
        int port = FreeUdpPort();
        await using var svc = new DeviceDiscoveryService();
        var anyEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.DeviceChanged += (_, _) => anyEvent.TrySetResult();

        await svc.StartAsync(MakeSettings(port));

        // ① 魔数不对的合法 JSON（替换掉协议魔数）
        string badMagicJson = JsonSerializer.Serialize(ValidAnnouncement(), JsonOpts)
            .Replace(DeviceAnnouncement.Magic, "OTHER/0.0", StringComparison.Ordinal);
        byte[] badMagic = Encoding.UTF8.GetBytes(badMagicJson);
        using (var sender = new UdpClient())
        {
            await sender.SendAsync(badMagic, badMagic.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
        }

        // ② 损坏的 JSON
        byte[] broken = Encoding.UTF8.GetBytes("{not-a-json");
        using (var sender = new UdpClient())
        {
            await sender.SendAsync(broken, broken.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
        }

        // 非法包处理后，合法包仍必须能被发现（证明监听未被破坏）
        await SendAnnouncementAsync(port, ValidAnnouncement());
        await anyEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 只应有合法包触发的这一条事件、列表里只有这一台设备
        Assert.Single(svc.Devices);
        Assert.Equal("device-A", svc.Devices[0].DeviceId);
    }

    [Fact]
    public async Task SelfAnnouncement_Filtered_NoEvent()
    {
        int port = FreeUdpPort();
        await using var svc = new DeviceDiscoveryService();
        var anyEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.DeviceChanged += (_, _) => anyEvent.TrySetResult();

        await svc.StartAsync(MakeSettings(port));
        // 伪造与本机相同 DeviceId 的宣告包
        await SendAnnouncementAsync(port, ValidAnnouncement(did: svc.LocalDeviceId));
        await Task.Delay(800);

        Assert.Empty(svc.Devices);
        Assert.False(anyEvent.Task.IsCompleted, "自身广播不应触发任何设备事件");
    }

    [Fact]
    public async Task HeartbeatTimeout_DeviceRemoved_OfflineRaised()
    {
        int port = FreeUdpPort();
        TransferSettings settings = MakeSettings(port);
        settings.OfflineTimeout = TimeSpan.FromMilliseconds(400); // 清扫周期最少 1 秒，约 1~2 秒内判定离线
        await using var svc = new DeviceDiscoveryService();
        var offline = new TaskCompletionSource<DeviceChangeEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.DeviceChanged += (_, e) =>
        {
            if (e.ChangeType == DeviceChangeType.Offline)
            {
                offline.TrySetResult(e);
            }
        };

        await svc.StartAsync(settings);
        await SendAnnouncementAsync(port, ValidAnnouncement());

        // 等待设备被发现，然后等离线
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (svc.Devices.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Single(svc.Devices);

        DeviceChangeEventArgs e = await offline.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("device-A", e.Device.DeviceId);
        Assert.Empty(svc.Devices);
    }

    [Fact]
    public async Task InitialBroadcast_ProbeListenerReceivesAnnouncement()
    {
        int port = FreeUdpPort();

        // 测试监听端先挂上（与服务端同样 ReuseAddress），接收服务启动时的立即广播
        using var probe = new UdpClient();
        probe.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        probe.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));

        await using var svc = new DeviceDiscoveryService();
        await svc.StartAsync(MakeSettings(port));

        Task<UdpReceiveResult> receiveTask = probe.ReceiveAsync();
        UdpReceiveResult result = await receiveTask.WaitAsync(TimeSpan.FromSeconds(10));

        DeviceAnnouncement? announce = JsonSerializer.Deserialize<DeviceAnnouncement>(result.Buffer, JsonOpts);
        Assert.NotNull(announce);
        Assert.True(announce!.IsValid);
        Assert.Equal(svc.LocalDeviceId, announce.Did);
        Assert.Equal("测试机", announce.Dn);
        Assert.Equal(1, announce.Tp);
        Assert.Equal(2, announce.Wp);
    }

    [Fact]
    public async Task StopAsync_ClearsDevices_Idempotent_Restartable()
    {
        int port = FreeUdpPort();
        await using var svc = new DeviceDiscoveryService();
        await svc.StartAsync(MakeSettings(port));
        await SendAnnouncementAsync(port, ValidAnnouncement());

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (svc.Devices.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.NotEmpty(svc.Devices);

        await svc.StopAsync();
        Assert.Empty(svc.Devices);

        // 重复停止应安全（幂等）
        await svc.StopAsync();

        // 停止后可重新启动
        int port2 = FreeUdpPort();
        await svc.StartAsync(MakeSettings(port2));
        await svc.StopAsync();
    }
}
