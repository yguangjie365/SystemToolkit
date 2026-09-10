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
        var client = new WsClient(socket);
        var id = Guid.NewGuid();
        _wsClients[id] = client;

        try
        {
            // 首帧：前端据此渲染初始列表（此后只收增量）
            await client.SendJsonAsync(
                BuildEnvelope("deviceList", _discovery?.Devices ?? Array.Empty<DiscoveredDevice>()),
                ctx.RequestAborted);
            await client.SendJsonAsync(
                BuildEnvelope("serverInfo", new { host = _lanIp }),
                ctx.RequestAborted);

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
        }
    }

    /// <summary>设备上下线/更新 → 广播给所有已连接浏览器（单个连接失败不影响其余）。</summary>
    private async Task BroadcastDeviceChangeAsync(DeviceChangeEventArgs e)
    {
        // 本方法由事件回调 fire-and-forget 调用：整体兜底，广播失败不得逃逸到设备发现线程
        try
        {
            string json = BuildEnvelope("deviceChange", e.Device, e.ChangeType.ToString());

            // 🟡 审查 2026-09-11（R-1）：**并行**派发而非顺序 await。单条发送虽已带 IoTimeout
            // （不会永久挂死，区别于 v3 🔴-1 的无界饿死），但顺序循环下 N 个半开连接会把尾延迟
            // 累加成 N × 3s，期间**所有**浏览器端都收不到推送。并行后最坏只等一个 IoTimeout。
            // TrySendJsonAsync 自身已吞掉单连接异常，故 WhenAll 不会因个别连接失败而抛出。
            var sends = new List<Task>();
            foreach (WsClient client in _wsClients.Values)
            {
                sends.Add(client.TrySendJsonAsync(json));
            }

            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[FileWebServer] 设备变化广播失败：{ex.Message}");
        }
    }

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
    private sealed class WsClient(WebSocket socket)
    {
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
