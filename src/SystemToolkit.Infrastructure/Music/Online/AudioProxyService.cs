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

        // 审查 R1（2026-09-10）：裸 EndsWith("qq.com") 会被 evilqq.com 绕过——必须点边界
        // （host 等于域，或以 ".域" 结尾才是真子域）
        static bool Dom(string h, string d) => h == d || h.EndsWith("." + d, StringComparison.Ordinal);
        string host = uri.Host.ToLowerInvariant();
        return Dom(host, "music.163.com")
            || Dom(host, "music.126.net")
            || Dom(host, "126.net")
            || Dom(host, "qq.com")
            || Dom(host, "gtimg.cn") || Dom(host, "gtimg.com")
            || Dom(host, "qpic.cn")
            || Dom(host, "kugou.com")
            || Dom(host, "migu.cn") || Dom(host, "miguvideo.com");
    }

    private TaskCompletionSource<int>? _startGate;

    /// <inheritdoc />
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_started < 0, this);
        // 审查 O7（2026-09-10）：以 _startGate 本身作 CAS 目标——门在任何 await 之前
        // 原子发布，输方必定 await 到胜者的真实端口，杜绝"CAS 后读未赋值 _port=0"窗口
        var startGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<int>? existing = Interlocked.CompareExchange(ref _startGate, startGate, null);
        if (existing is not null)
        {
            return await existing.Task.ConfigureAwait(false); // 已启动/启动中：等真实端口
        }

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
            // 审查 O7：保留 _startGate 为已完成门——后续 StartAsync 调用 CAS 失败后 await 到真实端口；
            // 不能置 null，否则停止前误置 null 会让新调用重新 CAS 成功并二次建站
            Interlocked.Exchange(ref _started, 1);
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
            _startGate = null; // 审查 O7：停止后归零门，允许再 StartAsync 重新 CAS 建站
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

        // 🟡 审查 2026-09-10（🟡-22）：此处与 StopAsync 之间存在窗口——刚取到的 port 可能
        // 在返回的 URL 被消费前端口已停。评估后**不修**：窗口极窄，且失败后果是 NAudio
        // 拿到连接拒绝 → 走 PlaybackFailed 显式可见（🔴-2 已修），不是静默错误。
        // 加锁会把「启动/停止」与「URL 消费」耦合起来，反而放大争用面。
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

        // 🟡 审查 2026-09-10（🟡-22）：此处与 StopAsync 之间存在窗口——刚取到的 port 可能
        // 在返回的 URL 被消费前端口已停。评估后**不修**：窗口极窄，且失败后果是 NAudio
        // 拿到连接拒绝 → 走 PlaybackFailed 显式可见（🔴-2 已修），不是静默错误。
        // 加锁会把「启动/停止」与「URL 消费」耦合起来，反而放大争用面。
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
            // 🔴 审查 2026-09-10（🔴-3）：**不得**开启自动重定向——AllowAutoRedirect=true 只保证
            // 首跳过了白名单，后续每一跳的 Location 都由 HttpClient 内部直接跟随、不再经
            // _hostValidator。改由 SendGuardedAsync 手工跟随并逐跳复核。
            AllowAutoRedirect = false,
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

    /// <summary>重定向跟随上限（超过即熔断，防重定向环）。</summary>
    private const int MaxRedirectHops = 5;

    /// <summary>
    /// 手工跟随重定向，**每一跳都过 host 白名单**（🔴 审查 2026-09-10）。
    /// <para>
    /// 背景：原先 <c>AllowAutoRedirect = true</c> 让 HttpClient 自动跟随，而白名单只在入站
    /// 首 URL 上校验过一次。若白名单域上存在开放 302（平台历史上确有跳转型取址接口），
    /// 本机任意网页即可把本代理当作读内网 / localhost 的中转——Y5 要堵的洞只堵了第一跳。
    /// </para>
    /// </summary>
    /// <returns>最终（非 3xx）响应；任一跳不在白名单、或超过 <see cref="MaxRedirectHops"/> 时返回 null。</returns>
    private async Task<HttpResponseMessage?> SendGuardedAsync(
        HttpClient client,
        string url,
        string? rangeHeader,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        string current = url;
        // Referer 按**原始**直链域计算并全程保持（平台 CDN 校验防盗链；跳域不改变来源语义）
        var referer = new Uri(RefererFor(url));

        for (int hop = 0; ; hop++)
        {
            using var upstream = new HttpRequestMessage(HttpMethod.Get, current);
            upstream.Headers.UserAgent.ParseAdd(UserAgent);
            upstream.Headers.Referrer = referer;
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                upstream.Headers.TryAddWithoutValidation("Range", rangeHeader);
            }

            HttpResponseMessage resp = await client.SendAsync(upstream, completion, ct).ConfigureAwait(false);

            if ((int)resp.StatusCode is not (>= 300 and < 400) || resp.Headers.Location is null)
            {
                return resp;
            }

            string next = resp.Headers.Location.IsAbsoluteUri
                ? resp.Headers.Location.ToString()
                : new Uri(new Uri(current), resp.Headers.Location).ToString();
            resp.Dispose();

            if (hop >= MaxRedirectHops || !NeedsProxy(next) || !_hostValidator(next))
            {
                return null; // 跳数熔断 / 目标域不在白名单：拒绝跟随
            }

            current = next;
        }
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

        // Range 透传（axum→reqwest 的 C# 对应：HttpRequest→HttpRequestMessage）
        string? rangeHeader = request.Headers.ContainsKey("Range") ? request.Headers["Range"].ToString() : null;

        try
        {
            CancellationToken aborted = request.HttpContext.RequestAborted; // 审查 Y14：可取消
            using HttpResponseMessage? upstreamResp = await SendGuardedAsync(
                StreamClient, audioUrl, rangeHeader, HttpCompletionOption.ResponseHeadersRead, aborted).ConfigureAwait(false);
            if (upstreamResp is null)
            {
                // 🔴-3：重定向链上有不可信目标 → 显式失败（NAudio 侧拿到 502 → PlaybackFailed 可见）
                response.StatusCode = StatusCodes.Status502BadGateway;
                response.ContentType = "text/plain; charset=utf-8";
                await response.WriteAsync("Proxy error: 重定向目标不在白名单或跳数超限，已拒绝跟随");
                return;
            }

            response.StatusCode = (int)upstreamResp.StatusCode;
            response.ContentType = ContentTypeFor(audioUrl);
            response.Headers.Append("Accept-Ranges", "bytes");
            // 审查 R1b（2026-09-10）：刻意不发 Access-Control-Allow-Origin——消费方是 NAudio
            // 原生 HTTP（无 CORS 语义）；此前设 * 会让本机任意网页跨源读代理响应

            if (upstreamResp.Content.Headers.ContentLength is not null)
            {
                response.ContentLength = upstreamResp.Content.Headers.ContentLength;
            }

            if (upstreamResp.Content.Headers.ContentRange is not null)
            {
                response.Headers.Append("Content-Range", upstreamResp.Content.Headers.ContentRange.ToString());
            }

            // 纯流式透传：边下边播，零额外内存开销
            await upstreamResp.Content.CopyToAsync(response.Body, aborted);
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

        try
        {
            CancellationToken aborted = request.HttpContext.RequestAborted; // 审查 Y14
            using HttpResponseMessage? upstreamResp = await SendGuardedAsync(
                WebClient, coverUrl, null, HttpCompletionOption.ResponseContentRead, aborted).ConfigureAwait(false);
            if (upstreamResp is null)
            {
                // 🔴-3：同 /audio，重定向链不可信即显式失败
                response.StatusCode = StatusCodes.Status502BadGateway;
                await response.WriteAsync("Proxy error: 重定向目标不在白名单或跳数超限，已拒绝跟随");
                return;
            }

            response.StatusCode = (int)upstreamResp.StatusCode;
            response.ContentType = upstreamResp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            // 审查 R1b（2026-09-10）：不发 Access-Control-Allow-Origin（同 /audio 理由）
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
