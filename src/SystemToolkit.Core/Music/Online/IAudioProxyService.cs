namespace SystemToolkit.Core.Music.Online;

/// <summary>
/// 在线音乐本地音频代理：把平台直链经本地 HTTP 服务转发（注入防盗链 Referer + Range 透传）。
/// </summary>
/// <remarks>
/// 为什么需要代理：QQ/网易/酷狗/咪咕的音乐直链对请求头（Referer/User-Agent）有校验，
/// 播放器直连会被 403；流式 seek 需要分段请求（Range: bytes=...）逐段透传。
/// 架构对照 NexBox src-tauri/src/music_api/audio_proxy.rs 与旧工程 IAudioProxyService（G8.2）。
/// 实现位于 Infrastructure（Kestrel），经 Shell 组合根注册。
/// </remarks>
public interface IAudioProxyService
{
    /// <summary>代理服务是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>代理监听端口（启动后才有值，未启动为 0）。</summary>
    int Port { get; }

    /// <summary>启动代理服务（幂等：已运行则直接返回端口）。</summary>
    Task<int> StartAsync(CancellationToken ct = default);

    /// <summary>停止代理服务。</summary>
    Task StopAsync();

    /// <summary>
    /// 把在线音频原始 URL 转换为代理 URL（已 URL 编码）。
    /// <para>例：<c>http://dl.music.qq.com/xxx.mp3</c>
    /// → <c>http://127.0.0.1:{Port}/audio?url=http%3A%2F%2Fdl.music.qq.com%2Fxxx.mp3</c></para>
    /// <para>对非 http(s)（<c>file://</c> / <c>data:</c> / <c>blob:</c> / 本地路径）原样返回，不走代理。</para>
    /// <para>未启动时自动触发 <see cref="StartAsync"/>（惰性启动）。</para>
    /// </summary>
    Task<string> GetProxiedAudioUrlAsync(string rawUrl, CancellationToken ct = default);

    /// <summary>
    /// 把封面图原始 URL 转换为代理 URL（与音频同一服务，路由 /cover）。
    /// 对非 http(s) 原样返回。
    /// </summary>
    Task<string> GetProxiedCoverUrlAsync(string rawUrl, CancellationToken ct = default);
}
