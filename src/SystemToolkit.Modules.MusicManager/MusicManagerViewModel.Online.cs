using System.Collections.ObjectModel;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Online;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
        /// <summary>本地曲库（扫描/过滤/双击播放，现有能力）。</summary>
        LocalLibrary,

        /// <summary>在线搜索结果。</summary>
        OnlineSearch,

        /// <summary>歌单详情（在线歌单的曲目列表）。</summary>
        PlaylistDetail,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalLibraryView))]
    [NotifyPropertyChangedFor(nameof(IsOnlineSearchView))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistDetailView))]
    [NotifyPropertyChangedFor(nameof(ViewTitle))]
    private ContentViewMode _currentView = ContentViewMode.LocalLibrary;

    /// <summary>当前是否为本地曲库视图（XAML 三态可见性绑定）。</summary>
    public bool IsLocalLibraryView => CurrentView == ContentViewMode.LocalLibrary;

    /// <summary>当前是否为在线搜索视图。</summary>
    public bool IsOnlineSearchView => CurrentView == ContentViewMode.OnlineSearch;

    /// <summary>当前是否为歌单详情视图。</summary>
    public bool IsPlaylistDetailView => CurrentView == ContentViewMode.PlaylistDetail;

    /// <summary>内容区标题（跟随视图状态）。</summary>
    public string ViewTitle => CurrentView switch
    {
        ContentViewMode.OnlineSearch => $"搜索结果 · {SelectedPlatformText}",
        ContentViewMode.PlaylistDetail => OpenPlaylist?.Name ?? "歌单详情",
        _ => "本地曲库",
    };

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
        await LoadPlaylistsAsync(); // 歌单面板跟随平台（双平台用户歌单均已接线）
        await RefreshLoginStateAsync();
    }

    // ════════ 在线搜索 ════════

    [ObservableProperty]
    private string _onlineSearchText = string.Empty;

    /// <summary>在线搜索结果行（<see cref="ToQueueSong"/> 可直接转队列曲）。</summary>
    public sealed record OnlineResultRowVm(OnlineTrack Track)
    {
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
    }

    public ObservableCollection<OnlineResultRowVm> SearchResults { get; } = [];

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

            OnlineStatusText = _catalog.CatalogError; // 空结果/失败原因可见（🔴 不静默）
            CurrentView = ContentViewMode.OnlineSearch;
        }
        finally
        {
            IsSearchingOnline = false;
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

    /// <summary>歌单行（左栏列表与推荐歌单共用）。</summary>
    public sealed record PlaylistRowVm(OnlinePlaylist Playlist)
    {
        /// <summary>歌单名。</summary>
        public string Title => Playlist.Name;

        /// <summary>曲目数展示。</summary>
        public string CountText => Playlist.TrackCount == 0 ? string.Empty : $"{Playlist.TrackCount} 首";
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

            // QQ 无此能力等场景：错误说明透传（🔴 不静默）
            OnlineStatusText = _catalog.CatalogError;
        }
        finally
        {
            IsLoadingPlaylists = false;
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

    [RelayCommand]
    private async Task OpenPlaylistAsync(PlaylistRowVm? row)
    {
        if (row is null || _catalog is null)
        {
            return;
        }

        OpenPlaylist = row.Playlist;
        OnPropertyChanged(nameof(ViewTitle));
        List<OnlineTrack> tracks = await _catalog.LoadPlaylistTracksAsync(
            row.Playlist.Provider, row.Playlist.Id);
        PlaylistTracks.Clear();
        foreach (OnlineTrack track in tracks)
        {
            PlaylistTracks.Add(new OnlineResultRowVm(track));
        }

        OnlineStatusText = _catalog.CatalogError;
        CurrentView = ContentViewMode.PlaylistDetail;
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
        await PlayOnlineTracksAsync(tracks, tracks[0]);
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
        await PlayOnlineTracksAsync(tracks, row.Track);
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

            List<OnlinePlaylist> recommended = await _catalog.LoadRecommendationsAsync(SelectedPlatform);
            RecommendedPlaylists.Clear();
            foreach (OnlinePlaylist playlist in recommended)
            {
                RecommendedPlaylists.Add(new PlaylistRowVm(playlist));
            }

            // 空态原因可见：未登录 / QQ 不支持 / 网络失败（🔴 不静默）
            OnlineStatusText = _catalog.CatalogError;
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
        await PlayOnlineTracksAsync(tracks, row.Track);
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
}
