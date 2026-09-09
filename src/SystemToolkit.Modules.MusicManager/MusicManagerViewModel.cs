using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Core.Music.Services;
using SystemToolkit.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理 VM（MUSIC-6）：曲库/播放/歌词三 Tab。
/// 编排链：曲库 = <see cref="LocalMusicScanner"/> 扫描 + <see cref="IMusicLibraryStore"/> 持久化；
/// 播放 = <see cref="IPlaybackQueueService"/> 决策（MUSIC-5）→ <see cref="IMusicPlaybackEngine"/> 播放（MUSIC-4）；
/// 歌词 = <see cref="LyricParser"/>（MUSIC-3，.lrc 优先、内嵌兜底）。
/// </summary>
/// <remarks>
/// <para><b>引擎可空</b>：<see cref="IMusicPlaybackEngine"/> 的实现由 MUSIC-4 提供，
/// 未合入前 DI 解析为 null——曲库扫描/浏览/歌词全部可用，播放控制禁用并显示
/// 「播放引擎未就绪」，模块不因缺引擎而不可用（模块故障隔离）。</para>
/// </remarks>
public partial class MusicManagerViewModel : ObservableObject
{
    private readonly IMusicLibraryStore _store;
    private readonly LocalMusicScanner _scanner;
    private readonly IPlaybackQueueService _queue;
    private readonly ILogger _log;

    // 引擎为「可选/延迟」依赖（MUSIC-4 合入前模块仍可用）——经工厂委托注入保持依赖显式，
    // 不用 IServiceProvider 服务定位器（2026-09-08 审查采纳项）。
    private readonly Func<IMusicPlaybackEngine?>? _engineProvider;
    private readonly IMusicTagReader? _tagReader;

    // 在线播放组件（OM-4，均可空：在线组件缺席时本地播放完全不受影响——故障隔离同引擎语义）
    private readonly IOnlineUrlResolver? _urlResolver;
    private readonly IAudioProxyService? _audioProxy;

    /// <summary>在线目录服务（OM-5：搜索/歌单/推荐/登录态/在线歌词；可空，缺席时在线浏览降级）。</summary>
    private readonly IOnlineMusicCatalogService? _catalog;

    /// <summary>Cookie 凭据存储（OM-1，Core 契约；登录窗回传的 Cookie 加密落盘用）。</summary>
    private readonly IOnlineCredentialStore? _credentials;
    private CancellationTokenSource? _scanCts;

    /// <summary>扫描进度文本上次刷新时刻（审查 🟠-2：进度回调节流用，避免每首改一次 UI 文本）。</summary>
    private DateTime _lastScanProgressAt = DateTime.MinValue;

    /// <summary>
    /// 播放请求竞态序号：每次 PlayCurrentCoreAsync 递增；异步链每步之后校验，
    /// 过期请求（用户已切走/跳过策略已触发下一次）直接放弃回写——
    /// 对照 NexBox music-store 的 playSongSeq 防竞态设计（慢 URL 解析不得覆盖新曲目状态）。
    /// </summary>
    private long _playSeq;

    /// <summary>在线曲目连续不可播计数（成功起播归零；达到 <see cref="OnlineSkipPolicy.MaxConsecutiveSkips"/> 停止自动切曲）。</summary>
    private int _consecutiveOnlineFailures;

    /// <summary>扫描根目录缓存（LoadAsync 时刷新，避免扫描时重复读曲库 JSON）。</summary>
    private List<string> _scanRoots = [];

    /// <summary>
    /// UI 线程 Dispatcher（构造时捕获——VM 由 View 在 UI 线程构造，Application 已就绪）。
    /// 🔴 引擎契约允许 StateChanged/PositionChanged 在非 UI 线程触发（NAudio Timer/回调线程），
    /// 而本 VM 的事件处理器会改 LyricRows/UpNext（ObservableCollection）——
    /// 绑定激活时跨线程改集合直接抛异常闪退（2026-09-08 真机 0xE0434352 实证）。
    /// 所有引擎事件必须经 <see cref="RunOnUi"/> 编组。
    /// 测试宿主无 Application → Dispatcher 为 null → 直接执行（绑定未激活，安全）。
    /// </summary>
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;

    /// <summary>
    /// UI 线程编组：Dispatcher 缺失、已停机或其所属线程已退出时直接执行
    /// （测试宿主里 Application.Current 可能是已退出的冒烟 STA——BeginInvoke 会永不执行）；
    /// 同线程直接执行；否则 BeginInvoke。
    /// </summary>
    private void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? d = _dispatcher;
        if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive)
        {
            RunGuarded(action);
        }
        else if (d.CheckAccess())
        {
            RunGuarded(action);
        }
        else
        {
            d.BeginInvoke(() => RunGuarded(action));
        }
    }

    /// <summary>
    /// 🔴 处理器异常不得反噬引擎调用方：直执行路径的异常会沿同步调用链
    /// 传回引擎 PlayAsync 的 catch 被误报为「播放失败」（2026-09-08 实证）。
    /// </summary>
    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ScanStatusText = $"界面更新异常：{ex.Message}";
            _log.Error("[Music] UI 事件处理器异常", ex);
        }
    }
    private List<LyricLine> _lyricLineSource = [];

    public MusicManagerViewModel(
        IMusicLibraryStore store,
        LocalMusicScanner scanner,
        IPlaybackQueueService queue,
        ILogger log,
        Func<IMusicPlaybackEngine?>? engineProvider = null,
        IMusicTagReader? tagReader = null,
        System.Windows.Threading.Dispatcher? dispatcher = null,
        IOnlineUrlResolver? urlResolver = null,
        IAudioProxyService? audioProxy = null,
        IOnlineMusicCatalogService? catalog = null,
        IOnlineCredentialStore? credentials = null)
    {
        // dispatcher：测试显式传 null 禁编组（无绑定激活的环境直执行安全）；
        // 生产由模块 DI 工厂显式传 UI 线程 Dispatcher（不可回退全局捕获——
        // 测试宿主的 Application.Current 可能指向已退出的冒烟 STA，BeginInvoke 永不执行）
        _dispatcher = dispatcher;
        _store = store;
        _scanner = scanner;
        _queue = queue;
        _log = log;
        _engineProvider = engineProvider;
        _tagReader = tagReader;
        _urlResolver = urlResolver;
        _audioProxy = audioProxy;
        _catalog = catalog;
        _credentials = credentials;

        Songs.CollectionChanged += (_, _) =>
        {
            if (!_bulkLoadingSongs)
            {
                LibraryCountText = $"曲库 {Songs.Count} 首";
            }
        };
        _songsFilter = o => o is MusicSong s && MatchesFilter(s);
        SongsView = CollectionViewSource.GetDefaultView(Songs);
        SongsView.Filter = _songsFilter;
        // 图2 对齐（2026-09-09）：歌单详情内搜索——过滤当前歌单曲目（标题/副题/艺人）
        PlaylistTracksView = CollectionViewSource.GetDefaultView(PlaylistTracks);
        PlaylistTracksView.Filter = o => o is OnlineResultRowVm r && MatchesPlaylistFilter(r);
        // 🔴 队列服务契约未承诺事件线程（审查 🔴-2 防御性采纳）：与引擎事件同规则——
        // 一律经 RunOnUi 编组后再碰 ObservableCollection / 触发绑定刷新
        _queue.QueueChanged += () => RunOnUi(RebuildUpNext);
        _queue.CurrentChanged += () => RunOnUi(OnQueueCurrentChanged);

        // 引擎事件在 InitializeAsync 里解析引擎后接线（引擎实现可能晚于本模块合入）
    }

    // ════════ 曲库 ════════

    public ObservableCollection<MusicSong> Songs { get; } = [];

    private string _libraryCountText = "曲库 0 首";
    public string LibraryCountText { get => _libraryCountText; private set => SetProperty(ref _libraryCountText, value); }

    private string _filterText = string.Empty;
    /// <summary>搜索框：标题/艺术家/专辑任一包含即命中（忽略大小写）。</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                SongsView.Refresh();
            }
        }
    }

    /// <summary>曲库列表视图（搜索过滤 + 虚拟化由 ItemsControl 承担）。</summary>
    public ICollectionView SongsView { get; private set; } = null!;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayFromLibraryCommand))]
    private MusicSong? _selectedSong;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    private string _scanStatusText = string.Empty;
    public string ScanStatusText { get => _scanStatusText; private set => SetProperty(ref _scanStatusText, value); }

    private string _libraryWarning = string.Empty;
    /// <summary>曲库加载降级警告（缓存损坏/剔除消失文件），空 = 正常。🔴 不静默。</summary>
    public string LibraryWarning { get => _libraryWarning; private set => SetProperty(ref _libraryWarning, value); }

    /// <summary>View Loaded 时注入的文件夹选择回调（与 FileBackup.RestoreRequest 同款注入模式）。</summary>
    public Func<string?>? PickFolder { get; set; }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanAsync()
    {
        WireEngineOnce(); // 扫描按钮不受 IsEngineReady 禁用——是引擎热接通后的自愈入口（审查 🔴-2 采纳）
        string? picked = PickFolder?.Invoke();
        if (string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked))
        {
            return; // 用户取消或路径无效——无副作用
        }

        string root = picked;
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanStatusText = "扫描中…";
        _log.Info($"[Music] 开始扫描根目录：{root}");

        try
        {
            // 合并根目录（多值假设：允许多个扫描根，不覆盖已有；根清单来自 LoadAsync 缓存）
            List<string> roots = [.. _scanRoots];
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
            }

            MusicScanResult result = await _scanner.ScanAsync(
                roots,
                // 审查 🟠-2 采纳（2026-09-09）：扫描数千首时 Progress 每秒可报数十次，
                // 逐次改文本会拖累 UI——节流到 ~5 次/秒，且 100% 必定上报（收尾态不丢）
                new Progress<MusicScanProgress>(p =>
                {
                    DateTime now = DateTime.Now;
                    if (p.Percent < 100 && (now - _lastScanProgressAt).TotalMilliseconds < 200)
                    {
                        return;
                    }

                    _lastScanProgressAt = now;
                    ScanStatusText = $"扫描中… {p.Percent}%（{p.Scanned}/{p.Total}）";
                }),
                _scanCts.Token);

            // 合并/替换策略（审查 🔴-1 采纳）：
            // 完整扫描（未取消）→ 以本轮结果替换，磁盘已删除的曲目不再残留为僵尸条目；
            //   例外：本轮解析失败（Failures）的文件「缺席 ≠ 已删除」，按路径保留旧条目避免误清；
            // 取消扫描 → 本轮只是部分结果，维持「旧库 + 本轮已扫到」合并
            Dictionary<string, MusicSong> oldByPath = new(StringComparer.OrdinalIgnoreCase);
            foreach (MusicSong s in Songs)
            {
                oldByPath[s.LocalPath] = s;
            }

            Dictionary<string, MusicSong> merged = result.WasCancelled
                ? new Dictionary<string, MusicSong>(Songs.ToDictionary(s => s.Id))
                : [];
            foreach (MusicSong song in result.Songs)
            {
                merged[song.Id] = song;
            }

            if (!result.WasCancelled)
            {
                foreach (MusicScanFailure failure in result.Failures)
                {
                    if (oldByPath.TryGetValue(failure.FilePath, out MusicSong? kept))
                    {
                        merged[kept.Id] = kept;
                    }
                }
            }

            var library = new MusicLibrary
            {
                ScanRoots = roots,
                Songs = [.. merged.Values.OrderBy(s => s.LocalPath, StringComparer.OrdinalIgnoreCase)],
                LastScanAtUtc = DateTimeOffset.UtcNow,
            };

            ApplyLibrary(library);

            await _store.SaveAsync(library);
            // 🔴 缓存同步（复审 🟠-1）：不同步 _scanRoots 的话，下一次扫描会基于过期根清单
            // 构造 roots → 上一次新增根的曲目被「替换策略」整体清掉
            _scanRoots = [.. roots];
            if (!result.WasCancelled)
            {
                // 扫描成功即曲库已重建，旧的加载告警（如缓存损坏降级）随之失效（审查 🟠-5 采纳）
                LibraryWarning = string.Empty;
            }
            ScanStatusText = result.WasCancelled
                ? $"扫描已取消（已扫 {result.ProcessedCount} 个文件，结果已保留）"
                : result.IsClean
                    ? $"扫描完成：{result.Songs.Count} 首"
                    : $"扫描完成：{result.Songs.Count} 首，{result.Failures.Count} 个失败，{result.InaccessiblePaths.Count} 个目录不可达";
            _log.Info($"[Music] 扫描结束：songs={result.Songs.Count} failures={result.Failures.Count} inaccessible={result.InaccessiblePaths.Count} canceled={result.WasCancelled}");
        }
        catch (Exception ex)
        {
            // 扫描自身已兜住大部分异常；走到这里属意外，显式告知 🔴 不静默
            ScanStatusText = $"扫描失败：{ex.Message}";
            _log.Error("[Music] 扫描意外失败", ex);
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private bool CanStartScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _scanCts?.Cancel();

    // ════════ 播放 ════════

    /// <summary>解析引擎（可选依赖：DI 未注册时为 null，播放禁用）。每次操作前取，便于热接通。</summary>
    private IMusicPlaybackEngine? ResolveEngine() => _engineProvider?.Invoke();

    /// <summary>
    /// 引擎事件是否已接线（审查 F-2 加固，2026-09-09）：int 而非 bool——
    /// 用 <see cref="Interlocked.CompareExchange(ref int, int, int)"/> 原子抢占，
    /// 避免两个入口同时判定"未接线"而重复订阅引擎事件（重复订阅会让同一事件触发两次、状态双写）。
    /// 0 = 未接线，1 = 已接线。
    /// </summary>
    private int _engineWired;

    private string _currentTitle = "未在播放";
    public string CurrentTitle { get => _currentTitle; private set => SetProperty(ref _currentTitle, value); }

    private string _currentSub = string.Empty;
    public string CurrentSub { get => _currentSub; private set => SetProperty(ref _currentSub, value); }

    private string _currentArtistText = string.Empty;

    /// <summary>当前艺人（现代模板三行排版用）。</summary>
    public string CurrentArtistText
    {
        get => _currentArtistText;
        private set => SetProperty(ref _currentArtistText, value);
    }

    private string _currentAlbumText = string.Empty;

    /// <summary>当前专辑（现代模板三行排版用）。</summary>
    public string CurrentAlbumText
    {
        get => _currentAlbumText;
        private set => SetProperty(ref _currentAlbumText, value);
    }

    private string _positionCurrentText = "--:--";
    /// <summary>当前播放时间（分列显示——完整播放器与主屏底栏统一用分列）。</summary>
    public string PositionCurrentText { get => _positionCurrentText; private set => SetProperty(ref _positionCurrentText, value); }

    private string _positionDurationText = "--:--";
    /// <summary>总时长文本。</summary>
    public string PositionDurationText { get => _positionDurationText; private set => SetProperty(ref _positionDurationText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    private double _lyricProgress;

    /// <summary>当前歌词行内进度 0–1（反馈 4：卡拉OK渐变填充按行推进；无歌词/间奏为 0）。</summary>
    public double LyricProgress
    {
        get => _lyricProgress;
        private set => SetProperty(ref _lyricProgress, value);
    }

    private string _modeText = "列表循环";
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }

    /// <summary>播放模式图标态（list/shuffle/one，图3 反馈：模式切换必须看得见）。</summary>
    public string ModeIconKind => _queue.Mode switch
    {
        PlayMode.Shuffle => "shuffle",
        PlayMode.One => "one",
        _ => "list",
    };

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    /// <summary>期望在线音质（网易：jymaster/hires/lossless/exhigh/standard；QQ 当前固定 standard）。</summary>
    /// <remarks>音质选择 UI 在 OM-6 播放器还原时接线；未知值由网易客户端回退 exhigh。</remarks>
    [ObservableProperty]
    private string _preferredQuality = "exhigh";

    public ObservableCollection<MusicSong> UpNext { get; } = [];

    [ObservableProperty]
    private MusicSong? _queueCurrent; // IsEngineReady 与队列无关（复审 🟠-2：移除无语义的通知挂接）

    public bool IsEngineReady => ResolveEngine() is not null;

    [RelayCommand]
    private async Task PlayFromLibraryAsync()
    {
        WireEngineOnce(); // 引擎热接通自愈：若此前未接线，此时补接并刷新 IsEngineReady（审查 🔴-2 采纳）
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null || SelectedSong is null)
        {
            // 引擎未就绪时底部条有常驻提示；这里补状态行让双击也有可见反馈（🔴 不静默）
            ScanStatusText = engine is null ? "播放引擎未就绪，无法播放" : "请先选中一首曲目";
            return;
        }

        // 双击 = 以完整曲库为队列、双击项为起点重建队列（对照主流播放器）
        _queue.SetQueue(Songs, SelectedSong);
        await PlayCurrentCoreAsync(engine);
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        WireEngineOnce(); // 引擎热接通自愈（审查 🔴-2 采纳）
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null)
        {
            ScanStatusText = "播放引擎未就绪，无法播放";
            return;
        }

        if (IsPlaying)
        {
            // 🔴 AsyncRelayCommand 会吞异常（项目已知坑）——同步引擎操作必须就地显式化（审查 🟠-4 采纳）
            try
            {
                engine.Pause();
            }
            catch (Exception ex)
            {
                ScanStatusText = $"暂停失败：{ex.Message}";
                _log.Warn($"[Music] 暂停失败：{ex.Message}");
            }

            return;
        }

        if (QueueCurrent is not null)
        {
            try
            {
                engine.Resume();
            }
            catch (Exception ex)
            {
                ScanStatusText = $"恢复播放失败：{ex.Message}";
                _log.Warn($"[Music] 恢复播放失败：{ex.Message}");
            }

            return;
        }

        // 程序启动后尚未播放过：以选中曲起播（未选中则从首曲开始），
        // 🔴 不得静默早退——这是播放按钮的主路径（2026-09-08 真机「没声音」回归修复）
        MusicSong? start = SelectedSong ?? Songs.FirstOrDefault();
        if (start is null)
        {
            ScanStatusText = "曲库为空，请先扫描添加音乐";
            return;
        }

        _queue.SetQueue(Songs, start);
        await PlayCurrentCoreAsync(engine);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null || _queue.PickNext() is null)
        {
            return;
        }

        await PlayCurrentCoreAsync(engine);
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null || _queue.PickPrevious() is null)
        {
            return;
        }

        await PlayCurrentCoreAsync(engine);
    }

    [RelayCommand]
    private void ToggleMode()
    {
        _queue.Mode = _queue.Mode switch
        {
            PlayMode.List => PlayMode.Shuffle,
            PlayMode.Shuffle => PlayMode.One,
            _ => PlayMode.List,
        };
        ModeText = _queue.Mode switch
        {
            PlayMode.Shuffle => "随机播放",
            PlayMode.One => "单曲循环",
            _ => "列表循环",
        };
        OnPropertyChanged(nameof(ModeIconKind));
    }

    [RelayCommand]
    private void RemoveFromQueue(string songId) => _queue.RemoveFromQueue(songId);

    // ════════ 歌词 ════════

    /// <summary>歌词行包装（模型 <see cref="LyricLine"/> 无 UI 状态，IsActive 由 VM 维护）。</summary>
    /// <remarks>
    /// 🔴 必须是可变 class + INPC，禁止 record + 集合 Replace（2026-09-09 实测滚动追帧根因）：
    /// <c>_lyricRows[i] = row with { … }</c> 在 ItemsCollection 里是 Remove+Insert 语义，
    /// 每次 IsActive 切换重建两个行容器 → 布局抖动把 ScrollViewer 位置顶飞（日志实证
    /// offset 冲过 target 直至 clamp 底部）→ 视口"上面歌词逐行消失后才追上来"的空白块。
    /// </remarks>
    public sealed class LyricRowVm : System.ComponentModel.INotifyPropertyChanged
    {
        public LyricRowVm(string text, bool isActive)
        {
            _text = text;
            _isActive = isActive;
        }

        private readonly string _text;

        /// <summary>行文本（歌词重载时整列表重建，故只读）。</summary>
        public string Text => _text;

        private bool _isActive;

        /// <summary>是否当前行（就地通知，不重建容器）。</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    // 复用同一实例（Clear+Add），避免切歌时整体替换触发重绑定/闪烁（2026-09-08 审查采纳项）
    private readonly ObservableCollection<LyricRowVm> _lyricRows = [];
    public ObservableCollection<LyricRowVm> LyricRows => _lyricRows;

    private string _plainLyrics = string.Empty;
    public string PlainLyrics { get => _plainLyrics; private set => SetProperty(ref _plainLyrics, value); }

    private bool _hasLyrics;
    public bool HasLyrics { get => _hasLyrics; private set => SetProperty(ref _hasLyrics, value); }

    private int _activeLyricIndex = -1;
    public int ActiveLyricIndex
    {
        get => _activeLyricIndex;
        private set
        {
            if (!SetProperty(ref _activeLyricIndex, value))
            {
                return;
            }

            // 精准只更新新旧两行（就地 INPC 通知，不 Replace 集合——Replace 语义=Remove+Insert，
            // 会重建容器并抖动 ScrollViewer，见 LyricRowVm 注释）
            if (value >= 0 && value < _lyricRows.Count)
            {
                _lyricRows[value].IsActive = true;
            }

            if (_previousActiveIndex >= 0 && _previousActiveIndex < _lyricRows.Count
                && _previousActiveIndex != value)
            {
                _lyricRows[_previousActiveIndex].IsActive = false;
            }

            _previousActiveIndex = value;

            // OM-6 沉浸风格：中央单行大字 = 当前行文本（无行时清空）
            CurrentLyricText = value >= 0 && value < _lyricRows.Count
                ? _lyricRows[value].Text
                : string.Empty;
        }
    }

    private int _previousActiveIndex = -1;

    private string _currentLyricText = string.Empty;

    /// <summary>沉浸风格中央大字：当前句文本（随 <see cref="ActiveLyricIndex"/> 同步）。</summary>
    public string CurrentLyricText
    {
        get => _currentLyricText;
        private set => SetProperty(ref _currentLyricText, value);
    }

    private string _lyricsHint = string.Empty;
    public string LyricsHint { get => _lyricsHint; private set => SetProperty(ref _lyricsHint, value); }

    // ════════ 初始化与接线 ════════

    /// <summary>View Loaded 调用一次：读曲库 + 接引擎事件。幂等。</summary>
    public async Task InitializeAsync()
    {
        MusicLibraryLoadResult loaded = await _store.LoadAsync();
        ApplyLibrary(loaded.Library);
        _scanRoots = [.. loaded.Library.ScanRoots]; // 缓存扫描根（审查采纳项：扫描时不再重复读 JSON）
        LibraryWarning = loaded.LoadWarning ?? string.Empty;
        if (loaded.IsDegraded)
        {
            _log.Warn($"[Music] 曲库降级加载：{loaded.LoadWarning}");
        }

        WireEngineOnce();

        // OM-5：在线目录随启动预热（目录服务缺席时跳过，不给本地用户制造噪音）
        if (_catalog is not null)
        {
            _ = LoadPlaylistsAsync();
            _ = LoadRecommendationsAsync();
            _ = RefreshLoginStateAsync();
        }
    }

    private bool _bulkLoadingSongs;
    private readonly Predicate<object>? _songsFilter;

    private void ApplyLibrary(MusicLibrary library)
    {
        // 审查 R4：万级曲库逐条 Add 会触发等量 CollectionChanged（计数 INPC + 绑定重渲染）
        // + 过滤谓词，末尾再 Refresh 又全量过滤一遍 → 多秒冻结。批量载入期间关闸 + 摘过滤，结束后单次刷新
        _bulkLoadingSongs = true;
        try
        {
            if (SongsView is not null)
            {
                SongsView.Filter = null;
            }

            Songs.Clear();
            foreach (MusicSong song in library.Songs)
            {
                Songs.Add(song);
            }
        }
        finally
        {
            _bulkLoadingSongs = false;
            if (SongsView is not null && _songsFilter is not null)
            {
                SongsView.Filter = _songsFilter;
            }
        }

        SongsView?.Refresh();
        LibraryCountText = $"曲库 {Songs.Count} 首";
    }

    private void WireEngineOnce()
    {
        // 原子抢占：只有把 0 换成 1 的那个调用方继续执行接线（其余直接返回）
        if (Interlocked.CompareExchange(ref _engineWired, 1, 0) != 0)
        {
            return;
        }

        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null)
        {
            // 引擎未注册：归还抢占标记，保持可重试（InitializeAsync / 播放 / 扫描入口都会再尝试接线）
            Interlocked.Exchange(ref _engineWired, 0);
            return;
        }

        OnPropertyChanged(nameof(IsEngineReady));
        // 🔴 全部经 RunOnUi 编组：引擎事件可能在 NAudio 回调/Timer 线程触发，
        // 处理器会改 ObservableCollection（LyricRows/UpNext）与触发绑定刷新
        engine.StateChanged += (state, song) => RunOnUi(() => OnEngineStateChanged(state, song));
        engine.PositionChanged += (position, duration) => RunOnUi(() => OnEnginePositionChanged(position, duration));
        engine.TrackEnded += song => RunOnUi(() => OnTrackEnded(song));
        engine.PlaybackFailed += msg => RunOnUi(() =>
        {
            // 🔴 播放失败显式可见，不静默跳曲
            ScanStatusText = $"播放失败：{msg}";
            _log.Warn($"[Music] 播放失败：{msg}");

            // 在线曲目：引擎侧失败（断流/格式不支持）并入统一跳过策略（OM-4）；
            // 本地曲目维持原语义——只提示不自动跳（用户文件可修复）
            if (QueueCurrent?.IsOnline == true)
            {
                _ = AutoSkipOrStopOnlineAsync(ResolveEngine(), msg);
            }
        });
        _log.Info("[Music] 播放引擎已接通");
    }

    private void OnEngineStateChanged(PlayState state, MusicSong? song)
    {
        IsPlaying = state == PlayState.Playing;
        if (state == PlayState.Playing && song is not null)
        {
            CurrentTitle = song.Name;
            CurrentSub = string.IsNullOrEmpty(song.Album) ? song.Artist : $"{song.Artist} — {song.Album}";
            _consecutiveOnlineFailures = 0; // 真正起播 = 连续失败链归零（OnlineSkipPolicy 语义）
            _queue.ReportPlaybackStarted(song);
            _ = LoadLyricsAsync(song); // IO 在后台；结果经 RunOnUi 回 UI
            _ = LoadCoverAsync(song); // OM-6：封面 + 主色管线（同样后台 IO + RunOnUi 回写）
        }
    }

    private void OnTrackEnded(MusicSong song)
    {
        // 自然播完 → 按模式自动切曲（对照 MUSIC-4 任务板的 TrackEnded 语义；主动 Stop 不会触发）
        _ = NextAsync();
    }

    /// <summary>
    /// 拖动中标记（审查 F-8 加固，2026-09-09）：写端是 Slider 事件、读端是引擎位置回调
    /// （经 RunOnUi 编组后仍可能与写端不同线程），volatile 保证跨线程读可见性。
    /// </summary>
    private volatile bool _seeking;

    /// <summary>拖动开始：引擎位置刷新期间暂停回写 Slider（避免拖动与轮询打架）。</summary>
    public void BeginSeek() => _seeking = true;

    /// <summary>拖动结束：按百分比 Seek 到目标位置并恢复位置回写。</summary>
    public void EndSeek(double percent)
    {
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null)
        {
            ScanStatusText = "播放引擎未就绪，无法定位进度";
            _seeking = false;
            return;
        }

        if (engine.Duration <= TimeSpan.Zero)
        {
            // 🔴 时长未知：显式提示，禁止「拖了没反应」的静默（审查 🔴-1 采纳）
            ScanStatusText = "当前曲目尚未开始播放或时长未知，无法定位进度";
            _seeking = false;
            return;
        }

        var target = TimeSpan.FromMilliseconds(
            engine.Duration.TotalMilliseconds * Math.Clamp(percent, 0, 100) / 100);

        // 🔴 AsyncRelayCommand 会吞异常——Seek 就地显式化（审查 🟠-4 采纳）
        try
        {
            engine.Seek(target);
            // 立即本地回显，不等下一次位置轮询（审查 🟠-2 采纳：消除拖动结束的视觉延迟）
            PositionCurrentText = FormatTime(target);
            PositionDurationText = FormatTime(engine.Duration);
            ProgressValue = Math.Clamp(percent, 0, 100);
        }
        catch (Exception ex)
        {
            ScanStatusText = $"进度定位失败：{ex.Message}";
            _log.Warn($"[Music] 进度定位失败：{ex.Message}");
        }

        _seeking = false;
    }

    private void OnEnginePositionChanged(TimeSpan position, TimeSpan duration)
    {
        if (_seeking)
        {
            return; // 拖动中：不回写进度，避免 Slider 与引擎轮询互相拉扯
        }

        // 🔴 切歌防瞬移闸（对照 NexBox checkActive 的 waitingForNewSong 处理，2026-09-09 实测瞬移根修）：
        // 歌词重载后，若引擎 position 仍是旧歌的大值（>2s 且没有明显回落），
        // 用它对新歌词算行号会得到巨行号 → 歌词列表狂奔瞬移。冻结推进直到时间回落。
        if (_waitingForNewSong)
        {
            double t = position.TotalSeconds;
            if (Math.Abs(t - _lastPosBeforeReload) < 0.5)
            {
                _waitingForNewSong = false; // 同一首重新加载（位置几乎没变）：直接恢复推进
            }
            else if (t > 2.0 && t >= _lastPosBeforeReload - 1)
            {
                return; // 仍在旧歌位置：本轮跳过
            }
            else
            {
                _waitingForNewSong = false; // 新歌已从 0 附近开始：恢复推进
            }
        }

        PositionCurrentText = FormatTime(position);
        PositionDurationText = FormatTime(duration);
        ProgressValue = duration.TotalMilliseconds <= 0
            ? 0
            : Math.Min(100, position.TotalMilliseconds / duration.TotalMilliseconds * 100);

        // 歌词同步：按引擎位置求当前行（MUSIC-3 的 CalcActiveIndex）
        if (_lyricLineSource.Count > 0)
        {
            ActiveLyricIndex = LyricParser.CalcActiveIndex(_lyricLineSource, position.TotalSeconds);

            // 卡拉OK行内进度（反馈 4）：行 Time→Time+Duration 线性推进
            if (ActiveLyricIndex >= 0 && ActiveLyricIndex < _lyricLineSource.Count)
            {
                LyricLine line = _lyricLineSource[ActiveLyricIndex];
                LyricLine? next = ActiveLyricIndex + 1 < _lyricLineSource.Count
                    ? _lyricLineSource[ActiveLyricIndex + 1]
                    : null;
                // 逐字感知进度（有 Words=字符级精确；无=行级 smoothstep）——卡拉OK填充共用
                LyricProgress = Math.Clamp(
                    LyricParser.GetLineProgress(line, next, position.TotalSeconds), 0, 1);
            }
            else
            {
                LyricProgress = 0;
            }
        }
        else if (LyricProgress != 0)
        {
            LyricProgress = 0;
        }
    }

    private async Task PlayCurrentCoreAsync(IMusicPlaybackEngine engine)
    {
        MusicSong? song = _queue.Current;
        if (song is null)
        {
            return;
        }

        long seq = ++_playSeq; // 竞态序号：本次请求的身份证
        if (song.IsOnline)
        {
            await PlayOnlineCoreAsync(engine, song, seq);
            return;
        }

        try
        {
            await engine.PlayAsync(song.LocalPath, song);
            if (seq != _playSeq)
            {
                return; // 播放期间用户已切走：放弃回写（慢 IO 不得覆盖新状态）
            }

            QueueCurrent = song;
        }
        catch (Exception ex)
        {
            ScanStatusText = $"播放失败：{song.Name}（{ex.Message}）";
            _log.Warn($"[Music] 播放失败：{song.Name}（{ex.Message}）");
        }
    }

    /// <summary>
    /// 在线曲目播放核心（OM-4）：解析直链 → 代理包装 → 引擎播放；
    /// 不可播 → 原因分类 + 自动跳过（连续上限见 <see cref="OnlineSkipPolicy"/>）。
    /// </summary>
    private async Task PlayOnlineCoreAsync(IMusicPlaybackEngine engine, MusicSong song, long seq)
    {
        if (_urlResolver is null || _audioProxy is null)
        {
            // 在线组件缺席：显式可见（不静默），不影响本地播放
            ScanStatusText = "在线播放组件未就绪（urlResolver/audioProxy 未注入）";
            _log.Warn("[Music] 在线播放组件未注入，无法播放在线曲目");
            return;
        }

        OnlineSongUrlResult result = await _urlResolver.ResolveAsync(song.Online!, PreferredQuality);
        if (seq != _playSeq)
        {
            return; // 解析期间用户已切走：过期请求放弃（playSongSeq 竞态防护核心点）
        }

        if (!result.Playable)
        {
            HandleOnlineUnplayable(song, result);
            return;
        }

        if (string.IsNullOrEmpty(result.Url))
        {
            // 结果标记可播但无 URL：按不可播处理（保守路径，跳过策略统一出口）
            HandleOnlineUnplayable(song, new OnlineSongUrlResult
            {
                Playable = false,
                Reason = "error",
                Message = "播放地址为空",
            });
            return;
        }

        string playUrl = await _audioProxy.GetProxiedAudioUrlAsync(result.Url);
        if (seq != _playSeq)
        {
            return; // 代理包装期间同样可能过期
        }

        try
        {
            await engine.PlayAsync(playUrl, song);
            if (seq != _playSeq)
            {
                return;
            }

            QueueCurrent = song;
            if (result.Trial)
            {
                // 试听片段可播但非完整版：显式告知（trial/reason 语义透传 UI，🔴 不静默）
                ScanStatusText = $"试听片段：{song.Name}（完整版需要 VIP/登录）";
            }
        }
        catch (Exception ex)
        {
            // 引擎侧打开失败（格式不支持/网络断流）也走统一跳过策略
            ScanStatusText = $"在线播放失败：{song.Name}（{ex.Message}）";
            _log.Warn($"[Music] 在线播放失败：{song.Name}（{ex.Message}）");
            await AutoSkipOrStopOnlineAsync(engine, $"播放异常：{ex.Message}");
        }
    }

    /// <summary>解析不可播：原因分类 → 用户可见 → 自动跳过（计连续次数，达上限停止）。</summary>
    private void HandleOnlineUnplayable(MusicSong song, OnlineSongUrlResult result)
    {
        string reason = OnlineSkipPolicy.Describe(result);
        ScanStatusText = $"跳过 {song.Name}：{reason}";
        _log.Warn($"[Music] 在线曲目不可播：{song.Name}（{reason}）");
        _ = AutoSkipOrStopOnlineAsync(ResolveEngine(), reason);
    }

    /// <summary>连续失败计数 + 自动切下一首；达上限停止并给出汇总。engine 可为 null（保守直退）。</summary>
    private async Task AutoSkipOrStopOnlineAsync(IMusicPlaybackEngine? engine, string reason)
    {
        _consecutiveOnlineFailures++;
        if (OnlineSkipPolicy.ShouldStop(_consecutiveOnlineFailures, out string? stopMessage))
        {
            // 🔴 达上限：停止自动切曲（防死循环），汇总原因显式可见
            ScanStatusText = stopMessage!;
            _log.Warn($"[Music] {stopMessage}");
            return;
        }

        if (engine is null)
        {
            return;
        }

        await NextAsync(); // PickNext 内部推进队列 → PlayCurrentCoreAsync 递增 seq，天然衔接
    }

    /// <summary>
    /// 在线搜索结果起播入口（OM-5 搜索 UI 将调用）：在线曲目包装为队列曲 + 建队列起播。
    /// </summary>
    /// <param name="tracks">本次要入队的在线曲目（如搜索结果页/歌单详情）。</param>
    /// <param name="startAt">起播曲目（null = 首曲）。</param>
    public async Task PlayOnlineTracksAsync(IReadOnlyList<OnlineTrack> tracks, OnlineTrack? startAt = null)
    {
        WireEngineOnce(); // 引擎热接通自愈（同 PlayFromLibrary）
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null)
        {
            ScanStatusText = "播放引擎未就绪，无法播放在线曲目";
            return;
        }

        if (tracks.Count == 0)
        {
            ScanStatusText = "没有可播放的在线曲目";
            return;
        }

        var songs = tracks.Select(ToQueueSong).ToList();
        MusicSong? start = startAt is null ? null : songs.FirstOrDefault(s => s.Online!.Id == startAt.Id);
        _queue.SetQueue(songs, start);
        await PlayCurrentCoreAsync(engine);
    }

    /// <summary>OnlineTrack → 队列曲（混合队列统一容器）。代理 URL 由播放核心在解析后生成。</summary>
    private static MusicSong ToQueueSong(OnlineTrack t) => new()
    {
        Id = $"{t.Provider}:{t.Id}",
        LocalPath = string.Empty, // 在线曲无本地路径；真实音源 URL 由 PlayOnlineCoreAsync 解析后经代理生成
        Name = t.Name,
        Artist = t.Artist,
        Album = t.Album,
        DurationMs = t.DurationMs,
        Online = t,
    };

    private void OnQueueCurrentChanged()
    {
        QueueCurrent = _queue.Current;
        if (_queue.Current is not null)
        {
            CurrentTitle = _queue.Current.Name;
            CurrentSub = string.IsNullOrEmpty(_queue.Current.Album)
                ? _queue.Current.Artist
                : $"{_queue.Current.Artist} — {_queue.Current.Album}";
            // 现代模板三行排版（对照 NexBox）：艺人 / 专辑分行
            CurrentArtistText = _queue.Current.Artist;
            CurrentAlbumText = _queue.Current.Album;
        }
    }

    // ════════ 完整播放器（点击底部播放条展开，2026-09-08 用户拍板布局） ════════

    [ObservableProperty]
    private bool _isFullPlayerOpen;

    [RelayCommand]
    private void OpenFullPlayer() => IsFullPlayerOpen = true;

    [RelayCommand]
    private void CloseFullPlayer() => IsFullPlayerOpen = false;

    /// <summary>View 层上报初始化失败（状态行显示，🔴 不静默）。</summary>
    public void ReportInitError(string message)
    {
        ScanStatusText = message;
    }

    /// <summary>队列快照诊断（反馈2"二次点击变空"定位用；写入模块日志）。</summary>
    public void LogQueueSnapshot(string where)
        => _log.Info($"[Music] 队列快照({where}): UpNext={UpNext.Count}, Queue={_queue.Queue.Count}, Current={QueueCurrent?.Name ?? "无"}");

    private void RebuildUpNext()
    {
        UpNext.Clear();
        foreach (MusicSong song in _queue.Queue)
        {
            UpNext.Add(song);
        }
    }

    /// <summary>
    /// 歌词加载（IO 在后台线程：.lrc 同名文件优先、内嵌兜底），
    /// 结果经 <see cref="RunOnUi"/> 回 UI 更新集合（🔴 集合修改必须在 UI 线程）。
    /// </summary>
    private async Task LoadLyricsAsync(MusicSong song)
    {
        LyricDocument doc;
        try
        {
            if (song.IsOnline)
            {
                // OM-5：在线曲歌词走目录服务（网易含翻译；QQ 走 songMid）
                if (_catalog is null)
                {
                    doc = LyricDocument.None();
                }
                else
                {
                    string key = song.Online!.Provider == OnlineProvider.QQMusic
                        ? (song.Online.Mid ?? song.Online.Id)
                        : song.Online.Id;
                    OnlineLyrics online = await _catalog.GetLyricsAsync(song.Online.Provider, key);
                    // 逐字卡拉OK（2026-09-09）：有 YRC/QRC 走逐字解析，否则行级 LRC
                    doc = !string.IsNullOrWhiteSpace(online.Yrc)
                        ? LyricParser.ParseYrc(online.Yrc, online.Translation)
                        : LyricParser.Parse(
                            string.IsNullOrWhiteSpace(online.Lyric) ? null : online.Lyric,
                            online.Translation,
                            source: LyricSource.Online);
                }
            }
            else if (File.Exists(Path.ChangeExtension(song.LocalPath, ".lrc")))
            {
                string text = await File.ReadAllTextAsync(Path.ChangeExtension(song.LocalPath, ".lrc"));
                doc = LyricParser.Parse(text, source: LyricSource.SidecarFile);
            }
            else
            {
                // TagLib 打开文件解析帧是同步 IO——推线程池，避免 UI 线程卡顿（审查 🟠-2 采纳）
                MusicTagReadResult? tag = _tagReader is null
                    ? null
                    : await Task.Run(() => _tagReader.Read(song.LocalPath));
                string? embedded = tag is { Success: true } ? tag.Tags?.Lyrics : null;
                doc = LyricParser.Parse(embedded, source: LyricSource.Embedded);
            }
        }
        catch (Exception ex)
        {
            // 歌词读取失败不影响播放，但要可见（🔴 不静默）
            _log.Warn($"[Music] 歌词加载失败：{song.Name}（{ex.Message}）");
            doc = LyricDocument.None();
        }

        // 🔴 快速切歌竞态防护：A 曲的慢 IO 若晚于 B 曲完成，不得覆盖 B 的歌词
        if (!ReferenceEquals(song, _queue.Current))
        {
            return;
        }

        RunOnUi(() => ApplyLyrics(doc));
    }

    private void ApplyLyrics(LyricDocument doc)
    {
        _lyricLineSource = doc.Lines;
        _lyricRows.Clear();
        foreach (LyricLine line in doc.Lines)
        {
            _lyricRows.Add(new LyricRowVm(line.Text, false));
        }

        PlainLyrics = doc.PlainText ?? string.Empty;
        HasLyrics = LyricRows.Count > 0 || !string.IsNullOrEmpty(PlainLyrics);
        ActiveLyricIndex = -1;
        _previousActiveIndex = -1;
        LyricsHint = HasLyrics
            ? string.Empty
            : "未找到歌词（同名 .lrc 与内嵌歌词均无）";

        // 🔴 切歌防瞬移闸（对照 NexBox KaraokeLyricsView.waitingForNewSongRef）：
        // 新歌词就位时引擎 position 往往还停在旧歌的大值，用它算新歌词会得到巨行号 →
        // 平滑滚动狂奔（用户实测"歌词整段上下瞬移"）。冻结推进，等 position 回落再恢复。
        _waitingForNewSong = true;
        _lastPosBeforeReload = ResolveEngine()?.Position.TotalSeconds ?? 0;
        LyricsVersion++; // 通知 View：列表滚回顶部 + 取消进行中的滚动动画
        DumpLyricRows(); // 🔍 空白块排查：行级内容 dump（索引:长度'文本前缀'），对照截图空白位置的数据形态
    }

    /// <summary>歌词行内容一次性 dump（空白块排查用：确认空白位置对应的行是否存在/是否不可见文本）。</summary>
    private void DumpLyricRows()
    {
        if (_lyricRows.Count == 0)
        {
            return;
        }

        var dump = new System.Text.StringBuilder("[LyricDump] ");
        for (int i = 0; i < _lyricRows.Count; i++)
        {
            string text = _lyricRows[i].Text;
            dump.Append(i).Append(':').Append(text.Length)
                .Append('\'').Append(text.Length == 0 ? "" : text[..Math.Min(8, text.Length)])
                .Append("' ");
        }

        _log.Info(dump.ToString());
    }

    private bool _waitingForNewSong;
    private double _lastPosBeforeReload;

    /// <summary>歌词重载代数（每次 ApplyLyrics 自增；View 监听后重置滚动位置）。</summary>
    [ObservableProperty]
    private int _lyricsVersion;

    private bool MatchesFilter(MusicSong s)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        return s.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
               || s.Artist.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
               || s.Album.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss")
            : t.ToString(@"m\:ss");
}
