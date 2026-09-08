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
    private CancellationTokenSource? _scanCts;

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
                new Progress<MusicScanProgress>(p => ScanStatusText = $"扫描中… {p.Percent}%（{p.Scanned}/{p.Total}）"),
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

    private bool _engineWired;

    private string _currentTitle = "未在播放";
    public string CurrentTitle { get => _currentTitle; private set => SetProperty(ref _currentTitle, value); }

    private string _currentSub = string.Empty;
    public string CurrentSub { get => _currentSub; private set => SetProperty(ref _currentSub, value); }

    private string _positionCurrentText = "--:--";
    /// <summary>当前播放时间（分列显示——完整播放器与主屏底栏统一用分列）。</summary>
    public string PositionCurrentText { get => _positionCurrentText; private set => SetProperty(ref _positionCurrentText, value); }

    private string _positionDurationText = "--:--";
    /// <summary>总时长文本。</summary>
    public string PositionDurationText { get => _positionDurationText; private set => SetProperty(ref _positionDurationText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    private string _modeText = "列表循环";
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

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
            return; // 引擎未注册：保持可重试（InitializeAsync / 播放 / 扫描入口都会再尝试接线）
        }

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

        PositionCurrentText = FormatTime(position);
        PositionDurationText = FormatTime(duration);
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
