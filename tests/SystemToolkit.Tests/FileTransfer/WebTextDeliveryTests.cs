using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Infrastructure.FileTransfer;
using SystemToolkit.Modules.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// W2c：手机（Web 通道）文本的两端接线。
/// <para>
/// 与 W2a 的服务端用例互补——那边测"服务端收下并如实转达"，这边测**桌面端收到之后干了什么**：
/// 走同一扇确认门、按用户决定写剪贴板、拒绝与写失败都要如实留痕。
/// 用真实 Kestrel + 真实 POST 驱动，不假造事件（事件参数由服务端亲手构造，这正是要验的接口）。
/// </para>
/// </summary>
public class WebTextDeliveryTests
{
    private const string TextEndpoint = "/api/text";

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stkft-webtext", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static async Task<HttpResponseMessage> PostTextAsync(HttpClient http, int port, string token, string text)
    {
        string json = JsonSerializer.Serialize(new { text });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await http.PostAsync($"http://localhost:{port}{TextEndpoint}?t={token}", content);
    }

    /// <summary>造一个挂在真实 Web 服务上的桌面 VM（确认门与剪贴板都可注入）。</summary>
    private static async Task<(FileTransferDesktopViewModel Vm, ConcurrentQueue<string> Logs, string Dir)>
        NewDesktopVmAsync(FileWebServer web)
    {
        string dir = NewTempDir();
        var logs = new ConcurrentQueue<string>();
        var vm = new FileTransferDesktopViewModel(
            new DeviceDiscoveryService(),
            new FileTransferService(),
            new TransferHistoryService(dir),
            logs.Enqueue,
            NullLogger.Instance,
            dispatcher: null,
            web: web);
        await Task.CompletedTask;
        return (vm, logs, dir);
    }

    /// <summary>
    /// 手机发文本 → 桌面弹出确认门（同一扇）→ 用户接受 → **全文写进剪贴板**。
    /// <para>反向验证：把 <c>OnWebTextReceived</c> 里的 <c>ConfirmIncomingText(request)</c>
    /// 换成直接 <c>WriteClipboard</c> → 本用例的"确认回调被调用"断言变红。</para>
    /// </summary>
    [Fact]
    public async Task PhoneText_Accepted_WritesFullTextToClipboardThroughConfirmGate()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, dir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
                await NewDesktopVmAsync(server);
            try
            {
                int confirmCalls = 0;
                string? confirmedSource = null;
                var clipboard = new ConcurrentQueue<string>();
                vm.ConfirmTransferRequest = e =>
                {
                    Interlocked.Increment(ref confirmCalls);
                    confirmedSource = e.PeerEndpoint;
                    Assert.Equal(TransferKind.Text, e.Kind);
                    return TransferDecision.AcceptWith(TransferConflictPolicy.Rename);
                };
                vm.WriteClipboard = text => { clipboard.Enqueue(text); return true; };

                using var http = new HttpClient();
                string payload = "密钥：aB3-\n第二行\r\n第三行";
                HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, payload);
                Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

                // ① 走了确认门（而不是默默写剪贴板）
                Assert.Equal(1, confirmCalls);
                Assert.Contains("手机", confirmedSource);

                // ② 剪贴板里是**全文**（含换行），不是预览
                Assert.True(clipboard.TryDequeue(out string? written));
                Assert.Equal(payload, written);

                // ③ 日志如实说明"已接收并写入剪贴板"
                Assert.Contains(logs, m => m.Contains("已接收手机文本") && m.Contains("写入剪贴板"));
            }
            finally
            {
                DeleteTempDir(histDir);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 用户拒绝 → **一个字节都不写剪贴板**，且日志如实说明"手机端不会收到这个结果"
    /// （网页通道没有回执通道，这一点必须显式留痕，不能被理解成"已送达"）。
    /// </summary>
    [Fact]
    public async Task PhoneText_Rejected_DoesNotTouchClipboard()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, dir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
                await NewDesktopVmAsync(server);
            try
            {
                var clipboard = new ConcurrentQueue<string>();
                vm.ConfirmTransferRequest = _ => TransferDecision.Reject;
                vm.WriteClipboard = text => { clipboard.Enqueue(text); return true; };

                using var http = new HttpClient();
                HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, "不该被复制的内容");
                Assert.True(resp.IsSuccessStatusCode);

                Assert.True(clipboard.IsEmpty, "拒绝后不得写剪贴板");
                Assert.Contains(logs, m => m.Contains("已拒绝手机文本") && m.Contains("没有回执"));
            }
            finally
            {
                DeleteTempDir(histDir);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 剪贴板写失败 → 如实报失败，不得假报送达（与 B8b 的 <c>ReceiveTextDecision</c> 同一条红线）。
    /// </summary>
    [Fact]
    public async Task PhoneText_ClipboardWriteFails_LogsFailureHonestly()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, dir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
                await NewDesktopVmAsync(server);
            try
            {
                vm.ConfirmTransferRequest = _ => TransferDecision.AcceptWith(TransferConflictPolicy.Rename);
                vm.WriteClipboard = _ => false;

                using var http = new HttpClient();
                HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, "写不进去的文本");
                Assert.True(resp.IsSuccessStatusCode);

                Assert.Contains(logs, m => m.Contains("剪贴板写入失败"));
            }
            finally
            {
                DeleteTempDir(histDir);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 未注入剪贴板能力（View 还没挂上）时按**写入失败**处理——缺能力时宁可如实报失败，
    /// 也不假报送达（与 <see cref="FileTransferDesktopViewModel.WriteClipboard"/> 的既有约定一致）。
    /// </summary>
    [Fact]
    public async Task PhoneText_WithoutClipboardCapability_ReportsFailure()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, dir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
                await NewDesktopVmAsync(server);
            try
            {
                vm.ConfirmTransferRequest = _ => TransferDecision.AcceptWith(TransferConflictPolicy.Rename);
                // 刻意不设 WriteClipboard

                using var http = new HttpClient();
                Assert.True((await PostTextAsync(http, port, server.Token, "x")).IsSuccessStatusCode);

                Assert.Contains(logs, m => m.Contains("剪贴板写入失败"));
            }
            finally
            {
                DeleteTempDir(histDir);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>「发到手机」：Web 服务未启动 → 明确指向"去哪开"，不笼统说"没有手机在线"。</summary>
    [Fact]
    public async Task SendToPhone_WebNotRunning_LogsWhereToEnable()
    {
        using var server = new FileWebServer(); // 只构造、不启动
        (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
            await NewDesktopVmAsync(server);
        try
        {
            vm.TextToSend = "hello";
            await vm.SendTextToPhoneCommand.ExecuteAsync(null);

            Assert.Contains(logs, m => m.Contains("Web 服务未启动") && m.Contains("手机访问"));
        }
        finally
        {
            DeleteTempDir(histDir);
        }
    }

    /// <summary>未注入 Web 通道时明确说不可用（降级但不静默）。</summary>
    [Fact]
    public async Task SendToPhone_WithoutWebChannel_LogsUnavailable()
    {
        using var server = new FileWebServer();
        (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
            await NewDesktopVmAsync(server);
        try
        {
            var withoutWeb = new FileTransferDesktopViewModel(
                new DeviceDiscoveryService(),
                new FileTransferService(),
                new TransferHistoryService(histDir),
                logs.Enqueue,
                NullLogger.Instance,
                dispatcher: null); // 不给 web
            withoutWeb.TextToSend = "hello";
            await withoutWeb.SendTextToPhoneCommand.ExecuteAsync(null);

            Assert.Contains(logs, m => m.Contains("Web 通道不可用"));
            _ = vm;
        }
        finally
        {
            DeleteTempDir(histDir);
        }
    }

    /// <summary>
    /// 服务在跑但**没有手机在线** → 送达 0，如实说"未送达任何人"
    /// （0 不代表已读，也不该被说成成功）。
    /// </summary>
    [Fact]
    public async Task SendToPhone_NoBrowserOnline_LogsNoRecipient()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, dir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
                await NewDesktopVmAsync(server);
            try
            {
                vm.TextToSend = "hello";
                await vm.SendTextToPhoneCommand.ExecuteAsync(null);

                Assert.Contains(logs, m => m.Contains("没有已连接的手机浏览器") && m.Contains("未送达"));
            }
            finally
            {
                DeleteTempDir(histDir);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 有手机在线 → 推送成功并如实报告送达数（端到端：WS 真的收到 chatMessage）。
    /// </summary>
    [Fact]
    public async Task SendToPhone_WithBrowserOnline_DeliversChatMessage()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, dir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
                await NewDesktopVmAsync(server);
            try
            {
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                await ws.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/ws?t={server.Token}"), CancellationToken.None);
                // 吃掉首帧三连（deviceList → browserList → serverInfo）
                for (int i = 0; i < 3; i++)
                {
                    await ReceiveTextAsync(ws);
                }

                vm.TextToSend = "来自电脑的一段话";
                await vm.SendTextToPhoneCommand.ExecuteAsync(null);

                string frame = await ReceiveTextAsync(ws);
                Assert.Contains("\"type\":\"chatMessage\"", frame);
                Assert.Contains("\"origin\":\"desktop\"", frame);
                Assert.Contains(logs, m => m.Contains("已向 1 个手机浏览器推送文本"));
            }
            finally
            {
                DeleteTempDir(histDir);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>非法文本（超限）→ 不推、不静默：日志给出原因（判据与 TCP 通道同一份）。</summary>
    [Fact]
    public async Task SendToPhone_TextTooLong_LogsAndSkipsBroadcast()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, dir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string histDir) =
                await NewDesktopVmAsync(server);
            try
            {
                vm.TextToSend = new string('a', (256 * 1024) + 1);
                await vm.SendTextToPhoneCommand.ExecuteAsync(null);

                Assert.Contains(logs, m => m.Contains("单条上限"));
                Assert.DoesNotContain(logs, m => m.Contains("已向"));
            }
            finally
            {
                DeleteTempDir(histDir);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static async Task<string> ReceiveTextAsync(System.Net.WebSockets.WebSocket ws)
    {
        byte[] buffer = new byte[16 * 1024];
        System.Net.WebSockets.WebSocketReceiveResult result =
            await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
