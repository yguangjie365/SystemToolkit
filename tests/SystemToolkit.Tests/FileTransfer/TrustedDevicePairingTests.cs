using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Infrastructure.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// 「记住此设备 / 30 天免配对」用例（2026-09-13 批次 P3 ⑲）。
/// <para>
/// 这一组的要害是**跨重启**与**撤销彻底性**——两者都只有在"两个服务实例 + 同一份存储"的
/// 场景里才测得出来：单实例内测"记住"等于什么都没测（内存会话本来就在）。
/// </para>
/// </summary>
public class TrustedDevicePairingTests
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
        string dir = Path.Combine(Path.GetTempPath(), "stkft-trust-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        { Directory.Delete(dir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private static async Task<JsonElement> PairAsync(HttpClient http, int port, string code, bool remember)
    {
        var payload = new StringContent(
            JsonSerializer.Serialize(new { code, remember }), Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await http.PostAsync($"http://localhost:{port}/api/pair", payload);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// 勾选「记住此设备」→ 令牌可用；**重启服务后同一令牌仍然可用**（免配对的本义）；
    /// 撤销后立即失效，且磁盘上的凭据被一并删除。
    /// </summary>
    [Fact]
    public async Task Remember_SurvivesRestart_AndRevokeRemovesCredential()
    {
        string dir = NewTempDir();
        string storePath = Path.Combine(dir, "trusted-devices.json");
        int port1 = FreeTcpPort();
        int port2 = FreeTcpPort();
        try
        {
            var store = new DpapiTrustedWebDeviceStore(storePath);
            string pairedToken;
            string sessionId;

            await using (var server1 = new FileWebServer(trustedStore: store))
            {
                await server1.StartAsync(MakeSettings(port1), dir);
                using var http = new HttpClient();
                JsonElement body = await PairAsync(http, port1, server1.PairCode, remember: true);

                Assert.True(body.GetProperty("remembered").GetBoolean(), "勾选后服务端必须回报 remembered=true");
                Assert.True(body.GetProperty("trustedDays").GetInt32() >= 1);
                pairedToken = body.GetProperty("token").GetString()!;

                WebSessionInfo session = Assert.Single(server1.Sessions);
                Assert.True(session.Trusted);
                sessionId = session.Id;

                // 令牌必须真能用
                HttpResponseMessage ok = await http.GetAsync($"http://localhost:{port1}/api/files?t={pairedToken}");
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

                await server1.StopAsync();
            }

            // 🔴 落盘内容不得包含明文令牌（只存哈希 + DPAPI 加密）
            string raw = await File.ReadAllTextAsync(storePath);
            Assert.DoesNotContain(pairedToken, raw);
            Assert.True(store.Load().Count == 1, "应有一条长期凭据记录");

            await using var server2 = new FileWebServer(trustedStore: store);
            await server2.StartAsync(MakeSettings(port2), dir);
            using var http2 = new HttpClient();

            // 重启后：内存会话是空的（启动即清），但凭据命中 → 免配对重建会话
            Assert.Empty(server2.Sessions);
            HttpResponseMessage afterRestart = await http2.GetAsync($"http://localhost:{port2}/api/files?t={pairedToken}");
            Assert.Equal(HttpStatusCode.OK, afterRestart.StatusCode);
            WebSessionInfo rebuilt = Assert.Single(server2.Sessions);
            Assert.True(rebuilt.Trusted, "免配对重建的会话必须标记为「已记住」");
            Assert.True(rebuilt.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1), "免配对会话的有效期应是长期而非 8 小时");

            // 撤销（主人裁定：长期凭据与当前会话一起失效）
            Assert.True(server2.RevokeSession(rebuilt.Id));
            Assert.Empty(server2.Sessions);
            Assert.Empty(store.Load()); // 磁盘上的记录也删了——否则下次访问又免配对

            HttpResponseMessage afterRevoke = await http2.GetAsync($"http://localhost:{port2}/api/files?t={pairedToken}");
            Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
            _ = sessionId;
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>不勾选 → 普通会话：重启后同一令牌失效（与"记住"形成对照）。</summary>
    [Fact]
    public async Task WithoutRemember_IsSessionOnly_AndDiesWithRestart()
    {
        string dir = NewTempDir();
        string storePath = Path.Combine(dir, "trusted-devices.json");
        try
        {
            var store = new DpapiTrustedWebDeviceStore(storePath);
            string token;
            int port1 = FreeTcpPort();

            await using (var server1 = new FileWebServer(trustedStore: store))
            {
                await server1.StartAsync(MakeSettings(port1), dir);
                using var http = new HttpClient();
                JsonElement body = await PairAsync(http, port1, server1.PairCode, remember: false);
                Assert.False(body.GetProperty("remembered").GetBoolean());
                token = body.GetProperty("token").GetString()!;
                Assert.False(Assert.Single(server1.Sessions).Trusted);
                await server1.StopAsync();
            }

            Assert.Empty(store.Load()); // 没勾选就不该留下长期凭据

            int port2 = FreeTcpPort();
            await using var server2 = new FileWebServer(trustedStore: store);
            await server2.StartAsync(MakeSettings(port2), dir);
            using var http2 = new HttpClient();
            HttpResponseMessage resp = await http2.GetAsync($"http://localhost:{port2}/api/files?t={token}");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>存储层往返：写入→读出内容一致；文件损坏时按"没记住任何设备"处理而不是抛。</summary>
    [Fact]
    public void Store_RoundTrips_AndTreatsCorruptFileAsEmpty()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "trusted.json");
        try
        {
            var store = new DpapiTrustedWebDeviceStore(path);
            Assert.Empty(store.Load()); // 文件不存在 = 空表，不抛

            var record = new TrustedWebDevice
            {
                Id = "abc123",
                TokenHash = "deadbeef",
                Label = "Android · Chrome",
                Ip = "192.168.1.44",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            };
            store.Save([record]);

            IReadOnlyList<TrustedWebDevice> loaded = store.Load();
            TrustedWebDevice only = Assert.Single(loaded);
            Assert.Equal(record.Id, only.Id);
            Assert.Equal(record.TokenHash, only.TokenHash);
            Assert.Equal(record.Label, only.Label);
            Assert.True(only.IsValid(DateTimeOffset.UtcNow));
            Assert.Equal(30, only.RemainingDays(DateTimeOffset.UtcNow));

            // 损坏文件（非法 base64 / 解不开）→ 空表，绝不抛（凭据层不该让服务起不来）
            File.WriteAllText(path, "这不是合法的加密载荷");
            Assert.Empty(store.Load());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>过期记录的语义：不再有效、剩余天数为 0。</summary>
    [Fact]
    public void TrustedDevice_ExpirySemantics()
    {
        var expired = new TrustedWebDevice
        {
            Id = "x",
            TokenHash = "h",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        };
        Assert.False(expired.IsValid(DateTimeOffset.UtcNow));
        Assert.Equal(0, expired.RemainingDays(DateTimeOffset.UtcNow));
    }
}
