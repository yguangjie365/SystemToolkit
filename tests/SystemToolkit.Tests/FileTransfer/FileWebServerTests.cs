using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Infrastructure.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// Web 文件服务单测：真实 Kestrel 回环验证令牌认证 / 配对换令牌（PairingService 一次性消费）/
/// 路径穿越防护 / 分块断点续传 / 上传不覆盖 / mtime 还原 / 打包下载。
/// <para>令牌由 StartAsync 随机生成并经 <see cref="IFileWebServer.Token"/> 暴露，用例直接取用；
/// 上传覆盖逻辑走 <see cref="IFileWebServer.WriteUploadedFileAsync"/>（免 multipart 组包）。</para>
/// </summary>
public class FileWebServerTests
{
    /// <summary>测试用日志收集器：把服务端留痕内容暴露给断言消息（排查 500 时靠它看真实异常）。</summary>
    private sealed class CapturingLogger : SystemToolkit.Core.Contracts.ILogger
    {
        public List<string> Messages { get; } = new();

        public void Info(string message) => Messages.Add("[INFO] " + message);

        public void Warn(string message) => Messages.Add("[WARN] " + message);

        public void Error(string message, Exception? ex = null) => Messages.Add("[ERROR] " + message + " " + ex);
    }

    /// <summary>取一个空闲 TCP 端口。</summary>
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
        string dir = Path.Combine(Path.GetTempPath(), "stkftweb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        { Directory.Delete(dir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    /// <summary>上传一个分块，返回原始响应（调用方自行断言状态码）。</summary>
    private static async Task<HttpResponseMessage> PostChunkAsync(
        HttpClient http, int port, string token, string name, long total, long mtime, long offset, byte[] chunk)
    {
        string url = $"http://localhost:{port}/api/files/upload-chunk?t={token}"
            + $"&name={Uri.EscapeDataString(name)}&size={total}&mtime={mtime}&offset={offset}";
        using var content = new ByteArrayContent(chunk);
        return await http.PostAsync(url, content);
    }

    private static async Task<long> QueryReceivedAsync(HttpClient http, int port, string token, string name, long total, long mtime)
    {
        string url = $"http://localhost:{port}/api/files/upload-status?t={token}"
            + $"&name={Uri.EscapeDataString(name)}&size={total}&mtime={mtime}";
        HttpResponseMessage resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("received").GetInt64();
    }

    /// <summary>用配对码换取令牌（POST /api/pair，免令牌白名单端点）。</summary>
    private static async Task<HttpResponseMessage> PairAsync(HttpClient http, int port, string code)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { code }),
            Encoding.UTF8, "application/json");
        return await http.PostAsync($"http://localhost:{port}/api/pair", content);
    }

    [Fact]
    public async Task Api_WithoutToken_Returns401Json()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await http.GetAsync($"http://localhost:{port}/api/files");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            // 必须是 JSON：前端 fetch 收到 HTML 页会解析失败，只能报「加载失败」
            Assert.Contains("json", resp.Content.Headers.ContentType!.MediaType);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Api_WithToken_ListsFilesWithoutLeakingAbsolutePaths()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "hello");

            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await http.GetAsync($"http://localhost:{port}/api/files?t={server.Token}");
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("a.txt", body);
            // FullPath 已标 [JsonIgnore]：响应中不得出现共享根目录的绝对路径
            Assert.DoesNotContain(dir, body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Download_PathTraversal_Returns404()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            // 共享目录之外的「机密文件」
            string outsideDir = dir + "_outside";
            Directory.CreateDirectory(outsideDir);
            await File.WriteAllTextAsync(Path.Combine(outsideDir, "secret.txt"), "top secret");

            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            string traversalPath = $"../{Path.GetFileName(dir)}_outside/secret.txt";
            HttpResponseMessage resp = await http.GetAsync(
                $"http://localhost:{port}/api/files/download?t={server.Token}&path={Uri.EscapeDataString(traversalPath)}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
            DeleteTempDir(dir + "_outside");
        }
    }

    [Fact]
    public async Task Browse_OutOfRootOrRootedPath_ReturnsEmpty()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            Assert.Empty(server.Browse("../.."));
            Assert.Empty(server.Browse(@"..\..\etc"));
            // Path.Combine 对 rooted path 会整体替换：必须同样被拦
            Assert.Empty(server.Browse(@"C:\Windows"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Browse_PartTempFiles_FilteredFromListing()
    {
        // 共享目录内 .part 临时文件（上传进行中 .upload_{guid}.part
        // 与 TCP 断点续传半截 {safeName}.{peerIp}.part）不得出现在手机端浏览列表中，
        // 否则可被下载获得损坏内容
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            // 正常文件 → 必须保留
            await File.WriteAllTextAsync(Path.Combine(dir, "normal.txt"), "ok");
            // Web 上传临时文件 → 必须过滤
            await File.WriteAllTextAsync(Path.Combine(dir, ".upload_abcdef0123456789abcdef0123456789.part"), "half-upload");
            // TCP 断点续传半截文件 → 必须过滤
            await File.WriteAllTextAsync(Path.Combine(dir, "quarterly.pdf.192_168_1_5.part"), "half-tcp");
            // 大写扩展名（忽略大小写保护）→ 必须过滤
            await File.WriteAllTextAsync(Path.Combine(dir, "legacy.PART"), "orphan");

            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            var list = server.Browse().ToList();

            Assert.Single(list);
            Assert.Equal("normal.txt", list[0].Name);
            Assert.DoesNotContain(list, e => e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task WriteUploadedFile_SameName_AppendsSequenceWithoutOverwrite()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            await server.WriteUploadedFileAsync("a.txt", new MemoryStream(Encoding.UTF8.GetBytes("first")));
            await server.WriteUploadedFileAsync("a.txt", new MemoryStream(Encoding.UTF8.GetBytes("second")));
            // 带路径前缀的文件名只取纯文件名，不落子目录
            await server.WriteUploadedFileAsync(@"sub\dir\a.txt", new MemoryStream(Encoding.UTF8.GetBytes("third")));

            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(dir, "a.txt")));
            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(dir, "a (1).txt")));
            Assert.Equal("third", await File.ReadAllTextAsync(Path.Combine(dir, "a (2).txt")));
            Assert.False(Directory.Exists(Path.Combine(dir, "sub")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 🟡 审查 2026-09-10（🟡-12）：非法文件名必须在写盘前被拒——覆盖 Windows 保留设备名
    /// （含带扩展名形式）与非法字符（NUL 字节）。反向验证：去掉 WriteUploadedFileAsync 中
    /// <c>IsWindowsReservedDeviceName</c> / <c>Path.GetInvalidFileNameChars</c> 两个判定 → 本用例变红。
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.bin")]
    [InlineData("COM1.log")]
    [InlineData("a\u0000b.txt")]
    public async Task WriteUploadedFile_ReservedDeviceNameOrIllegalChar_Rejected(string badName)
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                server.WriteUploadedFileAsync(badName, new MemoryStream(Encoding.UTF8.GetBytes("x"))));

            // 被拒的名字不得在共享根留下任何文件
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 🟡 审查 2026-09-10（🟡-5）：<c>/ws</c> 端点此前在服务端**完全缺失**（前端却在每 3 秒重连）。
    /// 本用例验证补齐后的契约：带令牌升级成功 → 首帧 <c>deviceList</c> 快照 → 心跳得到 <c>pong</c>。
    /// 反向验证：移除 <c>app.MapGet("/ws", …)</c> → 升级被 401/404 挡下 → 本用例变红。
    /// </summary>
    [Fact]
    public async Task WebSocket_WithToken_SendsDeviceListSnapshotAndAnswersPing()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?t={server.Token}"), CancellationToken.None);

            // 首帧：全量设备列表——**至少含本机条目**（2026-09-11「局域网设备显示 0」的修复点：
            // 发现服务只收录其它设备，单机场景下列表恒空、连服务器自己都不显示）
            string first = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"deviceList\"", first);
            Assert.Contains("\"isLocal\":true", first);

            // 第二帧：在线浏览器列表（含当前这条连接自己）——修复前服务端从不推送该消息，
            // 前端 state.browsers 恒空 → 手机端「局域网设备 0」
            string second = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"browserList\"", second);
            Assert.Contains("\"connectedAt\"", second);

            // 第三帧：服务器信息（前端 serverInfo 用于渲染主机名）
            string third = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"serverInfo\"", third);

            // 心跳：ping → pong
            await ws.SendAsync(
                Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"),
                System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            string pong = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"pong\"", pong);

            await ws.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 第二个浏览器连上时，第一个必须收到「在线浏览器」全量广播且数量为 2
    /// ——2026-09-11 补的服务端欠账（此前 <c>browserList</c> 从不推送）。
    /// </summary>
    [Fact]
    public async Task WebSocket_SecondClientJoin_BroadcastsBrowserListToFirst()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var first = new System.Net.WebSockets.ClientWebSocket();
            await first.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?t={server.Token}"), CancellationToken.None);
            // 消耗首帧三连：deviceList / browserList / serverInfo
            await ReceiveTextAsync(first);
            await ReceiveTextAsync(first);
            await ReceiveTextAsync(first);

            using var second = new System.Net.WebSockets.ClientWebSocket();
            await second.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?t={server.Token}"), CancellationToken.None);

            // 第一个连接收到「有新人加入」的广播：类型正确 + 含两条浏览器记录
            string pushed = await ReceiveTextAsync(first);
            Assert.Contains("\"type\":\"browserList\"", pushed);
            Assert.Equal(2, pushed.Split("\"ipAddress\"").Length - 1);

            await first.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            await second.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>无令牌的 <c>/ws</c> 升级必须被全局令牌中间件挡下（401），不得建立连接。</summary>
    [Fact]
    public async Task WebSocket_WithoutToken_Rejected()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() =>
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>读一条完整文本消息（首帧快照 / 心跳响应用）。</summary>
    private static async Task<string> ReceiveTextAsync(System.Net.WebSockets.WebSocket ws)
    {
        byte[] buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            System.Net.WebSockets.WebSocketReceiveResult result = await ws
                .ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    [Fact]
    public async Task Start_TokenUrlsAndPairCode_GeneratedAsContract()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            Assert.True(server.IsRunning);
            Assert.False(string.IsNullOrEmpty(server.Token));
            Assert.Equal(32, server.Token.Length); // 16 字节随机数 → 32 位 hex
            Assert.Equal(port, server.Port);
            // 二维码（LanUrl）只放短期配对码，绝不能带长期令牌——二维码极易被截图外泄
            Assert.Contains($":{port}/?c=", server.LanUrl);
            Assert.DoesNotContain(server.Token, server.LanUrl);
            Assert.Equal(6, server.PairCode.Length);
            // 本机"打开网页"按钮仍走令牌直连（本机无需扫码）
            Assert.Equal($"http://localhost:{port}/?t={server.Token}", server.Url);

            await server.StopAsync();
            Assert.False(server.IsRunning);
            Assert.Equal(string.Empty, server.LanUrl);
            Assert.Equal(string.Empty, server.PairCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task UploadChunk_TwoChunks_FinalContentAndHashCorrect()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            byte[] data = Encoding.UTF8.GetBytes(new string('a', 100));
            using var http = new HttpClient();

            HttpResponseMessage first = await PostChunkAsync(http, port, server.Token, "big.bin", 100, 1, 0, data[..50]);
            first.EnsureSuccessStatusCode();
            Assert.Equal(50, await QueryReceivedAsync(http, port, server.Token, "big.bin", 100, 1));

            HttpResponseMessage second = await PostChunkAsync(http, port, server.Token, "big.bin", 100, 1, 50, data[50..]);
            second.EnsureSuccessStatusCode();

            Assert.Equal(new string('a', 100), await File.ReadAllTextAsync(Path.Combine(dir, "big.bin")));
            // 定稿后不留 .part 中间产物
            Assert.Empty(Directory.GetFiles(dir, ".upload_*.part"));

            string body = await second.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("done").GetBoolean());
            string expected = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            Assert.Equal(expected, doc.RootElement.GetProperty("hash").GetString());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task UploadChunk_OriginalMtime_RestoredOnFinalize()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            // 模拟手机相册里一张 2024 年拍的照片
            var shotAt = new DateTimeOffset(2024, 5, 1, 10, 20, 30, TimeSpan.FromHours(8));
            long mtime = shotAt.ToUnixTimeMilliseconds();

            using var http = new HttpClient();
            byte[] data = Encoding.UTF8.GetBytes("photo-bytes");
            (await PostChunkAsync(http, port, server.Token, "IMG_0001.jpg", data.Length, mtime, 0, data))
                .EnsureSuccessStatusCode();

            var info = new FileInfo(Path.Combine(dir, "IMG_0001.jpg"));
            // 不还原的话时间会是「刚刚」，按时间排序的相册全乱
            Assert.Equal(2024, info.LastWriteTime.Year);
            Assert.Equal(5, info.LastWriteTime.Month);
            Assert.Equal(1, info.LastWriteTime.Day);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task UploadChunk_InvalidMtime_IgnoredSilently()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            byte[] data = Encoding.UTF8.GetBytes("x");
            // 荒唐的未来时间：应被静默忽略，上传本身照常成功
            HttpResponseMessage resp = await PostChunkAsync(http, port, server.Token, "weird.txt", 1,
                DateTimeOffset.UtcNow.AddYears(50).ToUnixTimeMilliseconds(), 0, data);

            resp.EnsureSuccessStatusCode();
            Assert.True(File.Exists(Path.Combine(dir, "weird.txt")));
            Assert.True(new FileInfo(Path.Combine(dir, "weird.txt")).LastWriteTime.Year < DateTime.Now.Year + 1);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task UploadChunk_InterruptedTransfer_ResumesFromRecordedOffset()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            byte[] data = Encoding.UTF8.GetBytes(new string('b', 120));
            using var http = new HttpClient();

            // 只传前 40 字节就"断线"
            (await PostChunkAsync(http, port, server.Token, "resume.bin", 120, 7, 0, data[..40]))
                .EnsureSuccessStatusCode();

            // 重新选择同一文件：查到的已传偏移必须等于 40
            long received = await QueryReceivedAsync(http, port, server.Token, "resume.bin", 120, 7);
            Assert.Equal(40, received);

            // 从断点续传剩余部分
            (await PostChunkAsync(http, port, server.Token, "resume.bin", 120, 7, 40, data[40..]))
                .EnsureSuccessStatusCode();
            Assert.Equal(new string('b', 120), await File.ReadAllTextAsync(Path.Combine(dir, "resume.bin")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task UploadChunk_OffsetMismatch_Returns409()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            // 服务端尚无任何数据，却声称从偏移 10 开始写
            HttpResponseMessage resp = await PostChunkAsync(http, port, server.Token, "gap.bin", 50, 1, 10, new byte[10]);
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task UploadChunk_SubdirectoryPath_RecreatesDirectoryStructure()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            byte[] data = Encoding.UTF8.GetBytes("nested");
            (await PostChunkAsync(http, port, server.Token, "photos/2024/a.jpg", 6, 1, 0, data))
                .EnsureSuccessStatusCode();

            Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(dir, "photos", "2024", "a.jpg")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Start_PublicAssets_LoadableWithoutToken()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            // 模拟手机扫码：URL 上只有配对码，没有任何令牌
            HttpResponseMessage page = await http.GetAsync($"http://localhost:{port}/?c={server.PairCode}");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("SystemToolkit", await page.Content.ReadAsStringAsync());

            // 页面依赖的 JS / CSS 同样必须能加载，否则前端没有机会执行配对
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{port}/app.js")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{port}/style.css")).StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PairFlow_ScanToPair_ExchangedTokenAccessesProtectedApi()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "scan.txt"), "ok");

            using var http = new HttpClient();

            // ① 扫码：二维码里只有配对码，绝不能是长期令牌
            Assert.Contains("?c=", server.LanUrl);
            Assert.DoesNotContain(server.Token, server.LanUrl);

            // ② 首屏加载（无令牌）。把二维码里的局域网 IP 换成本机回环，避免依赖网卡可达性
            string queryAndPath = server.LanUrl[server.LanUrl.IndexOf("/?", StringComparison.Ordinal)..];
            HttpResponseMessage page = await http.GetAsync($"http://localhost:{port}{queryAndPath}");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);

            // ③ 前端用配对码换令牌
            HttpResponseMessage pairResp = await PairAsync(http, port, server.PairCode);
            pairResp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync());
            string token = doc.RootElement.GetProperty("token").GetString()!;

            // ④ 用令牌访问数据接口
            HttpResponseMessage api = await http.GetAsync($"http://localhost:{port}/api/files?t={token}");
            api.EnsureSuccessStatusCode();
            Assert.Contains("scan.txt", await api.Content.ReadAsStringAsync());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PairCode_AfterRotation_LanUrlCarriesNewCode()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            // 注入短生命周期配对码服务：模拟「服务运行超过 10 分钟」的轮换场景
            // （旧实现缓存 LanUrl 会携带过期配对码，展开二维码扫码必 401——
            // 现为计算属性，实时取 PairingService 当前码）
            var pairing = new PairingService(TimeSpan.FromMilliseconds(120));
            await using var server = new FileWebServer(pairing: pairing);
            await server.StartAsync(MakeSettings(port), dir);

            string codeBefore = server.PairCode;
            Assert.Contains("?c=" + codeBefore, server.LanUrl);

            Thread.Sleep(200);

            string codeAfter = server.PairCode;
            Assert.NotEqual(codeBefore, codeAfter);             // 确认轮换确实发生
            Assert.Contains("?c=" + codeAfter, server.LanUrl);  // LanUrl 必须携带新码
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task UploadStatus_IllegalPath_Returns400()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await http.GetAsync(
                $"http://localhost:{port}/api/files/upload-status?t={server.Token}"
                + $"&name={Uri.EscapeDataString("../evil.txt")}&size=100&mtime=1");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Pair_CorrectCode_ReturnsTokenThatAccessesApi()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await PairAsync(http, port, server.PairCode);
            resp.EnsureSuccessStatusCode();

            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            string exchanged = doc.RootElement.GetProperty("token").GetString()!;
            Assert.Equal(server.Token, exchanged);
            Assert.True(doc.RootElement.GetProperty("expiresInMinutes").GetInt32() > 0);

            // 换来的令牌必须真的能访问受保护接口
            HttpResponseMessage api = await http.GetAsync($"http://localhost:{port}/api/files?t={exchanged}");
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Pair_ConsumedCode_ReplayFailsWith401()
    {
        // 一次性消费语义（PairingService.TryConsume）：手机提交配对码换 token 成功即消费，
        // 同一个 6 位码不能在有效期内反复兑换（泄露窗口从「10 分钟」缩到「首次成功为止」）
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            string code = server.PairCode;

            HttpResponseMessage first = await PairAsync(http, port, code);
            first.EnsureSuccessStatusCode();

            HttpResponseMessage replay = await PairAsync(http, port, code);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Pair_WrongCode_Returns401()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await PairAsync(http, port, "ZZZZZZ");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task DownloadZip_SelectedFiles_AllPresentInArchive()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "AAA");
            await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "BBB");

            var log = new CapturingLogger();
            await using var server = new FileWebServer(logger: log);
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await http.GetAsync(
                $"http://localhost:{port}/api/files/download-zip?t={server.Token}&paths=a.txt|b.txt");
            if (!resp.IsSuccessStatusCode)
            {
                Assert.Fail($"HTTP {(int)resp.StatusCode}。服务端日志：{string.Join(" | ", log.Messages)}");
            }

            using var zip = new ZipArchive(await resp.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
            Assert.Equal(2, zip.Entries.Count);
            Assert.Contains("a.txt", zip.Entries.Select(e => e.FullName));
            Assert.Contains("b.txt", zip.Entries.Select(e => e.FullName));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task DownloadZip_OutOfRootPath_SkippedWhileKeepingValidOnes()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ok.txt"), "OK");

            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            // 混入一个越界路径：应被静默跳过，而不是让整个下载失败
            HttpResponseMessage resp = await http.GetAsync(
                $"http://localhost:{port}/api/files/download-zip?t={server.Token}"
                + "&paths=" + Uri.EscapeDataString("ok.txt|../secret.txt"));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var zip = new ZipArchive(await resp.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
            Assert.Single(zip.Entries);
            Assert.Equal("ok.txt", zip.Entries[0].FullName);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Api_Devices_WithoutDiscovery_StillReturnsLocalDevice()
    {
        // 2026-09-11 主人反馈「局域网设备显示 0」：设备发现服务只收录**其它**设备
        // （ProcessDatagram 显式过滤自身广播），单机场景下列表恒空——连服务器自己都看不到。
        // 故本机条目由服务端合成，**不依赖**发现服务是否注入。
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await http.GetAsync($"http://localhost:{port}/api/devices?t={server.Token}");
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(1, doc.RootElement.GetArrayLength());

            JsonElement local = doc.RootElement[0];
            // isLocal 前端据此渲染「本机」角标、隐藏「打开」按钮（点开等同刷新当前页）
            Assert.True(local.GetProperty("isLocal").GetBoolean());
            Assert.Equal(System.Environment.MachineName, local.GetProperty("name").GetString());
            // 端口必须是 Web 端口：手机访问电脑只走这个入口，填 0 会显示成 "192.168.x.x:0"
            Assert.Equal(port, local.GetProperty("transferPort").GetInt32());
            Assert.True(local.GetProperty("isOnline").GetBoolean());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("//host/share/evil.txt")]
    public async Task UploadChunk_TraversalOrAbsolutePath_RejectedWith400(string evilPath)
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            byte[] data = Encoding.UTF8.GetBytes("evil");
            HttpResponseMessage resp = await PostChunkAsync(http, port, server.Token, evilPath, 4, 1, 0, data);

            // 严格拒绝而非静默剥离：客户端应当知道自己传了非法路径
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            // 共享目录之外绝不能出现任何文件
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "evil.txt")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
