using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Core.Music.Online;

namespace SystemToolkit.Infrastructure.Music.Online;

/// <summary>
/// <see cref="IAudioProxyService"/> 的 Kestrel 实现（对照 NexBox audio_proxy.rs）。
/// 路由：/audio?url=（流式透传 + Range）与 /cover?url=（封面补 CORS + 日缓存头）。
/// </summary>
/// <remarks>
/// 三个关键细节（均来自 NexBox 实证）：
/// ① 按域名注入 Referer（直链防盗链校验，缺了就 403）；
/// ② Range 头原样透传 + Content-Range 回写（流式 seek 的根基）；
/// ③ 双 HttpClient——流式专用 client 只设连接超时、**无整体超时**（防长曲目中途被掐断）。
/// </remarks>
public sealed class AudioProxyService : IAudioProxyService
{
    /// <summary>host 校验器（生产默认白名单；测试注入放行假上游）。null = 默认白名单。</summary>
    private readonly Func<string, bool> _hostValidator;

    /// <summary>hostValidator：注入自定义校验器（测试放行假上游用）；缺省走白名单。</summary>
    public AudioProxyService(Func<string, bool>? hostValidator = null)
    {
        _hostValidator = hostValidator ?? IsAllowedProxyHost;
    }

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly object _gate = new();
    private WebApplication? _app;
    private int _port;
    private int _started;

    /// <inheritdoc />
    public bool IsRunning => _app is not null;

    /// <inheritdoc />
    public int Port => _port;

    /// <summary>按直链域名返回防盗链 Referer（对照 NexBox referer_for）。</summary>
    public static string RefererFor(string url)
    {
        if (url.Contains("qq.com", StringComparison.Ordinal) || url.Contains("qpic.cn", StringComparison.Ordinal) || url.Contains("gtimg.cn", StringComparison.Ordinal))
        {
            return "https://y.qq.com/";
        }

        if (url.Contains("kugou.com", StringComparison.Ordinal))
        {
            return "https://www.kugou.com/";
        }

        if (url.Contains("migu.cn", StringComparison.Ordinal) || url.Contains("miguvideo.com", StringComparison.Ordinal))
        {
            return "https://music.migu.cn/";
        }

        return "https://music.163.com/";
    }

    /// <summary>按扩展名推断 Content-Type（对照 NexBox content_type_for）。</summary>
    public static string ContentTypeFor(string url)
    {
        string lower = url.ToLowerInvariant();
        if (lower.Contains(".flac", StringComparison.Ordinal))
        {
            return "audio/flac";
        }

        if (lower.Contains(".m4a", StringComparison.Ordinal))
        {
            return "audio/mp4";
        }

        if (lower.Contains(".ogg", StringComparison.Ordinal))
        {
            return "audio/ogg";
        }

        if (lower.Contains(".wav", StringComparison.Ordinal))
        {
            return "audio/wav";
        }

        if (lower.Contains(".mp4", StringComparison.Ordinal))
        {
            return "video/mp4";
        }

        return "audio/mpeg"; // .mp3 与未知名兜底
    }

    /// <summary>非 http(s) 的 URL 不走代理（本地路径/file/data/blob 原样返回）。</summary>
    private static bool NeedsProxy(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 代理目标 host 白名单（审查 Y5，2026-09-10）：仅放行已支持平台的直链/CDN 域，
    /// 防止本机网页把代理当读内网/localhost 的中转（开放代理）。
    /// 域清单与 <see cref="RefererFor"/> 的平台域一致。
    /// </summary>
    private static bool IsAllowedProxyHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        return host.EndsWith("music.163.com", StringComparison.Ordinal)
            || host.EndsWith(".music.126.net", StringComparison.Ordinal) || host.EndsWith("music.126.net", StringComparison.Ordinal)
            || host.EndsWith(".126.net", StringComparison.Ordinal)
            || host.EndsWith("qq.com", StringComparison.Ordinal)
            || host.EndsWith("qqmusic.qq.com", StringComparison.Ordinal)
            || host.EndsWith("gtimg.cn", StringComparison.Ordinal) || host.EndsWith("gtimg.com", StringComparison.Ordinal)
            || host.EndsWith("qpic.cn", StringComparison.Ordinal)
            || host.EndsWith("kugou.com", StringComparison.Ordinal)
            || host.EndsWith("migu.cn", StringComparison.Ordinal) || host.EndsWith("miguvideo.com", StringComparison.Ordinal);
    }

    private TaskCompletionSource<int>? _startGate;

    /// <inheritdoc />
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_started < 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 1)
        {
            // 审查 Y4（2026-09-10）：并发首启时胜者尚未赋 _port，直接返回会拿到 0——
            // 经 TCS 门等待胜者的真实端口
            TaskCompletionSource<int>? gate = _startGate;
            if (gate is not null)
            {
                return await gate.Task.ConfigureAwait(false);
            }

            return _port; // 已完全启动
        }

        var startGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _startGate = startGate;

        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                // 只绑本机回环：代理仅供本进程播放器使用，不对外暴露
                options.Listen(IPAddress.Loopback, 0);
            });

            WebApplication app = builder.Build();
            app.MapGet("/audio", async (HttpRequest request, HttpResponse response) =>
                await ProxyAudioAsync(request, response));
            app.MapGet("/cover", async (HttpRequest request, HttpResponse response) =>
                await ProxyCoverAsync(request, response));

            // 手动 Start（不用 Run）：监听就绪即返回端口，服务随宿主后台运行
            await app.StartAsync(ct);
            _port = app.Urls
                .Select(u => new Uri(u).Port)
                .FirstOrDefault();
            _app = app;
            _startGate = null;
            startGate.TrySetResult(_port);
            return _port;
        }
        catch (Exception startEx)
        {
            Interlocked.Exchange(ref _started, 0); // 失败回滚，允许重试
            _startGate = null;
            startGate.TrySetException(startEx); // 等待方与胜者同败（审查 Y4）
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        WebApplication? app;
        lock (_gate)
        {
            app = _app;
            _app = null;
            _port = 0;
            Interlocked.Exchange(ref _started, 0);
        }

        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<string> GetProxiedAudioUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        if (!NeedsProxy(rawUrl))
        {
            return rawUrl;
        }

        int port = IsRunning ? _port : await StartAsync(ct);
        return $"http://127.0.0.1:{port}/audio?url={Uri.EscapeDataString(rawUrl)}";
    }

    /// <inheritdoc />
    public async Task<string> GetProxiedCoverUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        if (!NeedsProxy(rawUrl))
        {
            return rawUrl;
        }

        int port = IsRunning ? _port : await StartAsync(ct);
        return $"http://127.0.0.1:{port}/cover?url={Uri.EscapeDataString(rawUrl)}";
    }

    // ── 请求处理 ──

    private static readonly HttpClient WebClient = CreateClient(TimeSpan.FromSeconds(30));
    private static readonly HttpClient StreamClient = CreateClient(timeout: null, connectTimeoutSeconds: 15);

    private static HttpClient CreateClient(TimeSpan? timeout, int connectTimeoutSeconds = 30)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false, // 透传代理请求，不带本地 Cookie 容器（平台直链按需在握手 API 完成）
        };
        var client = new HttpClient(handler);
        if (timeout is not null)
        {
            client.Timeout = timeout.Value;
        }
        else
        {
            // 无整体超时：只有连接超时——长曲目边下边播不被掐断（NexBox 实证细节）
            client.DefaultRequestHeaders.ConnectionClose = false;
        }

        return client;
    }

    private async Task ProxyAudioAsync(HttpRequest request, HttpResponse response)
    {
        string? audioUrl = request.Query["url"];
        if (string.IsNullOrWhiteSpace(audioUrl) || !NeedsProxy(audioUrl) || !_hostValidator(audioUrl))
        {
            // 审查 Y5：非白名单域一律拒绝（防开放代理中转内网/localhost）
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var upstream = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        upstream.Headers.UserAgent.ParseAdd(UserAgent);
        upstream.Headers.Referrer = new Uri(RefererFor(audioUrl));

        // Range 透传（axum→reqwest 的 C# 对应：HttpRequest→HttpRequestMessage）
        if (request.Headers.ContainsKey("Range"))
        {
            upstream.Headers.TryAddWithoutValidation("Range", request.Headers["Range"].ToString());
        }

        try
        {
            using HttpResponseMessage upstreamResp = await StreamClient.SendAsync(
                upstream, HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode = (int)upstreamResp.StatusCode;
            response.ContentType = ContentTypeFor(audioUrl);
            response.Headers.Append("Accept-Ranges", "bytes");
            response.Headers.Append("Access-Control-Allow-Origin", "*");

            if (upstreamResp.Content.Headers.ContentLength is not null)
            {
                response.ContentLength = upstreamResp.Content.Headers.ContentLength;
            }

            if (upstreamResp.Content.Headers.ContentRange is not null)
            {
                response.Headers.Append("Content-Range", upstreamResp.Content.Headers.ContentRange.ToString());
            }

            // 纯流式透传：边下边播，零额外内存开销
            await upstreamResp.Content.CopyToAsync(response.Body);
        }
        catch (Exception ex)
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
            response.ContentType = "text/plain; charset=utf-8";
            await response.WriteAsync($"Proxy error: {ex.Message}");
        }
    }

    private async Task ProxyCoverAsync(HttpRequest request, HttpResponse response)
    {
        string? coverUrl = request.Query["url"];
        if (string.IsNullOrWhiteSpace(coverUrl) || !NeedsProxy(coverUrl) || !_hostValidator(coverUrl))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var upstream = new HttpRequestMessage(HttpMethod.Get, coverUrl);
        upstream.Headers.UserAgent.ParseAdd(UserAgent);
        upstream.Headers.Referrer = new Uri(RefererFor(coverUrl));

        try
        {
            using HttpResponseMessage upstreamResp = await WebClient.SendAsync(upstream);

            response.StatusCode = (int)upstreamResp.StatusCode;
            response.ContentType = upstreamResp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            response.Headers.Append("Access-Control-Allow-Origin", "*");
            response.Headers.Append("Cache-Control", "public, max-age=86400");

            if (upstreamResp.Content.Headers.ContentLength is not null)
            {
                response.ContentLength = upstreamResp.Content.Headers.ContentLength;
            }

            byte[] body = await upstreamResp.Content.ReadAsByteArrayAsync();
            await response.Body.WriteAsync(body);
        }
        catch (Exception ex)
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
            await response.WriteAsync($"Proxy error: {ex.Message}");
        }
    }
}
