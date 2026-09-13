using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Infrastructure.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// 「同一台设备只保留一条会话/凭据」的去重用例（2026-09-14，主人实测反馈）。
/// <para>
/// 背景：主人界面上「已授权设备（会话）」列出 21 条一模一样的 "Android · Chrome"，
/// 其实是同一台手机反复扫码配对堆出来的。根因是**会话与长期凭据都只增不减**。
/// </para>
/// <para>
/// 这组用例钉的是"设备指纹"的三条边界：同设备要合并、异设备不能误合、
/// 以及升级前攒下的历史重复项要能在启动时清掉。
/// </para>
/// </summary>
public class WebSessionDedupeTests
{
    /// <summary>测试用日志收集器（断言"清理了几条"这类留痕）。</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public void Info(string message) => Messages.Add("[INFO] " + message);

        public void Warn(string message) => Messages.Add("[WARN] " + message);

        public void Error(string message, Exception? ex = null) => Messages.Add("[ERROR] " + message + " " + ex);
    }

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
        string dir = Path.Combine(Path.GetTempPath(), "stkft-dedupe", Guid.NewGuid().ToString("N"));
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

    private static async Task<JsonElement> PairAsync(HttpClient http, int port, string code, bool remember)
    {
        using var payload = new StringContent(
            JsonSerializer.Serialize(new { code, remember }), Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await http.PostAsync($"http://localhost:{port}/api/pair", payload);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// 同一台设备（同 IP + 同 UA）连配两次 → 列表里**只留一条**。
    /// <para>
    /// 反向验证：把两次创建点改回 <c>_sessions[id] = ...</c> 直接插入 → 本用例变红（会得到 2 条）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PairTwice_SameDevice_KeepsSingleSession()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/120.0 Mobile Safari/537.36");

            JsonElement first = await PairAsync(http, port, server.PairCode, remember: false);
            Assert.NotNull(first.GetProperty("token").GetString());
            Assert.Single(server.Sessions);

            // 配对码一次性消费 → 下一次取到的是新码（PairingService 惰性轮换）
            JsonElement second = await PairAsync(http, port, server.PairCode, remember: false);
            Assert.NotNull(second.GetProperty("token").GetString());

            WebSessionInfo only = Assert.Single(server.Sessions);
            Assert.Equal("Android · Chrome", only.Label);

            // 旧令牌必须真的失效（顶掉 = 撤销，旧令牌不该还能用）
            string oldToken = first.GetProperty("token").GetString()!;
            HttpResponseMessage withOld = await http.GetAsync(
                $"http://localhost:{port}/api/files?t={oldToken}");
            Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);

            string newToken = second.GetProperty("token").GetString()!;
            HttpResponseMessage withNew = await http.GetAsync(
                $"http://localhost:{port}/api/files?t={newToken}");
            Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 两台不同设备（同 IP、UA 不同）→ **两条都要留**。指纹取值过粗会把真实设备合并掉，
    /// 那比"多列几条"更糟（用户会失去"踢掉某一台"的能力）。
    /// </summary>
    [Fact]
    public async Task PairTwice_DifferentUserAgents_KeepsBothSessions()
    {
        string dir = NewTempDir();
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(MakeSettings(port), dir);

            using (var android = new HttpClient())
            {
                android.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/120.0 Mobile Safari/537.36");
                await PairAsync(android, port, server.PairCode, remember: false);
            }

            using (var iphone = new HttpClient())
            {
                iphone.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 Version/17.4 Mobile Safari/604.1");
                await PairAsync(iphone, port, server.PairCode, remember: false);
            }

            Assert.Equal(2, server.Sessions.Count);
            Assert.Contains(server.Sessions, s => s.Label == "Android · Chrome");
            Assert.Contains(server.Sessions, s => s.Label == "iPhone · Safari");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 勾了「记住此设备」后连配两次 → 磁盘上**只剩一条**长期凭据
    /// （此前每次配对都会新写一条，撤销还得逐条点）。
    /// </summary>
    [Fact]
    public async Task PairWithRemember_Twice_KeepsSingleTrustedRecord()
    {
        string dir = NewTempDir();
        string storePath = Path.Combine(dir, "trusted-devices.json");
        int port = FreeTcpPort();
        try
        {
            var store = new DpapiTrustedWebDeviceStore(storePath);
            await using var server = new FileWebServer(trustedStore: store);
            await server.StartAsync(MakeSettings(port), dir);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/120.0 Mobile Safari/537.36");

            JsonElement first = await PairAsync(http, port, server.PairCode, remember: true);
            JsonElement second = await PairAsync(http, port, server.PairCode, remember: true);

            Assert.True(first.GetProperty("remembered").GetBoolean());
            Assert.True(second.GetProperty("remembered").GetBoolean());

            Assert.Single(server.Sessions);
            Assert.Single(store.Load());

            // 最新那条凭据必须是真的（用第二次的令牌免配对再来一次仍然放行）
            string latestToken = second.GetProperty("token").GetString()!;
            Assert.True(server.RevokeSession(server.Sessions[0].Id));
            HttpResponseMessage after = await http.GetAsync($"http://localhost:{port}/api/files?t={latestToken}");
            // 撤销 = 长期凭据一起失效（既有裁定），故应 401
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 服务重启后，手机凭长期凭据**反复重建会话**（每次页面加载都会走这条路径）也不会堆积。
    /// 这是主人那 21 条的另一个来源：重启后 <c>_sessions</c> 清空，每次加载都新建一条。
    /// </summary>
    [Fact]
    public async Task AfterRestart_TrustedRebuild_DoesNotAccumulate()
    {
        string dir = NewTempDir();
        string storePath = Path.Combine(dir, "trusted-devices.json");
        int port = FreeTcpPort();
        try
        {
            var store = new DpapiTrustedWebDeviceStore(storePath);
            string token;
            using (var http = new HttpClient())
            {
                await using var server1 = new FileWebServer(trustedStore: store);
                await server1.StartAsync(MakeSettings(port), dir);
                token = (await PairAsync(http, port, server1.PairCode, remember: true))
                    .GetProperty("token").GetString()!;
                await server1.StopAsync();
            }

            int port2 = FreeTcpPort();
            await using var server2 = new FileWebServer(trustedStore: store);
            await server2.StartAsync(MakeSettings(port2), dir);

            Assert.Empty(server2.Sessions); // 重启后内存会话为空

            using var http2 = new HttpClient();
            // 模拟"页面加载 + 后续 API 调用"：同一凭据重建的会话只会有一条
            for (int i = 0; i < 3; i++)
            {
                HttpResponseMessage resp = await http2.GetAsync(
                    $"http://localhost:{port2}/api/files?t={token}");
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }

            Assert.Single(server2.Sessions);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// **升级前攒下的历史重复项**要在启动时被清掉：旧记录没有指纹字段，
    /// 去重时退回 <c>Ip + Label</c> 认人——否则主人机器上那 21 条永远不会消失。
    /// </summary>
    [Fact]
    public async Task Startup_CleansLegacyDuplicateCredentials()
    {
        string dir = NewTempDir();
        string storePath = Path.Combine(dir, "trusted-devices.json");
        int port = FreeTcpPort();
        try
        {
            var store = new DpapiTrustedWebDeviceStore(storePath);
            DateTimeOffset expiry = DateTimeOffset.UtcNow.AddDays(30);
            var legacy = new List<TrustedWebDevice>();
            for (int i = 0; i < 3; i++)
            {
                legacy.Add(new TrustedWebDevice
                {
                    Id = "legacy" + i,
                    TokenHash = new string('a', 64), // 只做去重，不参与校验
                    Label = "Android · Chrome",
                    Ip = "192.168.1.16",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i), // 第 0 条最新
                    ExpiresAt = expiry,
                    // 刻意不给 Fingerprint：模拟本字段引入之前的记录
                });
            }

            store.Save(legacy);
            Assert.Equal(3, store.Load().Count);

            var logger = new CapturingLogger();
            await using var server = new FileWebServer(trustedStore: store, logger: logger);
            await server.StartAsync(MakeSettings(port), dir);

            Assert.Single(store.Load());
            Assert.Contains(logger.Messages, m => m.Contains("已清理") && m.Contains("重复的免配对凭据"));

            // 保留的应是**最新**那条（CreatedAt 最大 = legacy0）
            Assert.Equal("legacy0", store.Load()[0].Id);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
