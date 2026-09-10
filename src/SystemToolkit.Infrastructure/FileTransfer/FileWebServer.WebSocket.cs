using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;

namespace SystemToolkit.Infrastructure.FileTransfer;

/// <summary>
/// Web 文件服务的实时推送分部（🟡 审查 2026-09-10）。
/// <para>
/// <b>背景</b>：前端 <c>wwwroot/app.js</c> 带有完整的 WebSocket 客户端（连接、断线重连、
/// 可见性恢复、心跳），设备列表与在线浏览器都按"WS 推送累积"渲染——但服务端从未实现
/// <c>/ws</c> 端点，导致每次连接失败、每 3 秒无谓重连一轮。本分部补上服务端：
/// 握手 → 首帧全量 → 设备变化增量推送 → 心跳应答。
/// </para>
/// <para>
/// <b>协议</b>（与 app.js <c>handleWsMessage</c> 对齐）：消息体统一为 <c>{ type, payload }</c>
/// （<c>deviceChange</c> 额外带顶层 <c>changeType</c>）；客户端发 <c>{type:"ping"}</c>，
/// 服务端回 <c>{type:"pong"}</c>。JSON 采用 <see cref="JsonSerializerDefaults.Web"/>（camelCase）
/// ——前端读的是 <c>deviceId</c> / <c>isOnline</c> 这类小驼峰字段。
/// </para>
/// <para>
/// <b>三类推送</b>：<c>deviceList</c>（设备全量快照，含<b>本机</b>条目）、
/// <c>browserList</c>（在线浏览器全量，含当前这台）、<c>serverInfo</c>（服务器地址）。
/// <c>browserList</c> 是 2026-09-11 补的——此前服务端从不推送它，而前端
/// <c>state.browsers</c> 只由该消息填充，导致「局域网设备 0」里连**当前浏览器自己**都不显示。
/// 连接加入/离开都会重推全量（各浏览器据此互相看见）。
/// </para>
/// <para>
/// <b>安全</b>：<c>/ws</c> 不在免令牌白名单内，沿用全局令牌中间件——升级请求必须带
/// <c>?t=&lt;token&gt;</c>，未授权连接在中间件层即被 401 挡下，不到达本端点。
/// </para>
/// </summary>
public sealed partial class FileWebServer
{
    /// <summary>实时推送连接数上限（🟡 审查 2026-09-11：防已配对设备开大量 upgrade 造成资源压力）。</summary>
    private const int MaxWsClients = 16;

    /// <summary>活跃的实时推送连接。键仅用于移除；值为连接包装（内部串行化发送）。</summary>
    private readonly ConcurrentDictionary<Guid, WsClient> _wsClients = new();

    /// <summary>设备变化订阅句柄（停止时退订，避免服务停了仍在收事件）。</summary>
    private EventHandler<DeviceChangeEventArgs>? _deviceChangedHandler;

    /// <summary>推送 JSON 选项：camelCase + 宽松读取（对齐前端既有字段命名）。</summary>
    private static readonly JsonSerializerOptions WsJsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <c>/ws</c> 端点：升级后先补一帧全量快照（设备列表 + 服务器信息），随后进入读循环
    /// 只处理心跳；设备上下线由 <see cref="BroadcastDeviceChangeAsync"/> 主动推送。
    /// </summary>
    private async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("该端点仅接受 WebSocket 升级请求。", ctx.RequestAborted);
            return;
        }

        // 🟡 审查 2026-09-11（🟡-1）：连接数上限。token 门已挡住外部未授权者，
        // 此处防的是「已配对设备开数千 upgrade」造成的内存/FD 压力（每个连接 = 一个 socket + 闸 + 缓冲）。
        // 局域网多设备+多浏览器场景下 16 足够；如需更多，调此常量即可。
        if (_wsClients.Count >= MaxWsClients)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("实时推送连接数已达上限，请稍后重试。", ctx.RequestAborted);
            return;
        }

        using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
        string clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "未知地址";
        var client = new WsClient(socket, clientIp, DateTimeOffset.UtcNow);
        var id = Guid.NewGuid();
        _wsClients[id] = client;

        try
        {
            // 首帧：前端据此渲染初始列表（此后只收增量）
            await client.SendJsonAsync(
                BuildEnvelope("deviceList", SnapshotDevices()),
                ctx.RequestAborted);
            await client.SendJsonAsync(
                BuildEnvelope("browserList", BuildBrowserList()),
                ctx.RequestAborted);
            // clientId = 本连接的 id：前端据此在 browserList 里认出「哪一条是我」
            // （手机端需要知道"浏览器那条里哪个是我"，而不是把电脑当成"本机"）
            await client.SendJsonAsync(
                BuildEnvelope("serverInfo", new { host = _lanIp, clientId = id }),
                ctx.RequestAborted);

            // 已在线的其它浏览器需要看到本连接加入（在线数变化）——不推的话，
            // 先连的浏览器永远停在它连接那一刻的列表长度。排除自己：首帧已含自己。
            await BroadcastBrowserListAsync(excludeId: id);

            byte[] buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                // App 层心跳：前端只发 ping，统一回 pong（不解析内容，免得为心跳加协议负担）
                await client.SendJsonAsync("""{"type":"pong"}""", ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开 / 服务停止：正常收尾路径
        }
        catch (WebSocketException)
        {
            // 网络中断（手机锁屏、Wi-Fi 抖动）：同样按断开处理
        }
        finally
        {
            _wsClients.TryRemove(id, out _);
            await client.CloseAsync();

            // 本连接已移出集合 → 重推全量，其余浏览器看到在线数下降
            await BroadcastBrowserListAsync();
        }
    }

    /// <summary>设备上下线/更新 → 广播给所有已连接浏览器（单个连接失败不影响其余）。</summary>
    private async Task BroadcastDeviceChangeAsync(DeviceChangeEventArgs e)
    {
        // 本方法由事件回调 fire-and-forget 调用：整体兜底，广播失败不得逃逸到设备发现线程
        try
        {
            await BroadcastJsonAsync(BuildEnvelope("deviceChange", e.Device, e.ChangeType.ToString()));
        }
        catch (Exception ex)
        {
            _logger.Warn($"[FileWebServer] 设备变化广播失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 在线浏览器快照。前端 <c>renderDevices</c> 读 <c>ipAddress</c> 与 <c>connectedAt</c>
    /// （后者经 <c>formatTime</c> 显示为「连接于 …」）；<c>Id</c> 供前端与 <c>serverInfo.clientId</c>
    /// 比对，认出「哪一条是我自己」并打上「本机」角标（2026-09-11 主人反馈：
    /// 原先服务端合成的电脑条目被打成「本机」，在手机上极易误会成手机自己）。
    /// </summary>
    private IReadOnlyList<BrowserInfo> BuildBrowserList() =>
        _wsClients
            .OrderBy(kv => kv.Value.ConnectedAt)
            .Select(kv => new BrowserInfo(kv.Key, kv.Value.IpAddress, kv.Value.ConnectedAt))
            .ToList();

    /// <summary>
    /// 在线浏览器列表变化（有连接加入或离开）→ 重推**全量**给所有连接。
    /// <para>
    /// 2026-09-11 新增：此前服务端从不推送 <c>browserList</c>，前端 <c>state.browsers</c> 恒空，
    /// 于是手机端「局域网设备 0」——连**当前这个浏览器自己**都不在列表里。
    /// 推全量而非增量：连接数是个位数，全量让各端天然最终一致，不需要维护差量状态。
    /// </para>
    /// </summary>
    /// <param name="excludeId">
    /// 可选的排除连接（新连接加入时传自己）：该连接的首帧已经带过含自己的最新列表，
    /// 再推一次纯属冗余，且会打乱「首帧三连」的帧序（前端与测试都按序依赖）。
    /// </param>
    private async Task BroadcastBrowserListAsync(Guid? excludeId = null)
    {
        try
        {
            await BroadcastJsonAsync(BuildEnvelope("browserList", BuildBrowserList()), excludeId);
        }
        catch (Exception ex)
        {
            // 广播失败不影响连接自身生命周期（下次有人进出时会再推一次）
            _logger.Warn($"[FileWebServer] 在线浏览器广播失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 并行派发同一消息给所有连接（TrySendJsonAsync 自身已吞掉单连接异常）。
    /// <para>
    /// 🟡 审查 2026-09-11（R-1）：**并行**而非顺序 await。单条发送虽已带 IoTimeout
    /// （不会永久挂死，区别于 v3 🔴-1 的无界饿死），但顺序循环下 N 个半开连接会把尾延迟
    /// 累加成 N × 3s，期间**所有**浏览器端都收不到推送。并行后最坏只等一个 IoTimeout。
    /// </para>
    /// </summary>
    private async Task BroadcastJsonAsync(string json, Guid? excludeId = null)
    {
        var sends = new List<Task>();
        foreach ((Guid id, WsClient client) in _wsClients)
        {
            if (excludeId == id)
            {
                continue;
            }

            sends.Add(client.TrySendJsonAsync(json));
        }

        await Task.WhenAll(sends);
    }

    /// <summary>在线浏览器条目（序列化后为 camelCase：<c>id</c> / <c>ipAddress</c> / <c>connectedAt</c>）。</summary>
    private sealed record BrowserInfo(Guid Id, string IpAddress, DateTimeOffset ConnectedAt);

    private static string BuildEnvelope(string type, object? payload, string? changeType = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["payload"] = payload,
        };
        if (changeType is not null)
        {
            envelope["changeType"] = changeType;
        }

        return JsonSerializer.Serialize(envelope, WsJsonOpts);
    }

    /// <summary>停止推送：退订设备变化并关闭所有连接（由 <c>StopAsync</c> 调用）。</summary>
    private async Task StopWebSocketClientsAsync()
    {
        if (_discovery is not null && _deviceChangedHandler is not null)
        {
            _discovery.DeviceChanged -= _deviceChangedHandler;
            _deviceChangedHandler = null;
        }

        // 🔴-1：并行关闭——串行时每个连接的握手超时会累加（N 个不配合的连接 × 3s
        // 可以把「停止服务」挂住数十秒）；并行后最坏只等一个 IoTimeout
        var closing = new List<Task>();
        foreach (WsClient client in _wsClients.Values)
        {
            closing.Add(client.CloseAsync());
        }

        await Task.WhenAll(closing);

        _wsClients.Clear();
    }

    /// <summary>
    /// 单个推送连接。<b>发送必须串行</b>——WebSocket 不允许并发的 <c>SendAsync</c>
    /// （设备广播与心跳应答可能同时写同一连接），故内置信号量串行化。
    /// </summary>
    private sealed class WsClient(WebSocket socket, string ipAddress, DateTimeOffset connectedAt)
    {
        /// <summary>对端 IP（「在线浏览器」列表展示用）。</summary>
        public string IpAddress { get; } = ipAddress;

        /// <summary>连接建立时刻（列表展示为「连接于 …」，同时作为稳定排序键）。</summary>
        public DateTimeOffset ConnectedAt { get; } = connectedAt;

        /// <summary>
        /// 单次对外 IO（发送 / 关闭握手）的超时上界。🔴 审查 2026-09-11（🔴-1）。
        /// <para>
        /// 对端「消失但无 RST」时（手机断 Wi-Fi、被系统回收、锁屏切后台），<c>SendAsync</c> 会阻塞
        /// 到 TCP 自身超时（数十秒~分钟级）。广播是**顺序** <c>foreach await</c>——一个这样的连接
        /// 会**饿死其余所有浏览器端**；而每个新的 <c>deviceChange</c> 还会继续堆到同一连接的闸上，
        /// 形成「越堵越堆」。关闭握手同理：不响应 close 的对端可把 <c>StopAsync</c> 无限期挂起。
        /// 故所有对外 IO 都必须有上界。
        /// </para>
        /// </summary>
        private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);

        private readonly SemaphoreSlim _sendGate = new(1, 1);

        public async Task SendJsonAsync(string json, CancellationToken ct)
        {
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
                    endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        /// <summary>广播用发送：任一连接异常只丢弃该连接，不打断整轮广播。</summary>
        public async Task TrySendJsonAsync(string json)
        {
            try
            {
                // 🔴-1：必须有超时——无界等待会让一个半开连接卡死整轮顺序广播
                using var cts = new CancellationTokenSource(IoTimeout);
                await SendJsonAsync(json, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 该连接已不可用（对端已断/半关/发送超时）；其读循环会自行收尾移除
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // 🔴-1：正常关闭握手也要有上界；超时即走下方硬断
                    using var cts = new CancellationTokenSource(IoTimeout);
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 🔴-1：握手超时/失败 → 硬断。宁可让对端把它当异常断连（前端本就带 3s 重连），
                // 也绝不让一个不配合的对端把 StopAsync 挂住（socket 随 using 释放）
                try
                {
                    socket.Abort();
                }
                catch (Exception)
                {
                    // 已释放/已中止：无补救动作
                }
            }
        }
    }
}
