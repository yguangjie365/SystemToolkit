using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
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
    private IMusicPlaybackEngine? _engine;
    private CancellationTokenSource? _scanCts;
    private LyricDocument _lyrics = LyricDocument.None();

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
            action();
        }
        else if (d.CheckAccess())
        {
            action();
        }
        else
        {
            d.BeginInvoke(action);
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
        System.Windows.Threading.Dispatcher? dispatcher = null)
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

        Songs.CollectionChanged += (_, _) => LibraryCountText = $"曲库 {Songs.Count} 首";
        SongsView = CollectionViewSource.GetDefaultView(Songs);
        SongsView.Filter = o => o is MusicSong s && MatchesFilter(s);
        _queue.QueueChanged += RebuildUpNext;
        _queue.CurrentChanged += OnQueueCurrentChanged;

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
    private bool _isScanning;

    private string _scanStatusText = string.Empty;
    public string ScanStatusText { get => _scanStatusText; private set => SetProperty(ref _scanStatusText, value); }

    private string _libraryWarning = string.Empty;
    /// <summary>曲库加载降级警告（缓存损坏/剔除消失文件），空 = 正常。🔴 不静默。</summary>
    public string LibraryWarning { get => _libraryWarning; private set => SetProperty(ref _libraryWarning, value); }

    /// <summary>View Loaded 时注入的文件夹选择回调（与 FileBackup.RestoreRequest 同款注入模式）。</summary>
    public Func<string?>? PickFolder { get; set; }

    [RelayCommand]
    private async Task ScanAsync()
    {
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
                new Progress<MusicScanProgress>(p => ScanStatusText = $"扫描中… {p.Percent}%（{p.Scanned}/{p.Total}）"),
                _scanCts.Token);

            // 合并策略：新扫描结果按 Id 覆盖旧条目（保留未被本轮触及的其它根的曲目）
            var merged = new Dictionary<string, MusicSong>(Songs.ToDictionary(s => s.Id));
            foreach (MusicSong song in result.Songs)
            {
                merged[song.Id] = song;
            }

            var library = new MusicLibrary
            {
                ScanRoots = roots,
                Songs = [.. merged.Values.OrderBy(s => s.LocalPath, StringComparer.OrdinalIgnoreCase)],
                LastScanAtUtc = DateTimeOffset.UtcNow,
            };

            ApplyLibrary(library);

            await _store.SaveAsync(library);
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

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _scanCts?.Cancel();

    // ════════ 播放 ════════

    /// <summary>解析引擎（null = MUSIC-4 未合入，播放禁用）。每次操作前取，便于热接通。</summary>
    private IMusicPlaybackEngine? ResolveEngine() => _engineProvider?.Invoke();

    private bool _engineWired;

    private string _currentTitle = "未在播放";
    public string CurrentTitle { get => _currentTitle; private set => SetProperty(ref _currentTitle, value); }

    private string _currentSub = string.Empty;
    public string CurrentSub { get => _currentSub; private set => SetProperty(ref _currentSub, value); }

    private string _positionText = "--:-- / --:--";
    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    private string _modeText = "列表循环";
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    public ObservableCollection<MusicSong> UpNext { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEngineReady))]
    private MusicSong? _queueCurrent;

    public bool IsEngineReady => ResolveEngine() is not null;

    [RelayCommand]
    private async Task PlayFromLibraryAsync()
    {
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
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null)
        {
            return;
        }

        if (IsPlaying)
        {
            engine.Pause();
        }
        else if (QueueCurrent is not null)
        {
            engine.Resume();
        }
        else
        {
            await PlayFromLibraryAsync();
        }
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
    }

    [RelayCommand]
    private void RemoveFromQueue(string songId) => _queue.RemoveFromQueue(songId);

    // ════════ 歌词 ════════

    /// <summary>歌词行包装（模型 <see cref="LyricLine"/> 无 UI 状态，IsActive 由 VM 维护）。</summary>
    public sealed record LyricRowVm(string Text, bool IsActive);

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

            // 精准只更新新旧两行（审查采纳项）：避免每次跳行全量遍历
            if (value >= 0 && value < _lyricRows.Count)
            {
                _lyricRows[value] = _lyricRows[value] with { IsActive = true };
            }

            if (_previousActiveIndex >= 0 && _previousActiveIndex < _lyricRows.Count
                && _previousActiveIndex != value)
            {
                _lyricRows[_previousActiveIndex] = _lyricRows[_previousActiveIndex] with { IsActive = false };
            }

            _previousActiveIndex = value;
        }
    }

    private int _previousActiveIndex = -1;

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
    }

    private void ApplyLibrary(MusicLibrary library)
    {
        Songs.Clear();
        foreach (MusicSong song in library.Songs)
        {
            Songs.Add(song);
        }

        SongsView?.Refresh();
    }

    private void WireEngineOnce()
    {
        if (_engineWired)
        {
            return;
        }

        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is null)
        {
            return; // MUSIC-4 未合入：保持可重试，下次 InitializeAsync 再接
        }

        _engine = engine;
        _engineWired = true;
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
            _queue.ReportPlaybackStarted(song);
            _ = LoadLyricsAsync(song); // IO 在后台；结果经 RunOnUi 回 UI
        }
    }

    private void OnTrackEnded(MusicSong song)
    {
        // 自然播完 → 按模式自动切曲（对照 MUSIC-4 任务板的 TrackEnded 语义；主动 Stop 不会触发）
        _ = NextAsync();
    }

    private bool _seeking;

    /// <summary>拖动进度条开始：引擎位置刷新期间暂停回写 Slider（避免拖动与轮询打架）。</summary>
    public void BeginSeek() => _seeking = true;

    /// <summary>拖动结束：按百分比 Seek 到目标位置并恢复位置回写。</summary>
    public void EndSeek(double percent)
    {
        IMusicPlaybackEngine? engine = ResolveEngine();
        if (engine is not null && engine.Duration > TimeSpan.Zero)
        {
            engine.Seek(TimeSpan.FromMilliseconds(engine.Duration.TotalMilliseconds * Math.Clamp(percent, 0, 100) / 100));
        }

        _seeking = false;
    }

    private void OnEnginePositionChanged(TimeSpan position, TimeSpan duration)
    {
        if (_seeking)
        {
            return; // 拖动中：不回写进度，避免 Slider 与引擎轮询互相拉扯
        }

        PositionText = $"{FormatTime(position)} / {FormatTime(duration)}";
        ProgressValue = duration.TotalMilliseconds <= 0
            ? 0
            : Math.Min(100, position.TotalMilliseconds / duration.TotalMilliseconds * 100);

        // 歌词同步：按引擎位置求当前行（MUSIC-3 的 CalcActiveIndex）
        if (_lyricLineSource.Count > 0)
        {
            ActiveLyricIndex = LyricParser.CalcActiveIndex(_lyricLineSource, position.TotalSeconds);
        }
    }

    private async Task PlayCurrentCoreAsync(IMusicPlaybackEngine engine)
    {
        MusicSong? song = _queue.Current;
        if (song is null)
        {
            return;
        }

        try
        {
            await engine.PlayAsync(song.LocalPath, song);
            QueueCurrent = song;
        }
        catch (Exception ex)
        {
            ScanStatusText = $"播放失败：{song.Name}（{ex.Message}）";
            _log.Warn($"[Music] 播放失败：{song.Name}（{ex.Message}）");
        }
    }

    private void OnQueueCurrentChanged()
    {
        QueueCurrent = _queue.Current;
        if (_queue.Current is not null)
        {
            CurrentTitle = _queue.Current.Name;
            CurrentSub = string.IsNullOrEmpty(_queue.Current.Album)
                ? _queue.Current.Artist
                : $"{_queue.Current.Artist} — {_queue.Current.Album}";
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
            string lrcPath = Path.ChangeExtension(song.LocalPath, ".lrc");
            if (File.Exists(lrcPath))
            {
                string text = await File.ReadAllTextAsync(lrcPath);
                doc = LyricParser.Parse(text, source: LyricSource.SidecarFile);
            }
            else
            {
                MusicTagReadResult? tag = _tagReader?.Read(song.LocalPath);
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

        RunOnUi(() => ApplyLyrics(doc));
    }

    private void ApplyLyrics(LyricDocument doc)
    {
        _lyrics = doc;
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
    }

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
