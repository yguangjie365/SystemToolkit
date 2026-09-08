using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Online;

namespace SystemToolkit.Infrastructure.Music.Online;

/// <summary>
/// <see cref="IOnlineUrlResolver"/> 默认实现（OM-4）：按平台分发 + 注入登录 Cookie + 音质降级链。
/// </summary>
/// <remarks>
/// <para>平台客户端以<b>委托</b>注入而非具体类型——解析编排逻辑可脱离真实网络单测
/// （测试用 lambda 模拟客户端行为）。</para>
/// <para>Cookie 来源：<see cref="IOnlineCredentialStore"/>（DPAPI 加密存储，OM-1）；
/// 未登录时传空串——免费曲库仍可播，VIP 曲目会得到明确的「需要登录」分类。</para>
/// </remarks>
public sealed class OnlineUrlResolver : IOnlineUrlResolver
{
    /// <summary>网易 URL 客户端委托：(id, preferredQuality, cookie, ct) → 结果。</summary>
    public delegate Task<OnlineSongUrlResult> NetEaseUrlFetcher(
        string id, string preferredQuality, string cookie, CancellationToken ct);

    /// <summary>QQ URL 客户端委托：(songMid, cookie, ct) → 结果。</summary>
    public delegate Task<OnlineSongUrlResult> QqUrlFetcher(string songMid, string cookie, CancellationToken ct);

    private readonly NetEaseUrlFetcher _netEase;
    private readonly QqUrlFetcher _qq;
    private readonly IOnlineCredentialStore _credentials;
    private readonly ILogger _log;

    public OnlineUrlResolver(
        NetEaseUrlFetcher netEaseFetcher,
        QqUrlFetcher qqFetcher,
        IOnlineCredentialStore credentials,
        ILogger log)
    {
        _netEase = netEaseFetcher;
        _qq = qqFetcher;
        _credentials = credentials;
        _log = log;
    }

    /// <inheritdoc />
    public async Task<OnlineSongUrlResult> ResolveAsync(OnlineTrack track, string preferredQuality, CancellationToken ct = default)
    {
        try
        {
            switch (track.Provider)
            {
                case OnlineProvider.NetEase:
                    return await _netEase(
                        track.Id, preferredQuality, _credentials.GetCookie(OnlineProvider.NetEase) ?? string.Empty, ct)
                        .ConfigureAwait(true);

                case OnlineProvider.QQMusic:
                    string mid = string.IsNullOrEmpty(track.Mid) ? track.Id : track.Mid;
                    return await _qq(mid, _credentials.GetCookie(OnlineProvider.QQMusic) ?? string.Empty, ct)
                        .ConfigureAwait(true);

                default:
                    return new OnlineSongUrlResult { Playable = false, Reason = "invalid_provider", Message = "未知音乐来源" };
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不是失败：由调用方的竞态序号处理
        }
        catch (Exception ex)
        {
            // 网络/解析层异常收敛为不可播结果（不抛给 VM——跳过策略统一处理）
            _log.Warn($"[Music][Online] URL 解析异常（{track.Provider} {track.Id}）：{ex.Message}");
            return new OnlineSongUrlResult { Playable = false, Reason = "error", Message = ex.Message };
        }
    }
}
