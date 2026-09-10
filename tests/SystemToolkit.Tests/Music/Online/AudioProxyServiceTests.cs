using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SystemToolkit.Infrastructure.Music.Online;

namespace SystemToolkit.Tests.Music.Online;

/// <summary>
/// 音频代理集成测试的共享假上游（2026-09-10 flaky 根治）。
/// <para>
/// 🔴 背景：原实现每个用例各自 new HttpListener + new AudioProxyService（Kestrel），
/// 端口经 bind(0)→取端口→释放 竞态分配，且部分实例从不停止 →
/// 后续用例会拿到残留/重复端口，请求上游时打到错误服务（实测 418 串扰），
/// 表现为「单跑绿、全量随机红」的 flaky。
/// 根治：全类共用一个 HttpListener 与一个代理实例（IClassFixture + IAsyncLifetime），
/// 彻底消除反复端口分配与监听泄漏。
/// </para>
/// </summary>
public sealed class AudioUpstreamFixture : IAsyncLifetime
{
    private HttpListener? _listener;

    /// <summary>上游响应体：A×1000 / B×1000 / C×1000 ——Range(1000-1999) 应命中全 B 段。</summary>
    private readonly byte[] _body = Encoding.ASCII.GetBytes(
        new string('A', 1000) + new string('B', 1000) + new string('C', 1000));

    /// <summary>假上游前缀（http://127.0.0.1:{port}/）。</summary>
    public string Prefix { get; private set; } = string.Empty;

    /// <summary>上游最近收到的 Range 头（用例开头置 null，避免跨用例污染）。</summary>
    public string? LastRangeHeader { get; set; }

    /// <summary>放行校验器（假上游在 127.0.0.1，生产默认走白名单——审查 Y5）。</summary>
    public AudioProxyService Proxy { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        int upstreamPort = TakeFreePort();

        // 🔴 顺序要害：先注册 http.sys 前缀，再起 Kestrel 代理。
        // 反序会让 Kestrel（端口 0 自动分配）抢到刚释放的端口，代理请求上游时打到自己（418 串扰）。
        // 注意：不能在注册期间用 TcpListener 占住端口——http.sys 会因端口被占抛
        // HttpListenerException（"另一个程序正在使用此文件"）。
        _listener = new HttpListener();
        Prefix = $"http://127.0.0.1:{upstreamPort}/";
        _listener.Prefixes.Add(Prefix);
        _listener.Start();

        _ = Task.Run(ServeLoop);

        Proxy = new AudioProxyService(hostValidator: _ => true);

        // 碰撞兜底：极小概率下 Kestrel 仍会拿到上游端口，重起到不同端口为止
        for (int attempt = 0; ; attempt++)
        {
            int proxyPort = await Proxy.StartAsync();
            if (proxyPort != upstreamPort || attempt >= 4)
            {
                break;
            }

            await Proxy.StopAsync();
        }
    }

    /// <summary>bind(0) 取一个空闲端口后释放（TOCTOU 窗口由上面的顺序与碰撞重试兜底）。</summary>
    private static int TakeFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        await Proxy.StopAsync();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
    }

    /// <summary>回显收到的 Range 头，并按 Range 语义返回 206/200。</summary>
    private async Task ServeLoop()
    {
        while (_listener is not null && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return; // 监听已停
            }

            LastRangeHeader = ctx.Request.Headers["Range"];
            long start = 0, end = _body.Length - 1;
            bool isRange = false;
            if (LastRangeHeader is not null && LastRangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
            {
                isRange = true;
                string span = LastRangeHeader["bytes=".Length..];
                string[] parts = span.Split('-');
                if (parts.Length == 2 && long.TryParse(parts[0], out long s))
                {
                    start = s;
                    if (parts[1].Length > 0 && long.TryParse(parts[1], out long e))
                    {
                        end = e;
                    }
                }
            }

            byte[] slice = _body[(int)start..(int)(end + 1)];
            ctx.Response.StatusCode = isRange ? 206 : 200;
            ctx.Response.ContentType = "audio/mpeg";
            ctx.Response.ContentLength64 = slice.Length;
            if (isRange)
            {
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{_body.Length}";
            }

            ctx.Response.OutputStream.Write(slice);
            ctx.Response.OutputStream.Close();
        }
    }
}

/// <summary>
/// OM-3：音频代理——Referer/ContentType 域名映射 + Range 透传 + 封面缓存头（真实起 Kestrel 集成测）。
/// </summary>
public class AudioProxyServiceTests : IClassFixture<AudioUpstreamFixture>
{
    private readonly AudioUpstreamFixture _upstream;

    public AudioProxyServiceTests(AudioUpstreamFixture upstream) => _upstream = upstream;

    // ── 域名映射 ──

    [Theory]
    [InlineData("http://dl.stream.qq.com/music/xxx.mp3", "https://y.qq.com/")]
    [InlineData("https://y.gtimg.cn/music/photo_default.png", "https://y.qq.com/")]
    [InlineData("http://imge.kugou.com/cover.jpg", "https://www.kugou.com/")]
    [InlineData("https://freetest.migu.cn/song.flac", "https://music.migu.cn/")]
    [InlineData("http://p1.music.126.net/cover.jpg", "https://music.163.com/")]
    public void RefererFor_MapsByDomain(string url, string expected)
    {
        Assert.Equal(expected, AudioProxyService.RefererFor(url));
    }

    [Theory]
    [InlineData("http://x/a.flac", "audio/flac")]
    [InlineData("http://x/a.M4A", "audio/mp4")]
    [InlineData("http://x/a.ogg", "audio/ogg")]
    [InlineData("http://x/a.wav", "audio/wav")]
    [InlineData("http://x/b.mp4", "video/mp4")]
    [InlineData("http://x/unknown.bin", "audio/mpeg")]
    public void ContentTypeFor_MapsByExtension(string url, string expected)
    {
        Assert.Equal(expected, AudioProxyService.ContentTypeFor(url));
    }

    // ── 非代理 URL 透传 ──

    [Fact]
    public async Task LocalAndSpecialUrls_BypassProxy()
    {
        Assert.Equal(@"D:\music\a.mp3", await _upstream.Proxy.GetProxiedAudioUrlAsync(@"D:\music\a.mp3"));
        Assert.Equal("data:audio/mpeg;base64,AAA", await _upstream.Proxy.GetProxiedAudioUrlAsync("data:audio/mpeg;base64,AAA"));
        Assert.Equal("blob:xyz", await _upstream.Proxy.GetProxiedCoverUrlAsync("blob:xyz"));
    }

    // ── 集成：真实起 Kestrel，验证 Range 透传与流式取回 ──

    [Fact]
    public async Task AudioProxy_PassesRangeThrough_AndReturns206()
    {
        _upstream.LastRangeHeader = null;
        int port = await _upstream.Proxy.StartAsync();
        Assert.True(port > 0);
        Assert.True(_upstream.Proxy.IsRunning);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Range = new RangeHeaderValue(1000, 1999);

        string proxied = $"{_upstream.Prefix}song.mp3";
        HttpResponseMessage resp = await client.GetAsync(
            $"http://127.0.0.1:{port}/audio?url={Uri.EscapeDataString(proxied)}");

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal("audio/mpeg", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", string.Join("", resp.Headers.GetValues("Accept-Ranges")));

        byte[] body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(1000, body.Length);
        Assert.Equal((byte)'B', body[0]);

        // 🔴 核心断言：Range 头被原样透传到上游（流式 seek 的根基）
        Assert.Equal("bytes=1000-1999", _upstream.LastRangeHeader);

        // 幂等启动：再次 Start 返回同一端口
        Assert.Equal(port, await _upstream.Proxy.StartAsync());
    }

    [Fact]
    public async Task CoverProxy_AddsCacheHeader_NoCors()
    {
        int port = await _upstream.Proxy.StartAsync();

        using var client = new HttpClient();
        HttpResponseMessage resp = await client.GetAsync(
            $"http://127.0.0.1:{port}/cover?url={Uri.EscapeDataString(_upstream.Prefix + "cover.jpg")}");

        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal("public, max-age=86400", string.Join("", resp.Headers.GetValues("Cache-Control")));
        // 审查 R1b：刻意不发 CORS（消费方 HttpClient/NAudio 无 CORS 语义；* 会放大 SSRF 读取面）
        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("http://evilqq.com/a.flac", true)]        // 裸 EndsWith("qq.com") 绕过 → 必须拒
    [InlineData("http://notkugou.com/a.flac", true)]      // 绕过 kugou.com → 必须拒
    [InlineData("http://xmigu.cn/a.flac", true)]          // 绕过 migu.cn → 必须拒
    [InlineData("http://evil.music.163.com/x.flac", false)] // 真子域 → 放行（非 400）
    [InlineData("http://y.qq.com/a.flac", false)]         // 精确域 → 放行
    public async Task R1_HostAllowlist_RejectsSuffixBypass(string targetUrl, bool shouldReject)
    {
        // 白名单校验在触达网络前完成：这里造独立实例（默认严格校验器，不注入放行）
        var sut = new AudioProxyService();
        int port = await sut.StartAsync();
        try
        {
            using var client = new HttpClient();
            HttpResponseMessage resp = await client.GetAsync(
                $"http://127.0.0.1:{port}/audio?url={Uri.EscapeDataString(targetUrl)}");

            if (shouldReject)
            {
                Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 白名单外一律 400
            }
            else
            {
                Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode); // 放行后上游状态取决于网络，非 400
            }
        }
        finally
        {
            // 测试隔离（2026-09-10）：不停止会泄漏 Kestrel 监听与端口（此前 flaky 的成因之一）
            await sut.StopAsync();
        }
    }
}
