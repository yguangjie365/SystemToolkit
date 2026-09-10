namespace SystemToolkit.Core.Music.Online;

/// <summary>
/// 网易云平台 API 子集（OM-5 目录服务用）。
/// </summary>
/// <remarks>
/// 由 <c>Infrastructure.Music.Online.NetEaseOnlineClient</c> 实现（方法签名与实现完全一致，
/// 接口只为让 Core 编排层脱离 Infrastructure 具体类型——依赖守卫红线：模块/编排禁引 Infrastructure）。
/// Cookie 由实现内部经 <see cref="IOnlineCredentialStore"/> 注入的调用方传入。
/// </remarks>
public interface INetEaseOnlineApi
{
    /// <summary>搜索歌曲。</summary>
    Task<List<OnlineTrack>> SearchAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default);

    /// <summary>当前用户歌单（需登录 Cookie）。</summary>
    Task<List<OnlinePlaylist>> LoadUserPlaylistsAsync(string cookie = "", CancellationToken ct = default);

    /// <summary>歌单曲目（分页）。</summary>
    Task<List<OnlineTrack>> LoadPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 100, string cookie = "", CancellationToken ct = default);

    /// <summary>每日推荐歌曲（需登录）。</summary>
    Task<List<OnlineTrack>> LoadDailyRecommendSongsAsync(string cookie = "", CancellationToken ct = default);

    /// <summary>推荐歌单。</summary>
    Task<List<OnlinePlaylist>> LoadRecommendationsAsync(string cookie = "", CancellationToken ct = default);

    /// <summary>登录态。</summary>
    Task<OnlineLoginInfo> GetLoginStatusAsync(string cookie = "", CancellationToken ct = default);

    /// <summary>歌词（LRC 文本 + 翻译）。</summary>
    Task<OnlineLyrics> GetLyricsAsync(string id, string cookie = "", CancellationToken ct = default);
}

/// <summary>
/// QQ 音乐平台 API 子集（OM-5 目录服务用）。由 <c>QQMusicOnlineClient</c> 实现。
/// </summary>
/// <remarks>
/// 用户歌单已支持（2026-09-09：对照 NexBox qqmusic.rs 三层回退——创建 fcg_user_created_diss /
/// 收藏 fcg_get_profile_order_asset / 回退 musicu PlaylistBaseRead）；每日推荐/推荐歌单仍无
/// 对应接口——目录层按能力声明，不硬造。
/// </remarks>
public interface IQqMusicOnlineApi
{
    /// <summary>搜索歌曲。</summary>
    Task<List<OnlineTrack>> SearchAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default);

    /// <summary>当前用户歌单（创建 + 收藏，需登录 Cookie）。</summary>
    Task<List<OnlinePlaylist>> LoadUserPlaylistsAsync(string cookie = "", CancellationToken ct = default);

    /// <summary>登录态。</summary>
    Task<OnlineLoginInfo> GetLoginStatusAsync(string cookie = "", CancellationToken ct = default);

    /// <summary>歌词（LRC 文本）。</summary>
    Task<OnlineLyrics> GetLyricsAsync(string songMid, string cookie = "", CancellationToken ct = default);

    /// <summary>歌单曲目（经歌单 ID 取曲目，QQ 走 GetPlaylistInfoWithTrackIds + 批量取歌）。</summary>
    Task<List<OnlineTrack>> LoadPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 100, string cookie = "", CancellationToken ct = default);

    /// <summary>官方榜单列表（对照 NexBox get_rank_list：四策略降级，末级走预设 topid）。</summary>
    Task<List<OnlineRankBoard>> LoadRankListAsync(string cookie = "", CancellationToken ct = default);

    /// <summary>榜单歌曲（对照 NexBox get_rank_songs：CGI page=detail&amp;topid=X）。</summary>
    Task<List<OnlineTrack>> LoadRankSongsAsync(string rankId, int limit = 30, string cookie = "", CancellationToken ct = default);
}

/// <summary>
/// 在线目录服务契约（OM-5）：搜索 / 歌单 / 推荐 / 登录态 / 在线歌词，按平台分发。
/// </summary>
/// <remarks>
/// <para>实现位于 Infrastructure（组合双平台客户端），模块经 DI 可选解析（缺席时在线浏览降级，
/// 本地功能不受影响）。所有失败路径以<b>空结果 + <see cref="CatalogError"/> 文本</b>表达——🔴 不静默，
/// 由 VM 把错误透传到 UI。</para>
/// <para>QQ 不支持的能力（每日推荐/推荐歌单）返回空结果并置
/// <see cref="CatalogError"/> 说明，VM 据此显示空态文案。</para>
/// </remarks>
public interface IOnlineMusicCatalogService
{
    /// <summary>最近一次调用的错误说明（成功/无错时为空串）。🔴 供 VM 透传显示。</summary>
    string CatalogError { get; }

    /// <summary>搜索歌曲。</summary>
    Task<List<OnlineTrack>> SearchAsync(OnlineProvider provider, string keywords, int limit = 30, CancellationToken ct = default);

    /// <summary>当前用户歌单列表（双平台；QQ = 创建 + 收藏歌单）。</summary>
    Task<List<OnlinePlaylist>> LoadUserPlaylistsAsync(OnlineProvider provider, CancellationToken ct = default);

    /// <summary>歌单曲目（分页）。</summary>
    Task<List<OnlineTrack>> LoadPlaylistTracksAsync(OnlineProvider provider, string playlistId, int offset = 0, int limit = 100, CancellationToken ct = default);

    /// <summary>每日推荐歌曲（QQ 不支持 → 空结果 + CatalogError；网易需登录）。</summary>
    Task<List<OnlineTrack>> LoadDailyRecommendSongsAsync(OnlineProvider provider, CancellationToken ct = default);

    /// <summary>推荐歌单（QQ 不支持 → 空结果 + CatalogError）。</summary>
    Task<List<OnlinePlaylist>> LoadRecommendationsAsync(OnlineProvider provider, CancellationToken ct = default);

    /// <summary>官方榜单列表（仅 QQ 有来源；网易 → 空结果，UI 整区隐藏）。</summary>
    Task<List<OnlineRankBoard>> LoadRankListAsync(OnlineProvider provider, CancellationToken ct = default);

    /// <summary>榜单歌曲（仅 QQ；网易 → 空结果 + CatalogError）。</summary>
    Task<List<OnlineTrack>> LoadRankSongsAsync(OnlineProvider provider, string rankId, int limit = 30, CancellationToken ct = default);

    /// <summary>登录态（实现永不抛——异常收敛为未登录 + CatalogError）。</summary>
    Task<OnlineLoginInfo> GetLoginStatusAsync(OnlineProvider provider, CancellationToken ct = default);

    /// <summary>在线歌词（网易云含翻译；失败返回空文档而不是抛）。</summary>
    Task<OnlineLyrics> GetLyricsAsync(OnlineProvider provider, string songKey, CancellationToken ct = default);
}
