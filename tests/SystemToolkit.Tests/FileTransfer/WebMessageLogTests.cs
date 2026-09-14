using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Infrastructure.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// W3：会话消息流水与**重连补拉**（<c>GET /api/messages?since=</c>），以及 <c>fileOffered</c> 推送。
/// <para>
/// 这一组的要害是那条浏览器硬约束：手机锁屏/切后台时 WS 会被系统回收，
/// 断连期间的消息**收不到也不自动补** —— 补拉就是补这个洞。所以用例围绕三件事：
/// ① 消息真的进了流水且序号连续；② 补拉只给"没给过的"；③ **瞬时状态不进流水**
/// （补一条过期进度只会误导，而设备列表重连时本来就会重发）。
/// </para>
/// </summary>
public class WebMessageLogTests
{
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static TransferSettings MakeSettings(int port) => new() { WebPort = port };

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stkft-msglog", Guid.NewGuid().ToString("N"));
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

    private static async Task<JsonElement> GetMessagesAsync(HttpClient http, int port, string token, long? since)
    {
        string url = $"http://localhost:{port}/api/messages?t={token}"
            + (since is null ? string.Empty : $"&since={since}");
        HttpResponseMessage resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostTextAsync(HttpClient http, int port, string token, string text)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await http.PostAsync($"http://localhost:{port}/api/text?t={token}", content);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>连上 /ws 并吃掉首帧三连，之后拿到的就是业务推送。</summary>
    private static async Task<System.Net.WebSockets.ClientWebSocket> ConnectWsAsync(int port, string token)
    {
        var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?t={token}"), CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            await ReceiveTextAsync(ws);
        }

        return ws;
    }

    private static async Task<string> ReceiveTextAsync(System.Net.WebSockets.WebSocket ws)
    {
        byte[] buffer = new byte[16 * 1024];
        System.Net.WebSockets.WebSocketReceiveResult result =
            await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    /// <summary>
    /// 文本推送会进流水：<c>POST /api/text</c> 的响应与 WS 帧带**同一个序号**，
    /// 补拉时能按它去重（否则自己发的那条会再出现一次——流水里也有它，因为别的标签页需要看到）。
    /// </summary>
    [Fact]
    public async Task TextPublish_AssignsSeq_SharedBetweenResponseAndWsFrame()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

            JsonElement body = await PostTextAsync(http, port, server.Token, "第一条");
            long seq = body.GetProperty("seq").GetInt64();
            Assert.True(seq > 0, "响应必须带服务端分配的序号");

            string frame = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"chatMessage\"", frame);
            using var doc = JsonDocument.Parse(frame);
            Assert.Equal(seq, doc.RootElement.GetProperty("seq").GetInt64());

            JsonElement log = await GetMessagesAsync(http, port, server.Token, since: null);
            Assert.Equal(1, log.GetProperty("lastSeq").GetInt64());
            Assert.Single(log.GetProperty("messages").EnumerateArray());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>补拉只给"比 since 新的"：已经收到过的不要再发一遍。</summary>
    [Fact]
    public async Task Messages_SinceSeq_ReturnsOnlyNewer()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            long firstSeq = (await PostTextAsync(http, port, server.Token, "A")).GetProperty("seq").GetInt64();
            await PostTextAsync(http, port, server.Token, "B");
            await PostTextAsync(http, port, server.Token, "C");

            JsonElement only = await GetMessagesAsync(http, port, server.Token, firstSeq);
            Assert.Equal(3, only.GetProperty("lastSeq").GetInt64());
            Assert.False(only.GetProperty("truncated").GetBoolean());
            Assert.Equal(2, only.GetProperty("messages").GetArrayLength());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>首次拉取（since 缺省）不该被标成"有缺口"——那时前端本来就没有历史。</summary>
    [Fact]
    public async Task Messages_FirstPull_IsNotTruncated()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            await PostTextAsync(http, port, server.Token, "only");

            JsonElement log = await GetMessagesAsync(http, port, server.Token, since: null);
            Assert.False(log.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 前端记的序号**比服务端还新**（服务重启过，流水与序号归零）→ 如实回 <c>truncated=true</c>，
    /// 而不是假装什么都没发生（前端据此提示"部分历史已失效"）。
    /// </summary>
    [Fact]
    public async Task Messages_SeqAheadOfServer_ReportsTruncated()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            JsonElement log = await GetMessagesAsync(http, port, server.Token, since: 99);
            Assert.True(log.GetProperty("truncated").GetBoolean());
            Assert.Empty(log.GetProperty("messages").EnumerateArray());
            Assert.Equal(0, log.GetProperty("lastSeq").GetInt64());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 🔴 **瞬时状态不进流水**：上传进度推送（<c>transferUpdate</c>）不该出现在补拉结果里。
    /// 补一条过期进度只会让用户看到"卡在 37%"这种已经过去的事实；
    /// 而设备列表等在重连时由首帧三连重新给出。
    /// </summary>
    [Fact]
    public async Task Messages_DoNotContainTransientTransferUpdates()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

            // 传一块（会推 transferUpdate）
            byte[] chunk = new byte[64];
            using var payload = new ByteArrayContent(chunk);
            HttpResponseMessage up = await http.PostAsync(
                $"http://localhost:{port}/api/files/upload-chunk?t={server.Token}"
                + "&name=probe.bin&size=64&mtime=1&offset=0", payload);
            Assert.True(up.IsSuccessStatusCode);

            string frame = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"transferUpdate\"", frame);

            JsonElement log = await GetMessagesAsync(http, port, server.Token, since: null);
            Assert.Equal(0, log.GetProperty("lastSeq").GetInt64());
            Assert.Empty(log.GetProperty("messages").EnumerateArray());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// <c>fileOffered</c> 端到端：推一个真实文件 → WS 收到（含 name/path/size）→ 补拉可见。
    /// </summary>
    [Fact]
    public async Task FileOffer_PublishesToWsAndMessageLog()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "报告.txt"), "hello");
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

            int delivered = await server.PublishFileOfferAsync("报告.txt");
            Assert.Equal(1, delivered);

            string frame = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"fileOffered\"", frame);
            using (var doc = JsonDocument.Parse(frame))
            {
                JsonElement payload = doc.RootElement.GetProperty("payload");
                Assert.Equal("报告.txt", payload.GetProperty("name").GetString());
                Assert.Equal("报告.txt", payload.GetProperty("path").GetString());
                Assert.Equal(5, payload.GetProperty("size").GetInt64());
                Assert.Equal("电脑", payload.GetProperty("from").GetString());
            }

            using var http = new HttpClient();
            JsonElement log = await GetMessagesAsync(http, port, server.Token, since: null);
            JsonElement item = Assert.Single(log.GetProperty("messages").EnumerateArray());
            Assert.Equal("fileOffered", item.GetProperty("type").GetString());
            // payload 与 WS 帧同形：前端可以复用同一段渲染代码
            Assert.Equal("报告.txt", item.GetProperty("payload").GetProperty("name").GetString());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>共享目录里没有该文件 → 抛（而不是推一个点了必然 404 的气泡）。</summary>
    [Fact]
    public async Task FileOffer_MissingFile_Throws()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            await Assert.ThrowsAsync<ArgumentException>(() => server.PublishFileOfferAsync("nope.txt"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>目录穿越路径 → 抛（不能把共享目录外的文件暴露给网页）。</summary>
    [Fact]
    public async Task FileOffer_PathTraversal_Throws()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            await Assert.ThrowsAsync<ArgumentException>(() => server.PublishFileOfferAsync("../evil.txt"));
            await Assert.ThrowsAsync<ArgumentException>(() => server.PublishFileOfferAsync(string.Empty));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 流水超出上限 → 丢最旧，并如实把 <c>truncated</c> 置真（起点已被淘汰 = 中间确实少了消息）。
    /// 不做这一步的话，前端会以为"补拉成功且没有遗漏"，而实际上中间断了一截。
    /// </summary>
    [Fact]
    public async Task Messages_BeyondCapacity_ReportsTruncatedAndKeepsNewest()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "x");
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            const int total = 205; // 上限 200
            for (int i = 0; i < total; i++)
            {
                await server.PublishFileOfferAsync("f.txt");
            }

            using var http = new HttpClient();
            JsonElement log = await GetMessagesAsync(http, port, server.Token, since: 1);

            Assert.Equal(total, log.GetProperty("lastSeq").GetInt64());
            Assert.True(log.GetProperty("truncated").GetBoolean(), "起点已被淘汰时必须如实报缺口");
            Assert.Equal(200, log.GetProperty("messages").GetArrayLength());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 🟠 审查 v8-🟠-4：流水还有**累计字节**上限（2 MB），不只看条数。
    /// <para>
    /// <b>背景</b>：单条 payload 的文本上限是 <c>TransferText.MaxBytes</c>（256 KB），
    /// 而落进流水的是 <b>JSON 转义后</b>的形态——非 ASCII（中文）每个字符膨胀成 <c>\uXXXX</c>
    /// 的 6 字节，最坏 ≈768 KB/条。只限 200 条 ⇒ 单次 <c>GET /api/messages</c> 全量下发最坏
    /// ≈150 MB，而手机端 <c>app.js</c> 是 <c>resp.json()</c> 整包入内存。
    /// </para>
    /// <para>
    /// <b>反向验证</b>：把 <c>MaxMessageLogBytes</c> 预算去掉（只留条数上限）→ 本用例变红
    /// （6 条都在 200 条以内，会全部保留、<c>truncated</c> 为假）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Messages_ByteBudget_EvictsOldestEvenBelowCountLimit()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            using var http = new HttpClient();

            // 86,000 个汉字：UTF-8 258,000 字节（< 256 KB 文本上限）→ JSON 转义后 ≈516 KB/条。
            // 6 条累计 ≈3 MB > 2 MB 预算，但远少于 200 条上限——淘汰只能由**字节**预算触发。
            string chunk = new string('汉', 86_000);
            const int posted = 6;
            for (int i = 0; i < posted; i++)
            {
                await PostTextAsync(http, port, server.Token, chunk);
            }

            JsonElement log = await GetMessagesAsync(http, port, server.Token, since: 1);
            JsonElement messages = log.GetProperty("messages");

            Assert.Equal(posted, log.GetProperty("lastSeq").GetInt64());
            Assert.True(
                messages.GetArrayLength() < posted,
                $"字节预算生效时必须淘汰最旧（否则 {posted} 条会全留着）");
            Assert.True(
                log.GetProperty("truncated").GetBoolean(),
                "起点已被字节预算淘汰 = 中间确实少了消息，必须如实报缺口");
            // 留下的必须是**最新**的那条（丢最旧，不是丢最新）
            Assert.Equal(
                posted,
                messages[messages.GetArrayLength() - 1].GetProperty("seq").GetInt64());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>补拉端点与其它 API 同级，必须带令牌（否则等于把会话内容公开）。</summary>
    [Fact]
    public async Task Messages_WithoutToken_ReturnsUnauthorized()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await http.GetAsync($"http://localhost:{port}/api/messages");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
