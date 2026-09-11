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
        RefreshAccountArea(); // P3：账号区随平台联动（下拉里的"当前"标记与胶囊内容）
        // 审查 F-02：网络命令异常必须落用户可见状态（🔴 不静默——否则表现为"点击没反应"）
        try
        {
            // 🔴 审查 2026-09-11（🔴-2）：推荐区必须随平台刷新（官方榜单由它内部一并刷新）。
            // 原实现只刷榜单 + 歌单 → 切到 QQ 后右卡长期显示网易的每日推荐/推荐歌单
            // （chip 已变、内容没变），或刷新(A) 在飞期间切平台、被 A 的迟到结果覆盖。
            await LoadRecommendationsAsync();
            await LoadPlaylistsAsync(); // 歌单面板跟随平台（双平台用户歌单均已接线）
            await RefreshLoginStateAsync();
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            OnlineStatusText = "操作已取消或网络超时。";
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

    /// <summary>单条搜索历史的长度上限（🟠 审查 2026-09-11，🟠-7：超长粘贴不入库）。</summary>
    private const int MaxSearchHistoryLength = 64;

    public ObservableCollection<string> SearchHistory { get; } = [];

    public bool HasSearchHistory => SearchHistory.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineCommand))]
    private bool _isSearchingOnline;

    private bool CanSearchOnline() => !IsSearchingOnline;

    /// <summary>
    /// 在线目录请求代际（🟠 审查 2026-09-10）。
    /// <para>
    /// 播放链路有 <c>playSeq</c> 防护，在线目录此前没有：搜索/榜单/用户歌单三条命令互不等待，
    /// 用户搜 A 未完成就点赞榜（或改搜 B）时，A 迟到的结果会 Clear+Add 覆盖后来者的列表。
    /// 每个入口取号，await 回来先比号，不是最新一代即整批丢弃。
    /// </para>
    /// </summary>
    private int _onlineSeq;

    /// <summary>用户歌单加载代际（🟠-7：切换平台会连发两次加载，旧平台的歌单不得覆盖新平台）。</summary>
    private int _playlistSeq;

    /// <summary>
    /// 推荐区代际（🔴 审查 2026-09-11，🔴-2）。每日推荐 / 推荐歌单 / 榜单三者总是一起刷新，
    /// 故共用一个号。此处是 🟠-7 的**漏网点**：原实现只给搜索/歌单/榜单打开装了代际，
    /// 推荐区的三个写入点（以及"切平台不刷推荐"）没跟上。
    /// </summary>
    private int _recommendSeq;

    /// <summary>
    /// 歌单详情曲目代际（🔴-2）。**刻意与 <c>_onlineSeq</c> 分开**：两者写的是不同集合、
    /// 对应不同视图（歌单详情 vs 搜索结果），共用一个号会让"打开歌单"无谓作废进行中的搜索、
    /// 反之亦然。
    /// </summary>
    private int _playlistTracksSeq;

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

        int seq = ++_onlineSeq; // 🟠-7：本代请求号（迟到的旧结果整批丢弃）
        IsSearchingOnline = true;
        try
        {
            List<OnlineTrack> tracks = await _catalog.SearchAsync(SelectedPlatform, OnlineSearchText.Trim());
            if (seq != _onlineSeq)
            {
                return; // 期间用户点了榜单或又搜了一次 → 本次结果已过期
            }

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
            if (seq == _onlineSeq)
            {
                IsSearchingOnline = false; // 只有最新一代才复位（迟到者不得清掉后一代的"进行中"）
            }
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

        // 🟠 审查 2026-09-11（🟠-7）：外部输入先剥离零宽不可见字符再 Trim。
        // StripInvisible 覆盖 U+200B/200C/200D/2060/FEFF，而 Trim/正则 \s 都不匹配它们——
        // 否则含零宽的关键词会原样入库/渲染/落盘，且用户肉眼永远无法复现并删除该历史项。
        string q = (SystemToolkit.Core.Utilities.TextSanitizer.StripInvisible(query) ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return; // 剥完变空（整词都是零宽）：不入库
        }

        if (q.Length > MaxSearchHistoryLength)
        {
            q = q[..MaxSearchHistoryLength]; // 超长粘贴不入库（历史项只作展示与复搜用）
        }

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

        int seq = ++_playlistSeq; // 🟠-7
        IsLoadingPlaylists = true;
        try
        {
            List<OnlinePlaylist> playlists = await _catalog.LoadUserPlaylistsAsync(SelectedPlatform);
            if (seq != _playlistSeq)
            {
                return; // 平台已切换/再次加载 → 本批已过期
            }

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
            if (seq == _playlistSeq)
            {
                IsLoadingPlaylists = false;
            }
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
        int seq = ++_playlistTracksSeq; // 🔴-2：连点两个歌单时，A 的迟到结果不得覆盖 B
        // 审查 F-02：取曲目失败要可见（登录过期/网络/接口变更），不能 Task Faulted 静默
        try
        {
            // OM-8B（2026-09-12 打磨）：歌单分页全量加载——旧实现单页 limit=100，
            // 大歌单静默截断。循环取页直到服务端返回不足一页（或触达防失控上限），
            // 每页后校验代际（连点两个歌单时旧页作废），进度落状态行。
            const int PageSize = 500;
            const int MaxTracks = 2000;
            var tracks = new List<OnlineTrack>();
            int offset = 0;
            while (tracks.Count < MaxTracks)
            {
                List<OnlineTrack> page = await _catalog.LoadPlaylistTracksAsync(
                    row.Playlist.Provider, row.Playlist.Id, offset, PageSize);
                if (seq != _playlistTracksSeq)
                {
                    return; // 期间用户又打开了另一个歌单 → 本次结果已过期（不切视图、不写集合）
                }

                tracks.AddRange(page);
                if (page.Count < PageSize)
                {
                    break; // 末页
                }

                offset += page.Count;
                OnlineStatusText = $"已加载 {tracks.Count} 首…";
            }

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
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            OnlineStatusText = "操作已取消或网络超时。";
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
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            OnlineStatusText = "操作已取消或网络超时。";
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
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            OnlineStatusText = "操作已取消或网络超时。";
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

    /// <summary>官方榜单（仅 QQ 有来源；网易为空 → UI 整区隐藏）。</summary>
    public ObservableCollection<OnlineRankBoard> RankBoards { get; } = [];

    /// <summary>是否有榜单可显示（决定右栏榜单区是否出现）。</summary>
    public bool HasRankBoards => RankBoards.Count > 0;

    /// <summary>
    /// 每日推荐是否非空（P3 布局：为空时榜单区改两列大卡片占据右卡空间，
    /// 不至于让右栏空一大块）。
    /// </summary>
    public bool HasDailyRecommend => DailyRecommend.Count > 0;


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

        int seq = ++_recommendSeq; // 🔴-2：推荐区代际（与切平台 / 榜单刷新共用同一个号）
        IsLoadingRecommendations = true;
        try
        {
            List<OnlineTrack> daily = await _catalog.LoadDailyRecommendSongsAsync(SelectedPlatform);
            if (seq != _recommendSeq)
            {
                return; // 期间切了平台 / 又刷了一次 → 本次结果已过期
            }

            DailyRecommend.Clear();
            foreach (OnlineTrack track in daily)
            {
                DailyRecommend.Add(new OnlineResultRowVm(track));
            }
            BeginCoverLoads(DailyRecommend); // 实机反馈（图3）：每日推荐曲目也要显示封面（此前漏挂）
            OnPropertyChanged(nameof(HasDailyRecommend)); // P3：右卡布局随空态切换

            List<OnlinePlaylist> recommended = await _catalog.LoadRecommendationsAsync(SelectedPlatform);
            if (seq != _recommendSeq)
            {
                return;
            }

            RecommendedPlaylists.Clear();
            foreach (OnlinePlaylist playlist in recommended)
            {
                RecommendedPlaylists.Add(new PlaylistRowVm(playlist));
            }

            await LoadRankBoardsAsync(seq); // P3：官方榜单（网易为空 → 整区隐藏）

            if (seq != _recommendSeq)
            {
                return;
            }

            // 空态原因可见：未登录 / QQ 不支持 / 网络失败（🔴 不静默）
            OnlineStatusText = _catalog.CatalogError;
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            // 审查 v6（B1 残留）：收尾分支同样要带代际——过期代的超时不得覆盖最新代状态行
            if (seq == _recommendSeq)
            {
                OnlineStatusText = "操作已取消或网络超时。";
            }
        }
        catch (Exception ex)
        {
            // 审查 F-02：原只有 finally，异常被吞——用户只看到"加载中"消失
            OnlineStatusText = $"加载推荐失败：{ex.Message}";
            _log.Error("[Music] 加载每日推荐/推荐歌单失败", ex);
        }
        finally
        {
            // 🔴-2：只在仍是最新代时复位——迟到者不得清掉后一代的「加载中」标记
            if (seq == _recommendSeq)
            {
                IsLoadingRecommendations = false;
            }
        }
    }

    /// <summary>P3：加载官方榜单（网易无来源 → 空集合，UI 整区隐藏）。</summary>
    /// <param name="seq">调用方（推荐区）的代际号——本方法写的是推荐区同一批 UI 状态。</param>
    private async Task LoadRankBoardsAsync(int seq)
    {
        if (_catalog is null)
        {
            return;
        }

        try
        {
            List<OnlineRankBoard> boards = await _catalog.LoadRankListAsync(SelectedPlatform);
            if (seq != _recommendSeq)
            {
                return; // 🔴-2：旧平台的结果不得覆盖新平台
            }

            RankBoards.Clear();
            foreach (OnlineRankBoard board in boards)
            {
                RankBoards.Add(board);
            }

            OnPropertyChanged(nameof(HasRankBoards));
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            // 审查 v6（B1 残留）：收尾分支同样要带代际
            if (seq == _recommendSeq)
            {
                OnlineStatusText = "操作已取消或网络超时。";
            }
        }
        catch (Exception ex)
        {
            // 🟠 审查 2026-09-11（🟠-5）：原实现只 _log.Warn，而注释自称"🔴 不静默"——
            // UI 上表现为榜单区整块消失，用户无法区分「网易无此来源（设计如此）」与
            // 「QQ 加载失败」。失败必须落到用户可见处。
            if (seq == _recommendSeq)
            {
                _log.Warn($"[Music] 榜单加载失败：{ex.Message}");
                OnlineStatusText = $"榜单加载失败：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// P3：点榜单卡片 → 载入该榜歌曲，作为可播放队列展示（复用搜索结果视图与双击播放管线）。
    /// </summary>
    [RelayCommand]
    private async Task OpenRankBoardAsync(OnlineRankBoard? board)
    {
        if (board is null)
        {
            return;
        }

        if (_catalog is null)
        {
            OnlineStatusText = "在线目录服务未就绪，无法载入榜单";
            return;
        }

        int seq = ++_onlineSeq; // 🟠-7：与搜索共用代际（两者写同一个 SearchResults）
        IsSearchingOnline = true;
        try
        {
            List<OnlineTrack> songs = await _catalog.LoadRankSongsAsync(SelectedPlatform, board.Id, 30);
            if (seq != _onlineSeq)
            {
                return; // 期间用户又搜了一次/换了榜 → 本次结果已过期
            }

            SearchResults.Clear();
            foreach (OnlineTrack track in songs)
            {
                SearchResults.Add(new OnlineResultRowVm(track));
            }

            BeginCoverLoads(SearchResults);
            OnlineStatusText = songs.Count == 0
                ? $"榜单「{board.Name}」暂无曲目（{_catalog.CatalogError}）"
                : $"已载入榜单「{board.Name}」{songs.Count} 首 — 双击曲目即以其为起点播放";
            CurrentView = ContentViewMode.OnlineSearch;
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            OnlineStatusText = "操作已取消或网络超时。";
        }
        catch (Exception ex)
        {
            OnlineStatusText = $"载入榜单失败：{ex.Message}";
            _log.Error($"[Music] 载入榜单失败（{board.Name}）", ex);
        }
        finally
        {
            if (seq == _onlineSeq)
            {
                IsSearchingOnline = false;
            }
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
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            OnlineStatusText = "操作已取消或网络超时。";
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

    // ════════ P3 头部账号区（对齐 NexBox 账号 widget）════════

    private OnlineLoginInfo? _netEaseLogin;
    private OnlineLoginInfo? _qqLogin;

    /// <summary>平台切换菜单行（头像/昵称/已登录徽标/添加平台）。</summary>
    public ObservableCollection<PlatformAccountRowVm> PlatformAccounts { get; } = [];

    /// <summary>当前平台键（"NetEase"/"QQMusic"），退出按钮参数用。</summary>
    public string CurrentPlatformKey => SelectedPlatform == OnlineProvider.NetEase ? "NetEase" : "QQMusic";

    private bool _isCurrentAccountLoggedIn;

    /// <summary>当前平台是否已登录（决定胶囊 vs 登录按钮）。</summary>
    public bool IsCurrentAccountLoggedIn
    {
        get => _isCurrentAccountLoggedIn;
        private set => SetProperty(ref _isCurrentAccountLoggedIn, value);
    }

    private bool _isCheckingLogin;

    /// <summary>
    /// 登录态检测进行中（🟠 审查 2026-09-11，🟠-3）。
    /// <para>
    /// 背景：<see cref="RefreshLoginStateAsync"/> 两个平台是**串行 await**，期间
    /// <see cref="IsCurrentAccountLoggedIn"/> 仍是上一次的值（首屏为默认 <c>false</c>）——
    /// 已登录用户会短暂看到"登录"按钮，网络差时窗口更长，会诱导重复登录。
    /// View 据此显示"检测中…"且禁用，构成三态：检测中 / 已登录 / 未登录。
    /// </para>
    /// </summary>
    public bool IsCheckingLogin
    {
        get => _isCheckingLogin;
        private set => SetProperty(ref _isCheckingLogin, value);
    }

    /// <summary>登录态检测代际（🟠-3：初始化与切平台可能各发一次，迟到者不得复位后一代的标记）。</summary>
    private int _loginCheckSeq;

    private string _currentAccountName = "未登录";

    /// <summary>当前平台账号昵称（未登录为 "未登录"）。</summary>
    public string CurrentAccountName
    {
        get => _currentAccountName;
        private set => SetProperty(ref _currentAccountName, value);
    }

    private string _currentAccountAvatarUrl = string.Empty;

    /// <summary>当前平台账号头像 URL（空则由 View 用首字色块兜底）。</summary>
    public string CurrentAccountAvatarUrl
    {
        get => _currentAccountAvatarUrl;
        private set => SetProperty(ref _currentAccountAvatarUrl, value);
    }

    /// <summary>当前账号是否有可用头像 URL（View 用它决定是否挂 ImageBrush，避免空源解码告警）。</summary>
    public bool HasCurrentAccountAvatar => !string.IsNullOrWhiteSpace(CurrentAccountAvatarUrl);

    private string _currentAccountInitial = "?";

    /// <summary>头像兜底首字（无头像 URL 时显示）。</summary>
    public string CurrentAccountInitial
    {
        get => _currentAccountInitial;
        private set => SetProperty(ref _currentAccountInitial, value);
    }

    private string _currentVipBadge = string.Empty;

    /// <summary>VIP 徽标文本：SVIP / VIP / 空（口径见 <see cref="VipBadgeOf"/>）。</summary>
    public string CurrentVipBadge
    {
        get => _currentVipBadge;
        private set => SetProperty(ref _currentVipBadge, value);
    }

    /// <summary>
    /// 重算头部账号区（切换平台与登录态刷新后调用）。
    /// 徽章口径对照 NexBox：<c>is_vip = vip_type &gt;= 1</c>、<c>is_svip = vip_type &gt;= 10</c>。
    /// </summary>
    private void RefreshAccountArea()
    {
        OnlineLoginInfo? current = SelectedPlatform == OnlineProvider.NetEase ? _netEaseLogin : _qqLogin;

        IsCurrentAccountLoggedIn = current?.LoggedIn == true;
        CurrentAccountName = IsCurrentAccountLoggedIn && !string.IsNullOrWhiteSpace(current!.Nickname)
            ? current.Nickname!
            : "未登录";
        CurrentAccountAvatarUrl = IsCurrentAccountLoggedIn ? current!.AvatarUrl ?? string.Empty : string.Empty;
        OnPropertyChanged(nameof(HasCurrentAccountAvatar));
        CurrentAccountInitial = CurrentAccountName.Length > 0 ? CurrentAccountName[..1] : "?";
        CurrentVipBadge = IsCurrentAccountLoggedIn ? VipBadgeOf(current!.VipType) : string.Empty;

        PlatformAccounts.Clear();
        PlatformAccounts.Add(BuildAccountRow(OnlineProvider.NetEase, "网易云", _netEaseLogin));
        PlatformAccounts.Add(BuildAccountRow(OnlineProvider.QQMusic, "QQ 音乐", _qqLogin));
    }

    private PlatformAccountRowVm BuildAccountRow(OnlineProvider provider, string platformName, OnlineLoginInfo? info)
    {
        bool loggedIn = info?.LoggedIn == true;
        return new PlatformAccountRowVm
        {
            PlatformKey = provider == OnlineProvider.NetEase ? "NetEase" : "QQMusic",
            PlatformName = platformName,
            LoggedIn = loggedIn,
            DisplayName = loggedIn && !string.IsNullOrWhiteSpace(info!.Nickname) ? info.Nickname! : "未登录",
            Initial = loggedIn && !string.IsNullOrWhiteSpace(info!.Nickname) ? info.Nickname![..1] : "＋",
            VipBadge = loggedIn ? VipBadgeOf(info!.VipType) : string.Empty,
            IsCurrent = provider == SelectedPlatform,
        };
    }

    /// <summary>VIP 徽标口径（NexBox netease.rs:589-590 同义）：&gt;=10 SVIP，&gt;=1 VIP，其余无。</summary>
    private static string VipBadgeOf(int vipType) => vipType >= 10 ? "SVIP" : vipType >= 1 ? "VIP" : string.Empty;

    /// <summary>平台切换菜单的一行（平台名 + 昵称/未登录 + 徽标 + 是否当前）。</summary>
    public sealed class PlatformAccountRowVm
    {
        public string PlatformKey { get; init; } = string.Empty;
        public string PlatformName { get; init; } = string.Empty;
        public bool LoggedIn { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string Initial { get; init; } = "＋";
        public string VipBadge { get; init; } = string.Empty;
        public bool IsCurrent { get; init; }
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
        // 🟡 审查 2026-09-11（F-6）：登出必须一并刷新推荐区——否则右栏的每日推荐/推荐歌单/榜单
        // 仍显示**上一个登录用户**的个性化内容（陈旧，且属隐私面的残留）。
        // 与 SelectPlatformAsync 的三连刷对齐；LoadRecommendationsAsync 自带 _recommendSeq 代际，
        // 重复调用安全。
        await LoadRecommendationsAsync();
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
        // 🟡 审查 2026-09-11（F-6）：登录成功后拉取**本账号**的个性化推荐（与切平台三连刷对齐）
        await LoadRecommendationsAsync();
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

        // 🟠 审查 2026-09-11（🟠-3）：进入"检测中"——View 据此显示"检测中…"并禁用，
        // 避免已登录用户在网络慢时看到"登录"按钮而重复点击。
        int seq = ++_loginCheckSeq;
        IsCheckingLogin = true;
        try
        {
            OnlineLoginInfo netEase = await _catalog.GetLoginStatusAsync(OnlineProvider.NetEase);
            _netEaseLogin = netEase;
            NetEaseLoginText = netEase.LoggedIn ? $"已登录 · {netEase.Nickname}" : "未登录";
            if (!netEase.LoggedIn && !string.IsNullOrEmpty(_catalog.CatalogError))
            {
                NetEaseLoginText = "检测失败";
            }

            OnlineLoginInfo qq = await _catalog.GetLoginStatusAsync(OnlineProvider.QQMusic);
            _qqLogin = qq;
            QqLoginText = qq.LoggedIn ? $"已登录 · {qq.Nickname}" : "未登录";
            if (!qq.LoggedIn && !string.IsNullOrEmpty(_catalog.CatalogError))
            {
                QqLoginText = "检测失败";
            }

            RefreshAccountArea(); // P3：头部账号胶囊 + 平台切换菜单
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            OnlineStatusText = "操作已取消或网络超时。";
        }
        catch (Exception ex)
        {
            // 审查 Y13（2026-09-10）：命令直调，异常必须落用户可见处（AsyncRelayCommand 会吞）
            OnlineStatusText = "登录状态刷新失败：" + ex.Message;
            _log.Error("[Music] 登录状态刷新异常", ex);
        }
        finally
        {
            // 🟠-3：只在仍是最新一代时退出"检测中"（迟到者不得清掉后一代的标记）
            if (seq == _loginCheckSeq)
            {
                IsCheckingLogin = false;
            }
        }
    }
}
