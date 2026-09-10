using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Online;

namespace SystemToolkit.Infrastructure.Music.Online;

/// <summary>
/// <see cref="IOnlineMusicCatalogService"/> 默认实现（OM-5）：按平台分发到双客户端 + 注入 Cookie。
/// </summary>
/// <remarks>
/// <para>平台 API 以接口注入（<see cref="INetEaseOnlineApi"/>/<see cref="IQqMusicOnlineApi"/>），
/// 具体客户端在类声明上实现这两个接口——测试可用假 API 完整覆盖编排逻辑。</para>
/// <para>🔴 不静默：任何失败路径写入 <see cref="CatalogError"/> 并返回空结果，不抛给 VM。</para>
/// </remarks>
public sealed class OnlineMusicCatalogService : IOnlineMusicCatalogService
{
    private readonly INetEaseOnlineApi? _netEase;
    private readonly IQqMusicOnlineApi? _qq;
    private readonly IOnlineCredentialStore _credentials;
    private readonly ILogger _log;

    public OnlineMusicCatalogService(INetEaseOnlineApi? netEase, IQqMusicOnlineApi? qq, IOnlineCredentialStore credentials, ILogger log)
    {
        _netEase = netEase;
        _qq = qq;
        _credentials = credentials;
        _log = log;
    }

    /// <inheritdoc />
    public string CatalogError { get; private set; } = string.Empty;

    /// <summary>目录调用代际（🟡 审查 2026-09-10）。</summary>
    private int _callSeq;

    /// <summary>
    /// 开启一次目录调用：取代际号并清空错误槽。
    /// <para>
    /// 🟡-25：<see cref="CatalogError"/> 是**共享单槽**，而搜索 / 榜单 / 歌单等方法可被并发调用——
    /// 后发起者的清空会抹掉先发起者刚写入的真实失败（或反之后者的失败被归因给前者）。
    /// 只有最新一代能写错误槽；过期调用仅记日志、不写槽（其结果本身也会被 VM 层代际丢弃，
    /// 见 <c>MusicManagerViewModel._onlineSeq</c>）。
    /// </para>
    /// </summary>
    private int BeginCall()
    {
        int seq = Interlocked.Increment(ref _callSeq);
        CatalogError = string.Empty;
        return seq;
    }

    /// <summary>写入错误槽；仅当本调用仍是最新一代时生效（见 <see cref="BeginCall"/>）。</summary>
    private void SetError(int seq, string message)
    {
        if (seq != Volatile.Read(ref _callSeq))
        {
            _log.Warn($"[Music][Catalog] （过期调用，不写错误槽）{message}");
            return;
        }

        CatalogError = message;
        _log.Warn($"[Music][Catalog] {message}");
    }

    /// <inheritdoc />
    public async Task<List<OnlineTrack>> SearchAsync(OnlineProvider provider, string keywords, int limit = 30, CancellationToken ct = default)
    {
        int seq = BeginCall();
        if (string.IsNullOrWhiteSpace(keywords))
        {
            SetError(seq, "搜索关键词为空");
            return [];
        }

        try
        {
            return provider switch
            {
                OnlineProvider.NetEase when _netEase is not null
                    => await _netEase.SearchAsync(keywords, limit, _credentials.GetCookie(provider) ?? string.Empty, ct).ConfigureAwait(true),
                OnlineProvider.QQMusic when _qq is not null
                    => await _qq.SearchAsync(keywords, limit, _credentials.GetCookie(provider) ?? string.Empty, ct).ConfigureAwait(true),
                OnlineProvider.NetEase or OnlineProvider.QQMusic
                    => throw new InvalidOperationException("平台客户端未注册"),
                _ => throw new InvalidOperationException("未知音乐来源"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"搜索失败：{ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<OnlinePlaylist>> LoadUserPlaylistsAsync(OnlineProvider provider, CancellationToken ct = default)
    {
        int seq = BeginCall();
        try
        {
            // 2026-09-09 修复「QQ 登录后歌单为空」：QQ 客户端 9-3 已实现三层回退的用户歌单
            // （对照 NexBox qqmusic.rs），但目录层此前未接线（直接当不支持返回空）——接口声明过时所致
            string cookie = _credentials.GetCookie(provider) ?? string.Empty;
            return provider switch
            {
                OnlineProvider.NetEase when _netEase is not null
                    => await _netEase.LoadUserPlaylistsAsync(cookie, ct).ConfigureAwait(true),
                OnlineProvider.QQMusic when _qq is not null
                    => await _qq.LoadUserPlaylistsAsync(cookie, ct).ConfigureAwait(true),
                OnlineProvider.NetEase or OnlineProvider.QQMusic
                    => throw new InvalidOperationException("平台客户端未注册"),
                _ => throw new InvalidOperationException("未知音乐来源"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"加载歌单失败：{ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<OnlineTrack>> LoadPlaylistTracksAsync(OnlineProvider provider, string playlistId, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        int seq = BeginCall();
        try
        {
            string cookie = _credentials.GetCookie(provider) ?? string.Empty;
            return provider switch
            {
                OnlineProvider.NetEase when _netEase is not null
                    => await _netEase.LoadPlaylistTracksAsync(playlistId, offset, limit, cookie, ct).ConfigureAwait(true),
                OnlineProvider.QQMusic when _qq is not null
                    => await _qq.LoadPlaylistTracksAsync(playlistId, offset, limit, cookie, ct).ConfigureAwait(true),
                _ => throw new InvalidOperationException("平台客户端未注册"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"加载歌单曲目失败：{ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<OnlineTrack>> LoadDailyRecommendSongsAsync(OnlineProvider provider, CancellationToken ct = default)
    {
        int seq = BeginCall();
        if (provider != OnlineProvider.NetEase || _netEase is null)
        {
            SetError(seq, "每日推荐仅网易云支持");
            return [];
        }

        try
        {
            return await _netEase.LoadDailyRecommendSongsAsync(_credentials.GetCookie(provider) ?? string.Empty, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"加载每日推荐失败：{ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<OnlinePlaylist>> LoadRecommendationsAsync(OnlineProvider provider, CancellationToken ct = default)
    {
        int seq = BeginCall();
        if (provider != OnlineProvider.NetEase || _netEase is null)
        {
            SetError(seq, "推荐歌单仅网易云支持");
            return [];
        }

        try
        {
            return await _netEase.LoadRecommendationsAsync(_credentials.GetCookie(provider) ?? string.Empty, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"加载推荐歌单失败：{ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<OnlineRankBoard>> LoadRankListAsync(OnlineProvider provider, CancellationToken ct = default)
    {
        int seq = BeginCall();

        // 网易云无榜单来源（NexBox netease.rs 亦无对应实现）→ 空结果，UI 整区隐藏（主人 2026-09-10 裁定）
        if (provider != OnlineProvider.QQMusic)
        {
            return [];
        }

        if (_qq is null)
        {
            SetError(seq, "在线目录服务未就绪");
            return [];
        }

        try
        {
            return await _qq.LoadRankListAsync(_credentials.GetCookie(provider) ?? string.Empty, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"加载榜单失败：{ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<OnlineTrack>> LoadRankSongsAsync(OnlineProvider provider, string rankId, int limit = 30, CancellationToken ct = default)
    {
        int seq = BeginCall();

        if (provider != OnlineProvider.QQMusic)
        {
            SetError(seq, "该平台不支持榜单");
            return [];
        }

        if (_qq is null)
        {
            SetError(seq, "在线目录服务未就绪");
            return [];
        }

        try
        {
            return await _qq.LoadRankSongsAsync(rankId, limit, _credentials.GetCookie(provider) ?? string.Empty, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"加载榜单歌曲失败：{ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<OnlineLoginInfo> GetLoginStatusAsync(OnlineProvider provider, CancellationToken ct = default)
    {
        int seq = BeginCall();
        try
        {
            string cookie = _credentials.GetCookie(provider) ?? string.Empty;
            return provider switch
            {
                OnlineProvider.NetEase when _netEase is not null
                    => await _netEase.GetLoginStatusAsync(cookie, ct).ConfigureAwait(true),
                OnlineProvider.QQMusic when _qq is not null
                    => await _qq.GetLoginStatusAsync(cookie, ct).ConfigureAwait(true),
                _ => new OnlineLoginInfo { Provider = provider, LoggedIn = false },
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"登录态检测失败：{ex.Message}");
            return new OnlineLoginInfo { Provider = provider, LoggedIn = false };
        }
    }

    /// <inheritdoc />
    public async Task<OnlineLyrics> GetLyricsAsync(OnlineProvider provider, string songKey, CancellationToken ct = default)
    {
        int seq = BeginCall();
        try
        {
            string cookie = _credentials.GetCookie(provider) ?? string.Empty;
            return provider switch
            {
                OnlineProvider.NetEase when _netEase is not null
                    => await _netEase.GetLyricsAsync(songKey, cookie, ct).ConfigureAwait(true),
                OnlineProvider.QQMusic when _qq is not null
                    => await _qq.GetLyricsAsync(songKey, cookie, ct).ConfigureAwait(true),
                _ => new OnlineLyrics(),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError(seq, $"在线歌词加载失败：{ex.Message}");
            return new OnlineLyrics();
        }
    }
}
