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
    /// <remarks>
    /// 🟡 审查 v8-🟡-13：<see cref="Messages"/> 必须是**加锁快照**而非裸 <c>List</c>——
    /// 写入发生在 Kestrel 请求线程/启动线程，断言侧却直接枚举，收尾日志可能还在写，
    /// 裸 List 会抛 <c>InvalidOperationException: Collection was modified</c>
    /// （与 2026-09-14 刚修过的 <c>BusCapture</c> 同源；此前靠时序侥幸通过）。
    /// </remarks>
    private sealed class CapturingLogger : SystemToolkit.Core.Contracts.ILogger
    {
        private readonly object _gate = new();

        private readonly List<string> _messages = new();

        /// <summary>已捕获日志的**只读快照**（遍历期间集合不变是结构保证）。</summary>
        public List<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return new List<string>(_messages);
                }
            }
        }

        public void Info(string message) => Add("[INFO] " + message);

        public void Warn(string message) => Add("[WARN] " + message);

        public void Error(string message, Exception? ex = null) => Add("[ERROR] " + message + " " + ex);

        private void Add(string line)
        {
            lock (_gate)
            {
                _messages.Add(line);
            }
        }
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
    /// 手机通道也遵守同名冲突策略（2026-09-13 批次 P1 ⑥）：跳过则**一份新文件都不留**、
    /// 覆盖则替换原文件。两条通道口径必须一致——否则"同一次操作、两台设备结果不同"。
    /// </summary>
    [Fact]
    public async Task WriteUploadedFile_RespectsConflictPolicy_SkipAndOverwrite()
    {
        string skipDir = NewTempDir();
        string overwriteDir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(skipDir, "a.txt"), "keep-me");
            File.WriteAllText(Path.Combine(overwriteDir, "a.txt"), "old");

            await using (var skipServer = new FileWebServer())
            {
                TransferSettings skipSettings = MakeSettings(FreeTcpPort());
                skipSettings.ConflictPolicy = SystemToolkit.Core.FileTransfer.Models.TransferConflictPolicy.Skip;
                await skipServer.StartAsync(skipSettings, skipDir);
                await skipServer.WriteUploadedFileAsync("a.txt", new MemoryStream(Encoding.UTF8.GetBytes("new")));
            }

            Assert.Equal("keep-me", await File.ReadAllTextAsync(Path.Combine(skipDir, "a.txt")));
            Assert.False(File.Exists(Path.Combine(skipDir, "a (1).txt")), "跳过策略不得留下任何新文件");
            Assert.False(
                Directory.EnumerateFiles(skipDir, ".upload_*.part").Any(),
                "跳过策略应丢弃已上传的临时断点，不留在共享目录里");

            await using (var overwriteServer = new FileWebServer())
            {
                TransferSettings overwriteSettings = MakeSettings(FreeTcpPort());
                overwriteSettings.ConflictPolicy = SystemToolkit.Core.FileTransfer.Models.TransferConflictPolicy.Overwrite;
                await overwriteServer.StartAsync(overwriteSettings, overwriteDir);
                await overwriteServer.WriteUploadedFileAsync("a.txt", new MemoryStream(Encoding.UTF8.GetBytes("new")));
            }

            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(overwriteDir, "a.txt")));
            Assert.False(File.Exists(Path.Combine(overwriteDir, "a (1).txt")), "覆盖策略不该再产生序号副本");
        }
        finally
        {
            DeleteTempDir(skipDir);
            DeleteTempDir(overwriteDir);
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
            // id：前端与 serverInfo.clientId 比对，认出「哪条浏览器是我」并打「本机」角标
            // （用 `"id":"` 精确匹配，避免被 ipAddress 之类的子串误判）
            Assert.Contains("\"id\":\"", second);

            // 第三帧：服务器信息（前端 serverInfo 用于渲染主机名）
            string third = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"serverInfo\"", third);
            // clientId：手机端据此把「本机」标在自己那条浏览器上，而不是标在电脑上
            Assert.Contains("\"clientId\":\"", third);

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

    /// <summary>
    /// 🟠 审查 v8-🟠-3：上传路径里的**零宽字符必须被剥离**——与 <c>WriteUploadedFileAsync</c>、
    /// <c>FileTransferService.SanitizeFileName</c> 共用同一判据（<c>TextSanitizer.StripInvisible</c>）。
    /// <para>
    /// 不剥离的后果：落盘 <c>a\u200Bb.txt</c> 这类文件名——手机端**看不见**那个字符，
    /// 但文件名对不上，复制/搜索/再次上传全都匹配不到。
    /// </para>
    /// <para>反向验证：去掉 <c>SanitizeRelativePath</c> 每段的 <c>StripInvisible</c> → 本用例变红
    /// （磁盘上会出现含 U+200B 的文件名）。</para>
    /// </summary>
    [Fact]
    public async Task UploadChunk_ZeroWidthInName_StrippedBeforeLanding()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            byte[] data = Encoding.UTF8.GetBytes("zw");
            // 零宽空格（U+200B）夹在中间：合法文件名字符，故旧实现会原样落盘
            (await PostChunkAsync(http, port, server.Token, "a\u200Bb.txt", 2, 1, 0, data))
                .EnsureSuccessStatusCode();

            Assert.True(File.Exists(Path.Combine(dir, "ab.txt")), "零宽字符应被剥离后落盘为 ab.txt");
            Assert.DoesNotContain(
                Directory.GetFiles(dir),
                f => Path.GetFileName(f).IndexOf('\u200b') >= 0);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 🟠 审查 v8-🟠-3：**只由零宽字符组成**的段剥离后为空 → 必须拒绝（而不是落一个空名文件）。
    /// <para>
    /// ⚠️ U+200B 不是空白字符（<c>char.IsWhiteSpace</c> 为假），所以入口的
    /// <c>IsNullOrWhiteSpace</c> 挡不住它——必须靠剥离后的空判据。
    /// </para>
    /// </summary>
    [Fact]
    public async Task UploadChunk_ZeroWidthOnlySegment_RejectedWith400()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            byte[] data = Encoding.UTF8.GetBytes("x");
            HttpResponseMessage resp = await PostChunkAsync(
                http, port, server.Token, "\u200b\u200b", 1, 1, 0, data);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            // 断言拒绝**理由**：剥离后为空属"非法路径"，而不是被别的原因顺带挡下
            Assert.Contains("非法的上传路径", await resp.Content.ReadAsStringAsync());
            Assert.Empty(Directory.GetFiles(dir));
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
            // 🔴 2026-09-13 起令牌按**设备**签发（协议 §5.3）：它**不再**等于本机预览令牌。
            // 旧断言 `Assert.Equal(server.Token, exchanged)` 是"单一全局令牌"时代的产物——
            // 那次改动同时让"踢出某台手机"成为可能（见 Pair_IssuesPerDeviceSession_…）。
            // 判据不放松：仍是同规格的 32 位 hex 随机串，且必须真的能访问受保护接口。
            Assert.NotEqual(server.Token, exchanged);
            Assert.Equal(32, exchanged.Length);
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

    /// <summary>
    /// 下载响应必须**同时**给出 ASCII 回退名与 RFC 5987 的 UTF-8 名。
    /// <para>
    /// 2026-09-11 主人反馈「手机端下载下来的文件名被改」：原先走
    /// <c>Results.File(..., fileDownloadName)</c>，非 ASCII 字符只进 <c>filename*</c> 段，
    /// **部分手机浏览器/系统下载器不认 filename***，会退化成转义串或 URL 末段当名字。
    /// 故改为手工构造两段并保留 URL 末段带名。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Download_NonAsciiFileName_ProvidesAsciiFallbackAndUtf8Star()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            // 中文名：ASCII 段承载不了它（会被替换为 _），原名只能靠 filename* 传递
            const string fileName = "报告2026.txt";
            await File.WriteAllTextAsync(Path.Combine(dir, fileName), "hello");

            using var http = new HttpClient();
            string url = $"http://localhost:{port}/api/files/download/{Uri.EscapeDataString(fileName)}"
                + $"?path={Uri.EscapeDataString(fileName)}&t={server.Token}";
            HttpResponseMessage resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            Assert.True(
                resp.Content.Headers.TryGetValues("Content-Disposition", out IEnumerable<string>? values),
                "下载响应必须带 Content-Disposition");
            string cd = string.Join(";", values!);

            // ASCII 回退：中文被替换为下划线，但**扩展名必须保住**（丢了扩展名手机无法正确打开）
            Assert.Contains("filename=\"__2026.txt\"", cd);
            // RFC 5987：支持 filename* 的客户端据此还原中文原名
            Assert.Contains("filename*=UTF-8''", cd);
            Assert.Contains(Uri.EscapeDataString(fileName), cd);
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

    /// <summary>
    /// 证书信息端点（协议 §6.3，2026-09-13 P0）：**免令牌**且 HTTP 模式下无指纹。
    /// <para>必须免令牌——手机端要"早于配对"就能拿到基线指纹，否则首次访问没有基线，
    /// 之后的"证书已变更"提示就无从判断（JS 拿不到 TLS 层证书，只能靠服务端告知）。</para>
    /// </summary>
    [Fact]
    public async Task CertInfo_IsPublic_ReportsHttpModeWithoutFingerprint()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            HttpResponseMessage resp = await http.GetAsync($"http://localhost:{port}/api/cert-info");
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("https").GetBoolean());
            Assert.Equal(string.Empty, doc.RootElement.GetProperty("fingerprint").GetString());
            Assert.Equal(string.Empty, server.CertFingerprint);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 会话模型（协议 §5.3，2026-09-13 P0）：配对签发**该设备专属**令牌；踢出只作废它，
    /// 本机预览令牌必须仍然可用（否则桌面「打开网页」会跟着失效）。
    /// <para>反向验证：把 <c>RevokeAllSessions</c> 改成连本机预览一起清，最后一条断言即变红。</para>
    /// </summary>
    [Fact]
    public async Task Pair_IssuesPerDeviceSession_RevokeInvalidatesOnlyThatDevice()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "hello");

            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            using var http = new HttpClient();

            // 配对前：没有任何远程会话（本机预览不算"来访设备"）
            Assert.Empty(server.Sessions);

            HttpResponseMessage paired = await PairAsync(http, port, server.PairCode);
            paired.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await paired.Content.ReadAsStringAsync());
            string deviceToken = doc.RootElement.GetProperty("token").GetString()!;

            // 每台设备独立令牌，且与本机预览令牌不同
            Assert.NotEqual(server.Token, deviceToken);
            WebSessionInfo session = Assert.Single(server.Sessions);
            Assert.False(string.IsNullOrWhiteSpace(session.Label));
            Assert.False(session.IsLocalPreview);

            // 该令牌可用
            (await http.GetAsync($"http://localhost:{port}/api/files?t={deviceToken}")).EnsureSuccessStatusCode();

            // 踢出：设备令牌立即失效，本机预览不受影响
            Assert.Equal(1, server.RevokeAllSessions());
            Assert.Empty(server.Sessions);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await http.GetAsync($"http://localhost:{port}/api/files?t={deviceToken}")).StatusCode);
            (await http.GetAsync($"http://localhost:{port}/api/files?t={server.Token}")).EnsureSuccessStatusCode();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 孤儿上传断点清理（协议 §4.3 🟠，2026-09-13 P0）：服务启动时删掉超过 7 天未续传的
    /// <c>.upload_*.part</c>，但**保留未超期的**（用户可能过会儿接着传，删了等于毁进度）。
    /// </summary>
    [Fact]
    public async Task StartAsync_RemovesOnlyStaleOrphanUploadParts()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            string stale = Path.Combine(dir, ".upload_aaaa.part");
            string recent = Path.Combine(dir, ".upload_bbbb.part");
            await File.WriteAllTextAsync(stale, "old");
            await File.WriteAllTextAsync(recent, "new");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8)); // 超过 7 天保留期

            var logger = new CapturingLogger();
            await using var server = new FileWebServer(null, null, logger);
            await server.StartAsync(MakeSettings(port), dir);

            Assert.False(File.Exists(stale), "超过 7 天的孤儿断点应在服务启动时被清理");
            Assert.True(File.Exists(recent), "未超期的断点必须保留（可能是用户稍后要续传的文件）");
            Assert.Contains(logger.Messages, m => m.Contains("孤儿上传断点"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ==================================================================
    // W1a：上传状态推送（transferUpdate）
    // ==================================================================

    /// <summary>
    /// 连上 <c>/ws</c> 并吃掉首帧三连（deviceList → browserList → serverInfo），
    /// 之后 <c>ReceiveTextAsync</c> 拿到的就是业务推送。
    /// </summary>
    private static async Task<System.Net.WebSockets.ClientWebSocket> ConnectWsAsync(int port, string token)
    {
        var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?t={token}"), CancellationToken.None);
        string first = await ReceiveTextAsync(ws);
        string second = await ReceiveTextAsync(ws);
        string third = await ReceiveTextAsync(ws);
        Assert.Contains("\"type\":\"deviceList\"", first);
        Assert.Contains("\"type\":\"browserList\"", second);
        Assert.Contains("\"type\":\"serverInfo\"", third);
        return ws;
    }

    /// <summary>
    /// W1a：分块上传应在「每块落定」时推 <c>Transferring</c>、在「定稿」时推 <c>Completed</c>。
    /// <para>
    /// 反向验证：移除 upload-chunk 端点里的两处 <c>BroadcastTransferUpdateAsync</c> → 本用例超时/断言失败变红。
    /// </para>
    /// </summary>
    [Fact]
    public async Task UploadChunk_ProgressThenFinalize_BroadcastsTransferUpdate()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

            // 两块文件：第一块只落一半 → 服务端必须推一条「传输中」的真实进度
            byte[] data = new byte[80];
            HttpResponseMessage first = await PostChunkAsync(http, port, server.Token, "chat.bin", 80, 1, 0, data[..40]);
            Assert.True(first.IsSuccessStatusCode);

            string progress = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"transferUpdate\"", progress);
            Assert.Contains("\"status\":\"Transferring\"", progress);
            Assert.Contains("\"fileName\":\"chat.bin\"", progress);
            Assert.Contains("\"transferredBytes\":40", progress);
            Assert.Contains("\"totalBytes\":80", progress);

            // 第二块收尾 → 定稿，推「已完成」
            HttpResponseMessage second = await PostChunkAsync(http, port, server.Token, "chat.bin", 80, 1, 40, data[40..]);
            Assert.True(second.IsSuccessStatusCode);

            string done = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"transferUpdate\"", done);
            Assert.Contains("\"status\":\"Completed\"", done);
            Assert.Contains("\"skipped\":false", done);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W1a：「按同名策略跳过」必须推 <c>Skipped</c> 且带原因码 ——
    /// 推成 <c>Completed</c> 就是状态欺骗（目标目录里并没有新增文件）。
    /// </summary>
    [Fact]
    public async Task UploadChunk_ConflictSkip_BroadcastsSkippedWithReasonCode()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            TransferSettings settings = MakeSettings(port);
            settings.ConflictPolicy = SystemToolkit.Core.FileTransfer.Models.TransferConflictPolicy.Skip;
            await using var server = new FileWebServer();
            await server.StartAsync(settings, dir);

            await File.WriteAllTextAsync(Path.Combine(dir, "dup.txt"), "已存在的旧内容");

            using var http = new HttpClient();
            using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

            byte[] data = Encoding.UTF8.GetBytes("新内容");
            HttpResponseMessage resp = await PostChunkAsync(http, port, server.Token, "dup.txt", data.Length, 3, 0, data);
            Assert.True(resp.IsSuccessStatusCode);

            string frame = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"transferUpdate\"", frame);
            Assert.Contains("\"status\":\"Skipped\"", frame);
            Assert.Contains("\"skipped\":true", frame);
            Assert.Contains("\"reasonCode\":\"CONFLICT_SKIP\"", frame);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W1a：**参数校验类拒绝不推**（这里是文件大小超限 413）。
    /// <para>
    /// 理由：那是"请求错误"而非传输失败，调用方当场就拿到了 HTTP 错误；
    /// 推给所有已连浏览器只是噪音，还会让"有人在传大文件失败"的错觉扩散。<br/>
    /// 断言手法：拒绝之后紧跟一次 <c>ping</c>——若中间推过任何东西，
    /// <c>pong</c> 之前就会先收到那一帧（次序断言，不靠等待超时，避免用例抖动）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task UploadChunk_ValidationRejection_DoesNotBroadcastTransferUpdate()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

            byte[] data = new byte[4];
            HttpResponseMessage bad = await PostChunkAsync(http, port, server.Token, "huge.bin", long.MaxValue, 1, 0, data);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bad.StatusCode);

            await ws.SendAsync(
                Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"),
                System.Net.WebSockets.WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            string next = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"pong\"", next);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ==================================================================
    // W2a：文本通道（POST /api/text + WS chatMessage）
    // ==================================================================

    /// <summary>发一条文本到 <c>/api/text</c>，返回原始响应（调用方自行断言状态码）。</summary>
    private static async Task<HttpResponseMessage> PostTextAsync(
        HttpClient http, int port, string token, string text, string? clientId = null)
    {
        string url = $"http://localhost:{port}/api/text?t={token}";
        string json = clientId is null
            ? JsonSerializer.Serialize(new { text })
            : JsonSerializer.Serialize(new { text, clientId });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await http.PostAsync(url, content);
    }

    /// <summary>
    /// 连上 <c>/ws</c> 并额外解析出 <c>serverInfo</c> 里的 <c>clientId</c>（W2 的排除回声用）。
    /// </summary>
    /// <summary>
    /// 取 WS 帧里 <c>payload.text</c> 的原文。
    /// <para>
    /// 为什么不直接 <c>Assert.Contains(中文, frame)</c>：WS 序列化用的默认编码器会把非 ASCII
    /// 转义成 <c>\uXXXX</c>（对 <c>JSON.parse</c> 无影响，但字符串匹配会扑空）。解析后再断言，
    /// 顺带把"帧结构是对的"也钉住了。
    /// </para>
    /// </summary>
    private static string PayloadText(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        return doc.RootElement.GetProperty("payload").GetProperty("text").GetString()!;
    }

    private static async Task<string> ConnectWsWithIdAsync(
        int port, string token, System.Net.WebSockets.ClientWebSocket ws)
    {
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?t={token}"), CancellationToken.None);
        string first = await ReceiveTextAsync(ws);
        string second = await ReceiveTextAsync(ws);
        string third = await ReceiveTextAsync(ws);
        Assert.Contains("\"type\":\"serverInfo\"", third);
        using var doc = JsonDocument.Parse(third);
        return doc.RootElement.GetProperty("payload").GetProperty("clientId").GetString()!;
    }

    /// <summary>
    /// W2：手机发文本 → 服务端触发 <c>TextReceived</c>（电脑上屏），并**回声给其它标签页**。
    /// <para>
    /// 反向验证：删掉端点里的 <c>TextReceived?.Invoke</c> → 事件断言红；
    /// 把 <c>excludeId</c> 恒传 <c>null</c> → 「发送方自己不该收到」的次序断言红。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PostText_WithClientId_RaisesEventAndEchoesToOtherTabsOnly()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            var received = new List<WebTextReceivedEventArgs>();
            server.TextReceived += (_, e) => received.Add(e);

            using var http = new HttpClient();
            using var a = new System.Net.WebSockets.ClientWebSocket();
            string aId = await ConnectWsWithIdAsync(port, server.Token, a);
            using System.Net.WebSockets.ClientWebSocket b = await ConnectWsAsync(port, server.Token);

            // B 加入会给 A 推一次 browserList 增量——先吃掉，否则 A 的接收队列不干净，
            // 「A 不该收到回声」的断言会误判成收到。
            string joinNotice = await ReceiveTextAsync(a);
            Assert.Contains("\"type\":\"browserList\"", joinNotice);

            HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, "你好，世界", aId);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            // ① 电脑上屏：事件如实转达全文与字节数（5 个汉字 = UTF-8 15 字节）
            Assert.Single(received);
            Assert.Equal("你好，世界", received[0].Text);
            Assert.Equal(5, received[0].CharCount);
            Assert.Equal(15, received[0].ByteCount);

            // ② 其它标签页收到回声
            string echo = await ReceiveTextAsync(b);
            Assert.Contains("\"type\":\"chatMessage\"", echo);
            Assert.Contains("\"origin\":\"phone\"", echo);
            Assert.Equal("你好，世界", PayloadText(echo));

            // ③ 发送方自己**不该**再收到一份（本地已上屏，再来一条就是重复气泡）。
            //    用 ping/pong 次序断言（同 W1a）：中间若插过任何帧，pong 就不是下一帧。
            await a.SendAsync(
                Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"),
                System.Net.WebSockets.WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
            string next = await ReceiveTextAsync(a);
            Assert.Contains("\"type\":\"pong\"", next);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：<c>clientId</c> 认不出（老客户端 / 连接已断）时**退化为广播给所有人**——
    /// 宁可让发送方多一条重复的，也不让"别人发了消息我这没显示"。
    /// </summary>
    [Fact]
    public async Task PostText_WithoutClientId_EchoesToEveryoneIncludingSender()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

            HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, "hello");
            Assert.True(resp.IsSuccessStatusCode);

            string echo = await ReceiveTextAsync(ws);
            Assert.Contains("\"type\":\"chatMessage\"", echo);
            Assert.Contains("hello", echo);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>W2：空白文本 400（发一条空白没有任何意义，且会让对端剪贴板被空格覆盖）。</summary>
    [Fact]
    public async Task PostText_Blank_ReturnsBadRequest()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            using var http = new HttpClient();

            HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, "   ");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：超出 256 KB（按 UTF-8 字节）→ 413 + <c>TEXT_TOO_LONG</c>，且**不触发** <c>TextReceived</c>。
    /// 拒绝而非静默截断：截断会把一条长链接变成失效链接，而发送方看到的却是"发送成功"。
    /// </summary>
    [Fact]
    public async Task PostText_TooLong_Returns413AndDoesNotRaiseEvent()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            int raised = 0;
            server.TextReceived += (_, _) => raised++;

            using var http = new HttpClient();
            string tooLong = new string('a', (256 * 1024) + 1);
            HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, tooLong);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
            Assert.Contains(TransferReasonCodes.TextTooLong, await resp.Content.ReadAsStringAsync());
            Assert.Equal(0, raised);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：正文超过 1 MB 直接 413——**在读 body 之前**就用 Content-Length 挡掉。
    /// 少了这一道，1 GB 的 body 会被完整读进内存才轮到长度校验。
    /// </summary>
    [Fact]
    public async Task PostText_BodyOverLimit_Returns413()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            using var http = new HttpClient();

            string huge = new string('x', 2 * 1024 * 1024);
            HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, huge);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>W2：发文本与传文件同级，鉴权不得降级。</summary>
    [Fact]
    public async Task PostText_WithoutToken_ReturnsUnauthorized()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            using var http = new HttpClient();

            using var content = new StringContent("{\"text\":\"hi\"}", Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await http.PostAsync($"http://localhost:{port}/api/text", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>W2：正文不是合法 JSON → 400（而不是 500）。</summary>
    [Fact]
    public async Task PostText_NotJson_ReturnsBadRequest()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            using var http = new HttpClient();

            using var content = new StringContent("这不是 JSON", Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await http.PostAsync(
                $"http://localhost:{port}/api/text?t={server.Token}", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：<c>text</c> 字段类型不符（给了数字）→ 400。
    /// 🔴 这条钉的是「先验 <c>ValueKind</c> 再取值」——<c>JsonElement.TryGetXxx</c>
    /// 对类型不符是**抛异常**（只有键不存在才返 false），直接 <c>GetString()</c> 会把它变成 500。
    /// </summary>
    [Fact]
    public async Task PostText_TextFieldNotString_ReturnsBadRequest()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            using var http = new HttpClient();

            using var content = new StringContent("{\"text\":123}", Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await http.PostAsync(
                $"http://localhost:{port}/api/text?t={server.Token}", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：订阅方（电脑上屏逻辑）抛异常时**仍返回 200**——
    /// 服务端已经收下文本，此时再回 500 是状态自相矛盾（发送方会以为没发出去而重发）。
    /// </summary>
    [Fact]
    public async Task PostText_SubscriberThrows_StillReturnsOk()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);
            server.TextReceived += (_, _) => throw new InvalidOperationException("模拟订阅方崩了");

            using var http = new HttpClient();
            HttpResponseMessage resp = await PostTextAsync(http, port, server.Token, "hi");
            Assert.True(resp.IsSuccessStatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：电脑 → 手机推文本（<c>BroadcastTextAsync</c>）→ 所有浏览器收到 <c>chatMessage</c>，
    /// 返回**真实送达数**。
    /// </summary>
    [Fact]
    public async Task BroadcastTextAsync_WithBrowsers_DeliversChatMessage()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using System.Net.WebSockets.ClientWebSocket first = await ConnectWsAsync(port, server.Token);
            using System.Net.WebSockets.ClientWebSocket second = await ConnectWsAsync(port, server.Token);
            // first 会因 second 加入收到一次 browserList 增量——先吃掉再断言业务帧
            Assert.Contains("\"type\":\"browserList\"", await ReceiveTextAsync(first));

            int delivered = await server.BroadcastTextAsync("来自电脑的一段话");
            Assert.Equal(2, delivered);

            string a = await ReceiveTextAsync(first);
            Assert.Contains("\"type\":\"chatMessage\"", a);
            Assert.Contains("\"origin\":\"desktop\"", a);
            Assert.Equal("来自电脑的一段话", PayloadText(a));
            Assert.Contains("\"type\":\"chatMessage\"", await ReceiveTextAsync(second));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：无人在线 → 送达 0（**不是**抛异常，也不是假报成功）。
    /// 调用方据此告诉用户"当前没有已连接的手机"。
    /// </summary>
    [Fact]
    public async Task BroadcastTextAsync_NoBrowser_ReturnsZero()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            int delivered = await server.BroadcastTextAsync("没人在线");
            Assert.Equal(0, delivered);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// W2：<c>BroadcastTextAsync</c> 与 <c>FileTransferService.SendTextAsync</c> 同判据——
    /// 空文本与超长文本一律抛，绝不静默截断后回一个假的送达数。
    /// </summary>
    [Fact]
    public async Task BroadcastTextAsync_Invalid_Throws()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            await Assert.ThrowsAsync<ArgumentException>(() => server.BroadcastTextAsync("   "));
            await Assert.ThrowsAsync<ArgumentException>(
                () => server.BroadcastTextAsync(new string('a', (256 * 1024) + 1)));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ==================================================================
    // W2b：外壳资源不缓存
    // ==================================================================

    /// <summary>
    /// W2b：静态外壳（index / app.js / style.css）必须显式带禁止缓存头。
    /// <para>
    /// 为什么值得一条锁：三个文件合计 &lt;200 KB，而"手机上加载到旧 app.js"会让整轮改动
    /// 看起来像"没做"（W1b → W2b 之间就因此多了一轮往返）。这不是性能问题，是**可信度**问题。
    /// </para>
    /// <para>反向验证：删掉 <c>ServeEmbedded</c> 里的 <c>CacheControl</c> 赋值 → 本用例变红。</para>
    /// </summary>
    [Fact]
    public async Task StaticShell_IsServedWithNoCacheHeaders()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            foreach (string asset in new[] { "/index.html", "/app.js", "/style.css" })
            {
                HttpResponseMessage resp = await http.GetAsync($"http://localhost:{port}{asset}");
                Assert.True(resp.IsSuccessStatusCode, asset + " 应可访问");
                string? cacheControl = resp.Headers.CacheControl?.ToString();
                Assert.False(string.IsNullOrEmpty(cacheControl), asset + " 缺少 Cache-Control 头");
                Assert.Contains("no-store", cacheControl!);
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
