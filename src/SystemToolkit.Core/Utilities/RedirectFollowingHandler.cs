using System.Net;

namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 手工逐跳跟随重定向的 <see cref="DelegatingHandler"/>（🟠 审查 2026-09-11，🟠-8）。
/// <para>
/// <b>为什么不用 <c>HttpClientHandler.AllowAutoRedirect = true</c></b>：自动跟随发生在
/// <see cref="HttpClientHandler"/> 内部，后续每一跳的 <c>Location</c> 都不经过调用方的任何校验——
/// 只要目标域上存在一个开放 302，请求就会被带去任意主机
/// （这与 <c>AudioProxyService</c> 的 🔴-3 同源，那边已改逐跳，本处理器补齐直连客户端三处）。
/// </para>
/// <para>
/// 本处理器在内层 handler 关闭自动跟随的基础上手工跟随，并**逐跳复验**：
/// ① scheme 必须为 HTTPS（拒绝降级）；② host 必须命中白名单后缀；③ 跳数上限熔断。
/// 违反任一条即抛 <see cref="HttpRequestException"/>，由调用方既有的异常路径处理。
/// </para>
/// <para>
/// 白名单按「域后缀」匹配（<c>h == d || h.EndsWith("." + d)</c>），故 <c>qq.com</c> 已覆盖
/// <c>y.qq.com</c> / <c>isure.stream.qqmusic.qq.com</c> 这类子域。
/// 若平台日后新增域名导致跟随被拒（异常消息会带出目标域），把该域加进构造参数即可。
/// </para>
/// <para>
/// ⚠️ 限制：本处理器**不复制请求体**——SystemToolkit 的三处使用点均为无 body 的 GET。
/// 若要用于带 body 的 POST/PUT，需先补 Content 复制（并明确 307/308 才保留 body 的语义）。
/// </para>
/// </summary>
public sealed class RedirectFollowingHandler : DelegatingHandler
{
    private readonly string[] _allowedHostSuffixes;
    private readonly int _maxRedirects;

    /// <param name="innerHandler">内层 handler（须已设 <c>AllowAutoRedirect = false</c>）。</param>
    /// <param name="allowedHostSuffixes">允许的域后缀，如 <c>["qq.com"]</c>。</param>
    /// <param name="maxRedirects">最大跟随跳数（超出即原样返回最后一跳）。</param>
    public RedirectFollowingHandler(
        HttpMessageHandler innerHandler,
        string[] allowedHostSuffixes,
        int maxRedirects = 5)
        : base(innerHandler)
    {
        _allowedHostSuffixes = allowedHostSuffixes;
        _maxRedirects = maxRedirects;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        for (int hop = 0; hop < _maxRedirects; hop++)
        {
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            Uri? location = response.Headers.Location;
            Uri? current = request.RequestUri;
            if (location is null || current is null)
            {
                return response; // 3xx 但没给 Location：交给调用方判定
            }

            Uri target = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!IsAllowedTarget(target))
            {
                response.Dispose();
                throw new HttpRequestException(
                    $"重定向目标被拒绝（非 HTTPS 或不在白名单）：{target}（来源 {current}）");
            }

            var follow = new HttpRequestMessage(request.Method, target);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                follow.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Dispose();
            response = await base.SendAsync(follow, cancellationToken).ConfigureAwait(false);
            request = follow;
        }

        return response; // 超过跳数上限：返回最后一跳，调用方按非 2xx 处理
    }

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or   // 301
        HttpStatusCode.Found or              // 302
        HttpStatusCode.SeeOther or           // 303
        HttpStatusCode.TemporaryRedirect or  // 307
        HttpStatusCode.PermanentRedirect;    // 308

    private bool IsAllowedTarget(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false; // 禁止 HTTPS → HTTP 降级
        }

        string host = uri.Host;
        foreach (string suffix in _allowedHostSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
