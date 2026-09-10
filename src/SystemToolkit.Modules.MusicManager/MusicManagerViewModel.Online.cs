using System.Collections.ObjectModel;
using System.ComponentModel;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Core.Music.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media.Imaging;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理 VM 在线分部（OM-5）：主页面三面板的在线目录能力。
/// </summary>
/// <remarks>
/// <para><b>视图状态机</b>：中部内容区三态切换（本地曲库 / 在线搜索结果 / 歌单详情）——
/// 对照 NexBox MusicPage 的 viewMode 设计，替代 Tab 割裂。</para>
/// <para><b>降级纪律</b>：<see cref="_catalog"/>（IOnlineMusicCatalogService）缺席时，
/// 搜索/歌单/推荐按钮给出显式提示（🔴 不静默），本地曲库功能完全不受影响。</para>
/// </remarks>
public partial class MusicManagerViewModel
{
    // ════════ 视图状态机 ════════

    /// <summary>中部内容区状态。</summary>
    public enum ContentViewMode
    {
        /// <summary>主页：本地音乐卡头 + 本地条目 + 我的歌单垂直列表（图1 对齐，2026-09-09）。</summary>
        LocalLibrary,

        /// <summary>本地曲目列表（主页点「本地导入的歌单」进入；搜索/过滤/双击播放）。</summary>
        LocalTracks,

        /// <summary>在线搜索结果。</summary>
        OnlineSearch,

        /// <summary>歌单详情（在线歌单的曲目列表）。</summary>
        PlaylistDetail,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalLibraryView))]
    [NotifyPropertyChangedFor(nameof(IsLocalTracksView))]
    [NotifyPropertyChangedFor(nameof(IsOnlineSearchView))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistDetailView))]
    [NotifyPropertyChangedFor(nameof(ViewTitle))]
    private ContentViewMode _currentView = ContentViewMode.LocalLibrary;

    /// <summary>主页（歌单列表，图1）。</summary>
    public bool IsLocalLibraryView => CurrentView == ContentViewMode.LocalLibrary;

    /// <summary>本地曲目列表视图。</summary>
    public bool IsLocalTracksView => CurrentView == ContentViewMode.LocalTracks;

    /// <summary>当前是否为在线搜索视图。</summary>
    public bool IsOnlineSearchView => CurrentView == ContentViewMode.OnlineSearch;

    /// <summary>当前是否为歌单详情视图。</summary>
    public bool IsPlaylistDetailView => CurrentView == ContentViewMode.PlaylistDetail;

    /// <summary>内容区标题（跟随视图状态）。</summary>
    public string ViewTitle => CurrentView switch
    {
        ContentViewMode.OnlineSearch => $"搜索结果 · {SelectedPlatformText}",
        ContentViewMode.PlaylistDetail => OpenPlaylist?.Name ?? "歌单详情",
        ContentViewMode.LocalTracks => "本地音乐",
        _ => "我的歌单",
    };

    /// <summary>主页 → 本地曲目列表（图1 的「本地导入的歌单」条目点击）。</summary>
    [RelayCommand]
    private void ShowLocalTracks() => CurrentView = ContentViewMode.LocalTracks;

    // ════════ 平台切换 ════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPlatformText))]
    [NotifyPropertyChangedFor(nameof(ViewTitle))]
    private OnlineProvider _selectedPlatform = OnlineProvider.NetEase;

    /// <summary>当前平台展示名（顶栏切换按钮与视图标题共用）。</summary>
    public string SelectedPlatformText => SelectedPlatform == OnlineProvider.NetEase ? "网易云" : "QQ 音乐";

    /// <summary>顶栏平台切换（参数 "NetEase"/"QQMusic"）。切平台后刷新歌单面板与登录态。</summary>
    [RelayCommand]
    private async Task SelectPlatformAsync(string? platform)
    {
        SelectedPlatform = platform == "QQMusic" ? OnlineProvider.QQMusic : OnlineProvider.NetEase;
        // 审查 F-02：网络命令异常必须落用户可见状态（🔴 不静默——否则表现为"点击没反应"）
        try
        {
            await LoadPlaylistsAsync(); // 歌单面板跟随平台（双平台用户歌单均已接线）
            await RefreshLoginStateAsync();
        }
        catch (Exception ex)
        {
            OnlineStatusText = $"切换平台失败：{ex.Message}";
            _log.Error($"[Music] 切换平台失败（{SelectedPlatform}）", ex);
        }
    }

    // ════════ 在线搜索 ════════

    [ObservableProperty]
    private string _onlineSearchText = string.Empty;

    /// <summary>在线搜索结果行（<see cref="ToQueueSong"/> 可直接转队列曲；带封面缩略图懒加载）。</summary>
    public sealed class OnlineResultRowVm : ObservableObject
    {
        public OnlineResultRowVm(OnlineTrack track) => Track = track;

        public OnlineTrack Track { get; }

        /// <summary>标题。</summary>
        public string Title => Track.Name;

        /// <summary>艺术家 — 专辑（专辑空时只显示艺术家）。</summary>
        public string Subtitle => string.IsNullOrEmpty(Track.Album) ? Track.Artist : $"{Track.Artist} — {Track.Album}";

        /// <summary>权益徽标（VIP/试听；免费曲为空串）。</summary>
        public string Badge => Track.Fee == 1 ? "VIP" : Track.Fee == 8 ? "试听" : string.Empty;

        /// <summary>时长展示（m:ss）。</summary>
        public string DurationText => Track.DurationMs == 0
            ? "—"
            : TimeSpan.FromMilliseconds(Track.DurationMs) is var ts
                ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}"
                : "—";

        private BitmapSource? _coverImage;

        /// <summary>封面缩略图（异步装载；null 时模板显示音符占位）。</summary>
        public BitmapSource? CoverImage
        {
            get => _coverImage;
            private set
            {
                if (SetProperty(ref _coverImage, value))
                {
                    OnPropertyChanged(nameof(HasCover));
                }
            }
        }

        /// <summary>是否已有封面（占位图标 ↔ 图片切换用）。</summary>
        public bool HasCover => _coverImage is not null && !_coverFailed;

        private bool _coverFailed;

        /// <summary>封面下载/解码失败标记（审查 P4-24）：回退音符占位。</summary>
        public void MarkCoverFailed()
        {
            if (_coverFailed)
            {
                return;
            }

            _coverFailed = true;
            OnPropertyChanged(nameof(HasCover));
        }

        private bool _coverAttempted;

        /// <summary>
        /// 懒装载封面缩略图（loader = VM 注入的代理下载 + 缓存链；失败降级占位不中断列表）。
        /// 每行只尝试一次，防滚动重入反复请求。
        /// </summary>
        public async Task LoadCoverAsync(Func<string, Task<BitmapSource?>> loader)
        {
            if (_coverAttempted)
            {
                return;
            }

            _coverAttempted = true;
            if (string.IsNullOrWhiteSpace(Track.Cover))
            {
                return;
            }

            BitmapSource? image = await loader(Track.Cover).ConfigureAwait(true);
            if (image is not null)
            {
                CoverImage = image;
            }
        }
    }

    public ObservableCollection<OnlineResultRowVm> SearchResults { get; } = [];

    // ── P3a：搜索历史（最近 10 条，新→旧；AtomicFile JSON 持久化）──
    private readonly ISearchHistoryStore? _searchHistoryStore; // 仅构造注入（Release 分析器 IDE0044 要求 readonly）
    private const int SearchHistoryCapacity = 10;

    public ObservableCollection<string> SearchHistory { get; } = [];

    public bool HasSearchHistory => SearchHistory.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineCommand))]
    private bool _isSearchingOnline;

    private bool CanSearchOnline() => !IsSearchingOnline;

    [RelayCommand(CanExecute = nameof(CanSearchOnline))]
    private async Task SearchOnlineAsync()
    {
        if (_catalog is null)
        {
            OnlineStatusText = "在线目录服务未就绪，无法搜索";
            return;
        }

        if (string.IsNullOrWhiteSpace(OnlineSearchText))
        {
            OnlineStatusText = "请输入搜索关键词";
            return;
        }

        IsSearchingOnline = true;
        try
        {
            List<OnlineTrack> tracks = await _catalog.SearchAsync(SelectedPlatform, OnlineSearchText.Trim());
            SearchResults.Clear();
            foreach (OnlineTrack track in tracks)
            {
                SearchResults.Add(new OnlineResultRowVm(track));
            }

            BeginCoverLoads(SearchResults);
            OnlineStatusText = _catalog.CatalogError; // 空结果/失败原因可见（🔴 不静默）
            CurrentView = ContentViewMode.OnlineSearch;
            RecordSearchHistory(OnlineSearchText.Trim()); // P3a：成功才记历史
        }
        finally
        {
            IsSearchingOnline = false;
        }
    }

    // ── P3a：搜索历史 ──

    /// <summary>搜索成功后记录关键词（去重、置顶、容量 10），并异步持久化。</summary>
    private void RecordSearchHistory(string query)
    {
        if (_searchHistoryStore is null || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        string q = query.Trim();
        string? existing = SearchHistory.FirstOrDefault(h => string.Equals(h, q, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SearchHistory.Remove(existing);
        }

        SearchHistory.Insert(0, q);
        while (SearchHistory.Count > SearchHistoryCapacity)
        {
            SearchHistory.RemoveAt(SearchHistory.Count - 1);
        }

        OnPropertyChanged(nameof(HasSearchHistory));
        PersistSearchHistory();
    }

    /// <summary>UI 线程拍快照后后台原子写；失败仅记日志（🔴 不静默，但不打断搜索流）。</summary>
    private void PersistSearchHistory()
    {
        if (_searchHistoryStore is null)
        {
            return;
        }

        string[] snapshot = SearchHistory.ToArray();
        _ = PersistSearchHistorySafeAsync(snapshot);
    }

    private async Task PersistSearchHistorySafeAsync(string[] snapshot)
    {
        if (_searchHistoryStore is null)
        {
            return;
        }

        try
        {
            await _searchHistoryStore.SaveAsync(snapshot);
        }
        catch (Exception ex)
        {
            _log.Warn($"[Music] 搜索历史保存失败：{ex.Message}");
        }
    }

    /// <summary>启动时加载搜索历史（新→旧填充下拉）。</summary>
    public async Task LoadSearchHistoryAsync()
    {
        if (_searchHistoryStore is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<string> items = await _searchHistoryStore.LoadAsync();
            SearchHistory.Clear();
            foreach (string q in items)
            {
                SearchHistory.Add(q);
            }

            OnPropertyChanged(nameof(HasSearchHistory));
        }
        catch (Exception ex)
        {
            _log.Warn($"[Music] 搜索历史加载失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveSearchHistory(string? query)
    {
        if (query is null)
        {
            return;
        }

        string? match = SearchHistory.FirstOrDefault(h => string.Equals(h, query, StringComparison.Ordinal));
        if (match is not null)
        {
            SearchHistory.Remove(match);
            OnPropertyChanged(nameof(HasSearchHistory));
            PersistSearchHistory();
        }
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        SearchHistory.Clear();
        OnPropertyChanged(nameof(HasSearchHistory));
        PersistSearchHistory();
    }

    /// <summary>点历史项 → 填入搜索框并立即搜索。</summary>
    [RelayCommand]
    private void ApplySearchHistory(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        OnlineSearchText = query;
        if (SearchOnlineCommand.CanExecute(null))
        {
            SearchOnlineCommand.Execute(null);
        }
    }

    private string _onlineStatusText = string.Empty;

    /// <summary>在线区状态行（搜索/歌单/推荐共用出口；空 = 无事发生）。</summary>
    public string OnlineStatusText
    {
        get => _onlineStatusText;
        private set => SetProperty(ref _onlineStatusText, value);
    }

    /// <summary>双击搜索结果 = 以整页结果为队列、该曲为起点起播（OM-4 管线入口）。</summary>
    [RelayCommand]
    private async Task PlayFromSearchAsync(OnlineResultRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        var tracks = SearchResults.Select(r => r.Track).ToList();
        await PlayOnlineTracksAsync(tracks, row.Track);
    }

    /// <summary>把单首在线曲追加到当前播放队列（不清队列）。</summary>
    [RelayCommand]
    private void AddOnlineToQueue(OnlineResultRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        _queue.AddToQueue(ToQueueSong(row.Track));
        OnlineStatusText = $"已加入队列：{row.Track.Name}";
    }

    // ════════ 歌单面板 ════════

    /// <summary>歌单行（我的歌单胶囊与推荐歌单列表共用；带封面缩略图懒加载）。</summary>
    public sealed class PlaylistRowVm : ObservableObject
    {
        public PlaylistRowVm(OnlinePlaylist playlist) => Playlist = playlist;

        public OnlinePlaylist Playlist { get; }

        /// <summary>歌单名。</summary>
        public string Title => Playlist.Name;

        /// <summary>曲目数展示。</summary>
        public string CountText => Playlist.TrackCount == 0 ? string.Empty : $"{Playlist.TrackCount} 首";

        private BitmapSource? _coverImage;

        /// <summary>封面缩略图（异步装载；null 时模板显示色块占位）。</summary>
        public BitmapSource? CoverImage
        {
            get => _coverImage;
            private set
            {
                if (SetProperty(ref _coverImage, value))
                {
                    OnPropertyChanged(nameof(HasCover));
                }
            }
        }

        /// <summary>是否已有封面。</summary>
        public bool HasCover => _coverImage is not null && !_coverFailed;

        private bool _coverFailed;

        /// <summary>封面下载/解码失败标记（审查 P4-24）：回退音符占位。</summary>
        public void MarkCoverFailed()
        {
            if (_coverFailed)
            {
                return;
            }

            _coverFailed = true;
            OnPropertyChanged(nameof(HasCover));
        }

        private bool _coverAttempted;

        /// <summary>懒装载封面（同 OnlineResultRowVm，每行只尝试一次）。</summary>
        public async Task LoadCoverAsync(Func<string, Task<BitmapSource?>> loader)
        {
            if (_coverAttempted)
            {
                return;
            }

            _coverAttempted = true;
            if (string.IsNullOrWhiteSpace(Playlist.Cover))
            {
                return;
            }

            BitmapSource? image = await loader(Playlist.Cover).ConfigureAwait(true);
            if (image is not null)
            {
                CoverImage = image;
            }
        }
    }

    public ObservableCollection<PlaylistRowVm> UserPlaylists { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPlaylistsCommand))]
    private bool _isLoadingPlaylists;

    private bool CanLoadPlaylists() => !IsLoadingPlaylists;

    [RelayCommand(CanExecute = nameof(CanLoadPlaylists))]
    private async Task LoadPlaylistsAsync()
    {
        if (_catalog is null)
        {
            OnlineStatusText = "在线目录服务未就绪，无法加载歌单";
            return;
        }

        IsLoadingPlaylists = true;
        try
        {
            List<OnlinePlaylist> playlists = await _catalog.LoadUserPlaylistsAsync(SelectedPlatform);
            UserPlaylists.Clear();
            foreach (OnlinePlaylist playlist in playlists)
            {
                UserPlaylists.Add(new PlaylistRowVm(playlist));
            }

            BeginPlaylistCoverLoads(UserPlaylists);

            // QQ 无此能力等场景：错误说明透传（🔴 不静默）
            OnlineStatusText = _catalog.CatalogError;
        }
        finally
        {
            IsLoadingPlaylists = false;
        }
    }

    /// <summary>行缩略图加载器（代理下载 + URL 键控缓存；audioProxy 缺席 → null，行显示占位）。</summary>
    public Func<string, Task<BitmapSource?>>? OnlineCoverLoader =>
        _audioProxy is null ? null : LoadThumbCoreAsync;

    private async Task<BitmapSource?> LoadThumbCoreAsync(string rawUrl)
    {
        if (CoverThumbCache.TryGet(rawUrl, out BitmapSource? cached))
        {
            return cached;
        }

        try
        {
            string proxy = await _audioProxy!.GetProxiedCoverUrlAsync(rawUrl).ConfigureAwait(true);
            byte[] bytes = await CoverHttp.GetByteArrayAsync(proxy).ConfigureAwait(true); // 审查 O13：复用共享 client
            BitmapSource? image = Decode(bytes, 96); // 审查 O14：缩略图按目标尺寸解码（Player 分部私有解码）
            CoverThumbCache.Store(rawUrl, image);
            return image;
        }
        catch (Exception ex)
        {
            // 缩略图失败只降级占位（列表主功能不受影响），但日志可见（🔴 不静默）
            _log.Warn($"[Music] 缩略图加载失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>审查 O13：在线封面并发闸（防一次开歌单 100 张全量并发下载 + 解码排 Dispatcher）。</summary>
    private static readonly System.Threading.SemaphoreSlim CoverGate = new(6, 6);

    private static async Task RunThrottled(Func<Task> work)
    {
        await CoverGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            CoverGate.Release();
        }
    }

    private void BeginCoverLoads(System.Collections.Generic.IEnumerable<OnlineResultRowVm> rows)
    {
        Func<string, Task<BitmapSource?>>? loader = OnlineCoverLoader;
        if (loader is null)
        {
            return;
        }

        foreach (OnlineResultRowVm row in rows)
        {
            _ = RunThrottled(() => row.LoadCoverAsync(loader)); // 审查 O13：限流并发
        }
    }

    private void BeginPlaylistCoverLoads(System.Collections.Generic.IEnumerable<PlaylistRowVm> rows)
    {
        Func<string, Task<BitmapSource?>>? loader = OnlineCoverLoader;
        if (loader is null)
        {
            return;
        }

        foreach (PlaylistRowVm row in rows)
        {
            _ = RunThrottled(() => row.LoadCoverAsync(loader)); // 审查 O13：限流并发
        }
    }

    private OnlinePlaylist? _openPlaylist;

    /// <summary>当前打开的歌单（歌单详情视图标题 + 全部播放用）。</summary>
    public OnlinePlaylist? OpenPlaylist
    {
        get => _openPlaylist;
        private set => SetProperty(ref _openPlaylist, value);
    }

    public ObservableCollection<OnlineResultRowVm> PlaylistTracks { get; } = [];

    /// <summary>歌单详情的过滤视图（图2 对齐：搜索本歌单；主构造函数里挂 Filter）。</summary>
    public ICollectionView PlaylistTracksView { get; private set; } = null!;

    private string _playlistFilterText = string.Empty;

    /// <summary>歌单内搜索关键字：只过滤当前打开歌单的显示，不动源集合。</summary>
    public string PlaylistFilterText
    {
        get => _playlistFilterText;
        set
        {
            if (SetProperty(ref _playlistFilterText, value))
            {
                PlaylistTracksView.Refresh();
                OnPropertyChanged(nameof(PlaylistFilterNoMatch));
            }
        }
    }

    /// <summary>有曲目但被搜索字过滤光 → 提示"无匹配"（与"歌单为空"区分）。</summary>
    public bool PlaylistFilterNoMatch =>
        PlaylistTracks.Count > 0 && (PlaylistTracksView?.IsEmpty ?? false);

    private bool MatchesPlaylistFilter(OnlineResultRowVm row)
    {
        if (string.IsNullOrWhiteSpace(_playlistFilterText))
        {
            return true;
        }

        return (row.Title?.Contains(_playlistFilterText, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.Subtitle?.Contains(_playlistFilterText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    private async Task OpenPlaylistAsync(PlaylistRowVm? row)
    {
        if (row is null || _catalog is null)
        {
            return;
        }

        OpenPlaylist = row.Playlist;
        OnPropertyChanged(nameof(ViewTitle));
        PlaylistFilterText = string.Empty; // 换歌单即清空上一次的站内搜索
        // 审查 F-02：取曲目失败要可见（登录过期/网络/接口变更），不能 Task Faulted 静默
        try
        {
            List<OnlineTrack> tracks = await _catalog.LoadPlaylistTracksAsync(
                row.Playlist.Provider, row.Playlist.Id);
            PlaylistTracks.Clear();
            foreach (OnlineTrack track in tracks)
            {
                PlaylistTracks.Add(new OnlineResultRowVm(track));
            }

            BeginCoverLoads(PlaylistTracks);
            OnPropertyChanged(nameof(PlaylistFilterNoMatch));
            OnlineStatusText = _catalog.CatalogError;
            CurrentView = ContentViewMode.PlaylistDetail;
        }
        catch (Exception ex)
        {
            OnlineStatusText = $"加载歌单失败：{ex.Message}";
            _log.Error($"[Music] 加载歌单失败（{row.Playlist.Name}）", ex);
        }
    }

    /// <summary>歌单详情：从第一首起播整个歌单（首屏已加载的曲目为队列）。</summary>
    [RelayCommand]
    private async Task PlayPlaylistFromStartAsync()
    {
        if (PlaylistTracks.Count == 0)
        {
            OnlineStatusText = "歌单暂无可播放曲目";
            return;
        }

        var tracks = PlaylistTracks.Select(r => r.Track).ToList();
        // 审查 F-02：播放失败落状态行
        try
        {
            await PlayOnlineTracksAsync(tracks, tracks[0]);
        }
        catch (Exception ex)
        {
            OnlineStatusText = $"播放歌单失败：{ex.Message}";
            _log.Error("[Music] 从头播放歌单失败", ex);
        }
    }

    /// <summary>歌单详情：双击单曲 = 以整个歌单为队列起播该曲。</summary>
    [RelayCommand]
    private async Task PlayFromPlaylistAsync(OnlineResultRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        var tracks = PlaylistTracks.Select(r => r.Track).ToList();
        // 审查 F-02：播放失败落状态行
        try
        {
            await PlayOnlineTracksAsync(tracks, row.Track);
        }
        catch (Exception ex)
        {
            OnlineStatusText = $"播放失败：{ex.Message}";
            _log.Error($"[Music] 从歌单播放「{row.Title}」失败", ex);
        }
    }

    /// <summary>从歌单详情返回（回到上一内容态；简化为回本地曲库）。</summary>
    [RelayCommand]
    private void ClosePlaylist() => CurrentView = ContentViewMode.LocalLibrary;

    // ════════ 推荐面板（右） ════════

    public ObservableCollection<OnlineResultRowVm> DailyRecommend { get; } = [];

    public ObservableCollection<PlaylistRowVm> RecommendedPlaylists { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadRecommendationsCommand))]
    private bool _isLoadingRecommendations;

    private bool CanLoadRecommendations() => !IsLoadingRecommendations;

    [RelayCommand(CanExecute = nameof(CanLoadRecommendations))]
    private async Task LoadRecommendationsAsync()
    {
        if (_catalog is null)
        {
            OnlineStatusText = "在线目录服务未就绪，无法加载推荐";
            return;
        }

        IsLoadingRecommendations = true;
        try
        {
            List<OnlineTrack> daily = await _catalog.LoadDailyRecommendSongsAsync(SelectedPlatform);
            DailyRecommend.Clear();
            foreach (OnlineTrack track in daily)
            {
                DailyRecommend.Add(new OnlineResultRowVm(track));
            }
            BeginCoverLoads(DailyRecommend); // 实机反馈（图3）：每日推荐曲目也要显示封面（此前漏挂）

            List<OnlinePlaylist> recommended = await _catalog.LoadRecommendationsAsync(SelectedPlatform);
            RecommendedPlaylists.Clear();
            foreach (OnlinePlaylist playlist in recommended)
            {
                RecommendedPlaylists.Add(new PlaylistRowVm(playlist));
            }

            // 空态原因可见：未登录 / QQ 不支持 / 网络失败（🔴 不静默）
            OnlineStatusText = _catalog.CatalogError;
        }
        catch (Exception ex)
        {
            // 审查 F-02：原只有 finally，异常被吞——用户只看到"加载中"消失
            OnlineStatusText = $"加载推荐失败：{ex.Message}";
            _log.Error("[Music] 加载每日推荐/推荐歌单失败", ex);
        }
        finally
        {
            IsLoadingRecommendations = false;
        }
    }

    /// <summary>双击每日推荐曲 = 以整版推荐为队列起播该曲。</summary>
    [RelayCommand]
    private async Task PlayFromDailyRecommendAsync(OnlineResultRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        var tracks = DailyRecommend.Select(r => r.Track).ToList();
        // 审查 F-02：播放失败落状态行
        try
        {
            await PlayOnlineTracksAsync(tracks, row.Track);
        }
        catch (Exception ex)
        {
            OnlineStatusText = $"播放失败：{ex.Message}";
            _log.Error($"[Music] 从每日推荐播放「{row.Title}」失败", ex);
        }
    }

    // ════════ 登录态 ════════

    private string _netEaseLoginText = "检测中…";

    /// <summary>网易云登录态展示（左栏状态芯片）。</summary>
    public string NetEaseLoginText
    {
        get => _netEaseLoginText;
        private set => SetProperty(ref _netEaseLoginText, value);
    }

    private string _qqLoginText = "检测中…";

    /// <summary>QQ 音乐登录态展示。</summary>
    public string QqLoginText
    {
        get => _qqLoginText;
        private set => SetProperty(ref _qqLoginText, value);
    }

    /// <summary>
    /// 登录窗回调（View 注入）：参数为平台；View 打开 <c>OnlineLoginWindow</c> 并把
    /// 捕获的 Cookie 经 <see cref="OnLoginCookieObtainedAsync"/> 回传。VM 不持窗口引用。
    /// </summary>
    public Action<OnlineProvider>? LoginRequested { get; set; }

    /// <summary>请求打开登录窗（左栏「登录」按钮）。</summary>
    [RelayCommand]
    private void RequestLogin(string? platform) =>
        LoginRequested?.Invoke(platform == "QQMusic" ? OnlineProvider.QQMusic : OnlineProvider.NetEase);

    /// <summary>
    /// 完全退出登录（2026-09-09 用户要求）：清除本地加密 Cookie 后刷新登录态。
    /// 用途：旧 Cookie 残缺（如缺 qm_keyst/uin 不匹配）导致 QQ 歌单能看不能播，
    /// 退出后重新扫码登录可获得完整凭据。
    /// </summary>
    [RelayCommand]
    private async Task RequestLogoutAsync(string? platform)
    {
        OnlineProvider provider = platform == "QQMusic" ? OnlineProvider.QQMusic : OnlineProvider.NetEase;
        if (_credentials is null)
        {
            OnlineStatusText = "凭据存储未就绪，无法退出";
            return;
        }

        _credentials.Clear(provider);
        OnlineStatusText = $"已退出登录（{provider}），请重新扫码登录";
        _log.Info($"[Music] 用户退出登录（{provider}），本地凭据已清除");
        await RefreshLoginStateAsync();
        await LoadPlaylistsAsync(); // 歌单面板随登出刷新（未登录态各平台自会给出明确提示）
    }

    /// <summary>View 捕获到 Cookie 后回传：加密落盘 + 刷新登录态。</summary>
    public async Task OnLoginCookieObtainedAsync(OnlineProvider provider, string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            OnlineStatusText = "登录未完成（未捕获到有效 Cookie）";
            return;
        }

        if (_credentials is null)
        {
            OnlineStatusText = "凭据存储未就绪，登录结果未保存";
            return;
        }

        _credentials.SetCookie(provider, cookie);
        OnlineStatusText = $"登录成功（{provider}），Cookie 已加密保存";
        await RefreshLoginStateAsync();
    }

    /// <summary>刷新两平台登录态（InitializeAsync 与平台切换时调用；异常在服务层已收敛）。</summary>
    [RelayCommand]
    private async Task RefreshLoginStateAsync()
    {
        if (_catalog is null)
        {
            NetEaseLoginText = "目录服务未就绪";
            QqLoginText = "目录服务未就绪";
            return;
        }

        try
        {
            OnlineLoginInfo netEase = await _catalog.GetLoginStatusAsync(OnlineProvider.NetEase);
            NetEaseLoginText = netEase.LoggedIn ? $"已登录 · {netEase.Nickname}" : "未登录";
            if (!netEase.LoggedIn && !string.IsNullOrEmpty(_catalog.CatalogError))
            {
                NetEaseLoginText = "检测失败";
            }

            OnlineLoginInfo qq = await _catalog.GetLoginStatusAsync(OnlineProvider.QQMusic);
            QqLoginText = qq.LoggedIn ? $"已登录 · {qq.Nickname}" : "未登录";
            if (!qq.LoggedIn && !string.IsNullOrEmpty(_catalog.CatalogError))
            {
                QqLoginText = "检测失败";
            }
        }
        catch (Exception ex)
        {
            // 审查 Y13（2026-09-10）：命令直调，异常必须落用户可见处（AsyncRelayCommand 会吞）
            OnlineStatusText = "登录状态刷新失败：" + ex.Message;
            _log.Error("[Music] 登录状态刷新异常", ex);
        }
    }
}
