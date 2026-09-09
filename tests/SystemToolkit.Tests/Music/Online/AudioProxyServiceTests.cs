using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SystemToolkit.Infrastructure.Music.Online;

namespace SystemToolkit.Tests.Music.Online;

/// <summary>
/// OM-3：音频代理——Referer/ContentType 域名映射 + Range 透传 + 封面缓存头（真实起 Kestrel 集成测）。
/// </summary>
public class AudioProxyServiceTests : IDisposable
{
    // 测试假上游在 127.0.0.1：注入放行校验器（生产默认走白名单，审查 Y5）
    private readonly AudioProxyService _sut = new(hostValidator: _ => true);
    private HttpListener? _fakeUpstream;
    private string? _fakeUpstreamPrefix;
    private string? _lastRangeHeader;

    public void Dispose()
    {
        _sut.StopAsync().GetAwaiter().GetResult();
        _fakeUpstream?.Stop();
    }

    /// <summary>起一个假上游：回显收到的 Range 头，并按 Range 语义返回 206/200。</summary>
    private string StartFakeUpstream(byte[] body)
    {
        _fakeUpstream = new HttpListener();
        _fakeUpstreamPrefix = $"http://127.0.0.1:{FreePort()}/";
        _fakeUpstream.Prefixes.Add(_fakeUpstreamPrefix);
        _fakeUpstream.Start();

        _ = Task.Run(async () =>
        {
            while (_fakeUpstream is not null && _fakeUpstream.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _fakeUpstream.GetContextAsync();
                }
                catch
                {
                    return; // 监听已停
                }

                _lastRangeHeader = ctx.Request.Headers["Range"];
                long start = 0, end = body.Length - 1;
                bool isRange = false;
                if (_lastRangeHeader is not null && _lastRangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    isRange = true;
                    string span = _lastRangeHeader["bytes=".Length..];
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

                byte[] slice = body[(int)start..(int)(end + 1)];
                ctx.Response.StatusCode = isRange ? 206 : 200;
                ctx.Response.ContentType = "audio/mpeg";
                ctx.Response.ContentLength64 = slice.Length;
                if (isRange)
                {
                    ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{body.Length}";
                }

                ctx.Response.OutputStream.Write(slice);
                ctx.Response.OutputStream.Close();
            }
        });

        return _fakeUpstreamPrefix;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

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
        Assert.Equal(@"D:\music\a.mp3", await _sut.GetProxiedAudioUrlAsync(@"D:\music\a.mp3"));
        Assert.Equal("data:audio/mpeg;base64,AAA", await _sut.GetProxiedAudioUrlAsync("data:audio/mpeg;base64,AAA"));
        Assert.Equal("blob:xyz", await _sut.GetProxiedCoverUrlAsync("blob:xyz"));
    }

    // ── 集成：真实起 Kestrel，验证 Range 透传与流式取回 ──

    [Fact]
    public async Task AudioProxy_PassesRangeThrough_AndReturns206()
    {
        byte[] upstreamBody = Encoding.ASCII.GetBytes(new string('A', 1000) + new string('B', 1000) + new string('C', 1000));
        string upstreamPrefix = StartFakeUpstream(upstreamBody);

        int port = await _sut.StartAsync();
        Assert.True(port > 0);
        Assert.True(_sut.IsRunning);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Range = new RangeHeaderValue(1000, 1999);

        string proxied = $"{_fakeUpstreamPrefix}song.mp3";
        HttpResponseMessage resp = await client.GetAsync(
            $"http://127.0.0.1:{port}/audio?url={Uri.EscapeDataString(proxied)}");

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal("audio/mpeg", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", string.Join("", resp.Headers.GetValues("Accept-Ranges")));

        byte[] body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(1000, body.Length);
        Assert.Equal((byte)'B', body[0]);

        // 🔴 核心断言：Range 头被原样透传到上游（流式 seek 的根基）
        Assert.Equal("bytes=1000-1999", _lastRangeHeader);

        // 幂等启动：再次 Start 返回同一端口
        Assert.Equal(port, await _sut.StartAsync());
    }

    [Fact]
    public async Task CoverProxy_AddsCacheHeader_NoCors()
    {
        string upstreamPrefix = StartFakeUpstream([1, 2, 3, 4]);
        int port = await _sut.StartAsync();

        using var client = new HttpClient();
        HttpResponseMessage resp = await client.GetAsync(
            $"http://127.0.0.1:{port}/cover?url={Uri.EscapeDataString(upstreamPrefix + "cover.jpg")}");

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
        // 默认白名单校验器（不注入放行）
        var sut = new AudioProxyService();
        int port = await sut.StartAsync();
        using var client = new HttpClient();
        HttpResponseMessage resp = await client.GetAsync(
            $"http://127.0.0.1:{port}/audio?url={Uri.EscapeDataString(targetUrl)}");

        if (shouldReject)
        {
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 白名单外一律 400
        }
        else
        {
            Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode); // 放行后上游不可达 → 502 等，非 400
        }
    }
}
