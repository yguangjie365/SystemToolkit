using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;
using NAudio.Wave;

namespace SystemToolkit.Infrastructure.Music;

/// <summary>
/// NAudio 播放引擎（MUSIC-4，从旧工程 <c>MusicPlayerEngine</c> 350 行摘取重写）。
/// 支持本地 MP3/FLAC/WAV/AAC 等 Media Foundation 可解码格式。
/// </summary>
/// <remarks>
/// <para><b>相对旧引擎的裁剪与收敛</b>（对照契约 <see cref="IMusicPlaybackEngine"/> 注释）：
/// 删 10 段均衡器与 FFT 频谱（2026-09-07 用户裁定不在 FR-01~03 范围）、
/// 删在线流播放（只收本地文件路径）、删 5 个 public 淡入淡出方法
/// （收敛为 <see cref="PlayAsync"/> 内部的「淡出旧曲→建链→淡入新曲」与
/// <see cref="Volume"/> setter 的平滑过渡）。</para>
/// <para>🔴 <b>TrackEnded 语义</b>：NAudio 的 <c>PlaybackStopped</c> 对「自然播完」与
/// 「主动 Stop/切歌/失败」一视同仁（旧引擎照搬导致切歌自动再跳一首）。
/// 本实现用 <c>_stopRequested</c> 标志位分流：只有非请求发起的停止
/// （到达流末尾）才触发 <see cref="TrackEnded"/>。</para>
/// </remarks>
public sealed class NAudioMusicPlayerEngine : IMusicPlaybackEngine, IDisposable, IAsyncDisposable
{
    private readonly ILogger _logger;
    private WaveStream? _reader;
    private WaveOutEvent? _output;
    private CancellationTokenSource? _fadeCts;
    private System.Threading.Timer? _positionTimer;

    // 🔴 停止是否由请求发起：Stop() / 切歌 / 播放失败置 true，PlaybackStopped 时据此分流 TrackEnded
    private bool _stopRequested;

    private PlayState _state = PlayState.Stopped;
    private MusicSong? _currentSong;
    private float _volume = 1.0f;
    private float _prevVolume = 1.0f;
    private bool _muted;


    /// <inheritdoc />
    public PlayState State => _state;

    /// <inheritdoc />
    public MusicSong? CurrentSong => _currentSong;

    /// <inheritdoc />
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    /// <inheritdoc />
    public float Volume
    {
        get => _volume;
        set => SmoothVolumeTo(Math.Clamp(value, 0f, 1f));
    }

    /// <inheritdoc />
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_output is not null)
            {
                _output.Volume = _muted ? 0f : _volume;
            }
        }
    }

    /// <inheritdoc />
    public event Action<PlayState, MusicSong?>? StateChanged;

    /// <inheritdoc />
    public event Action<TimeSpan, TimeSpan>? PositionChanged;

    /// <inheritdoc />
    public event Action<MusicSong>? TrackEnded;

    /// <inheritdoc />
    public event Action<string>? PlaybackFailed;

    public NAudioMusicPlayerEngine(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PlayAsync(string filePath, MusicSong song, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 切歌 = 请求发起的停止（旧曲淡出 → 释放 → 建新链），不得触发 TrackEnded
        _stopRequested = true;
        await FadeOutAndStopCoreAsync(durationMs: 200, ct);

        try
        {
            ct.ThrowIfCancellationRequested();
            _reader = new MediaFoundationReader(filePath);
            _output = new WaveOutEvent();
            _output.Volume = 0f; // 起步 0 音量，随后淡入补到目标，避免首帧硬响（静音态下淡入到 0）
            _output.Init(_reader);
            _output.PlaybackStopped += OnPlaybackStopped;
            _stopRequested = false; // 新链建立完成，恢复自然结束检测
            _currentSong = song;
            _output.Play();
            SetState(PlayState.Playing, song);
            StartPositionTimer();
            await FadeInAsync(_muted ? 0f : _volume, durationMs: 250, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 🔴 不静默：走 PlaybackFailed 事件把原因带给 UI（契约要求）
            _stopRequested = true; // 失败收尾不触发 TrackEnded
            _logger.Error($"[MusicPlayer] 播放失败：{filePath}", ex);
            StopInternal(reportStateChange: true);
            PlaybackFailed?.Invoke($"{song.Name}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_output is not null && _state == PlayState.Playing)
        {
            _output.Pause();
            SetState(PlayState.Paused, _currentSong);
            StopPositionTimer();
        }
    }

    /// <inheritdoc />
    public void Resume()
    {
        if (_output is not null && _state == PlayState.Paused)
        {
            _output.Play();
            SetState(PlayState.Playing, _currentSong);
            StartPositionTimer();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        // 主动停止：不触发 TrackEnded
        _stopRequested = true;
        StopInternal(reportStateChange: true);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        if (_reader is null)
        {
            return;
        }

        TimeSpan target = position;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (_reader.TotalTime > TimeSpan.Zero && target > _reader.TotalTime)
        {
            target = _reader.TotalTime;
        }

        _reader.CurrentTime = target;
        // 立即补发一次，避免 UI 进度条等下一个 500ms 轮询周期（契约要求）
        PositionChanged?.Invoke(Position, Duration);
    }

    /// <summary>NAudio 停止回调：区分自然播完与请求停止（🔴 见类注释）。</summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        StopPositionTimer();
        bool wasRequest = _stopRequested;
        MusicSong? finished = _currentSong;
        _stopRequested = false; // 复位，供下一次播放

        if (!wasRequest && finished is not null)
        {
            // 自然播完：置 Stopped 并触发 TrackEnded（自动切曲的唯一触发源）
            SetState(PlayState.Stopped, null);
            _currentSong = null;
            // 🔴 审查 2026-09-10（🔴-2）：PlaybackStopped 在设备拔出/解码流中断时同样触发，
            // 原因放在 e.Exception。此前只认 _stopRequested，非请求停止一律走"自然播完"
            // → 触发 TrackEnded → VM 自动切下一首：播放中断被静默包装成正常结束，
            // PlaybackFailed（契约要求"失败必须显式可见"）在整个播放期内形同虚设。
            if (e.Exception is not null)
            {
                _logger.Warn($"[MusicPlayer] 播放中断（非请求停止）：{finished.Name}（{e.Exception.Message}）");
                PlaybackFailed?.Invoke($"{finished.Name}: {e.Exception.Message}");
            }
            else
            {
                TrackEnded?.Invoke(finished);
            }

            // 🟡 审查 2026-09-10（🟡-23）**部分修复**：播完立即释放 reader（文件流句柄）。
            // 🔴 为什么不连 output 一起 Dispose：本回调由 NAudio 触发，在其中调用
            // WaveOutEvent.Stop()/Dispose() 是否与播放线程互相等待（死锁）**未能确证**——
            // 查证尝试（读 NAudio 仓库 raw 源码）三次均 404。按「不确证不动底层并发」纪律，
            // output 保持既有的「下一次 PlayAsync 经 StopInternal 回收」路径，
            // 此处只做无争议、零风险的 reader 释放。
            _reader?.Dispose();
            _reader = null;
        }
        else if (_state != PlayState.Stopped)
        {
            SetState(PlayState.Stopped, null);
            _currentSong = null;
        }
    }

    private void SetState(PlayState state, MusicSong? song)
    {
        _state = state;
        StateChanged?.Invoke(state, song);
    }

    private void StartPositionTimer()
    {
        StopPositionTimer();
        // 100ms：进度条/行高亮对 500ms 不敏感，但逐字卡拉OK填充需要 ≥10Hz 才不显格子感
        // （NexBox 前端以 RAF 60fps 直读 audio.currentTime；100ms 轮询成本极低——读 CurrentTime 属性）
        _positionTimer = new System.Threading.Timer(_ =>
        {
            // 审查 R1（2026-09-10）：Timer.Dispose 不等待在途回调，StopInternal 在 UI 线程
            // Dispose _reader 与回调读 Position 存在 TOCTOU——线程池异常不经过
            // DispatcherUnhandledException，会直接崩进程，必须就地吞掉
            try
            {
                if (_reader is not null && _state == PlayState.Playing)
                {
                    PositionChanged?.Invoke(Position, Duration);
                }
            }
            catch (Exception)
            {
                // 🟠 审查 2026-09-10（🟠-8）：原先只吞 ObjectDisposedException / InvalidOperationException，
                // 与本段自宣的"线程池异常不经过 DispatcherUnhandledException 会直接崩进程"不符——
                // Position/Duration 读的是 MediaFoundationReader.CurrentTime，设备异常可能抛
                // COMException 等其它类型。线程池回调的兜底必须覆盖全部异常；此处只读快照，
                // 100ms 高频下刻意不打日志（避免刷屏），故空块即最终形态。
            }
        }, null, 0, 100);
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    /// <summary>停止并释放音频链路（可选是否发状态变更）。</summary>
    private void StopInternal(bool reportStateChange)
    {
        StopPositionTimer();
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
        _currentSong = null;
        if (reportStateChange && _state != PlayState.Stopped)
        {
            SetState(PlayState.Stopped, null);
        }
        else if (!reportStateChange)
        {
            _state = PlayState.Stopped;
        }
    }

    /// <summary>淡出到 0 → 释放（切歌统一入口；durationMs=0 则硬停）。</summary>
    private async Task FadeOutAndStopCoreAsync(int durationMs, CancellationToken ct)
    {
        if (_output is null || (_state != PlayState.Playing && _state != PlayState.Paused))
        {
            StopInternal(reportStateChange: false);
            return;
        }

        if (durationMs > 0 && !_muted)
        {
            await FadeVolumeAsync(target: 0f, durationMs, ct);
        }

        StopInternal(reportStateChange: false);
    }

    /// <summary>起播后从 0 淡入到目标音量。</summary>
    private async Task FadeInAsync(float targetVolume, int durationMs, CancellationToken ct)
    {
        _fadeCts?.Cancel();
        _fadeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = _fadeCts.Token;
        try
        {
            int steps = Math.Max(1, durationMs / 50);
            for (int i = 1; i <= steps; i++)
            {
                token.ThrowIfCancellationRequested();
                float v = targetVolume * ((float)i / steps);
                if (_output is not null && !_muted)
                {
                    _output.Volume = v;
                }

                await Task.Delay(durationMs / steps, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消（切歌打断淡入）
        }
    }

    /// <summary>任意音量点平滑过渡到目标（Volume setter 与淡出共用）。</summary>
    private async void SmoothVolumeTo(float target)
    {
        if (target > 0)
        {
            _prevVolume = target;
        }

        _volume = target;
        WaveOutEvent? output = _output;
        if (output is null || _state != PlayState.Playing)
        {
            return; // 未播放时只记值，建链/恢复时生效
        }

        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        CancellationToken token = _fadeCts.Token;
        try
        {
            const int stepMs = 30;
            int steps = Math.Max(1, 200 / stepMs);
            float startVolume = output.Volume;
            if (Math.Abs(startVolume - target) < 0.005f)
            {
                if (!_muted)
                {
                    output.Volume = target;
                }

                return;
            }

            for (int i = 1; i <= steps; i++)
            {
                token.ThrowIfCancellationRequested();
                float v = startVolume + (target - startVolume) * ((float)i / steps);
                if (_output is not null && !_muted)
                {
                    _output.Volume = v;
                }

                await Task.Delay(stepMs, token);
            }

            if (_output is not null && !_muted)
            {
                _output.Volume = target;
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的音量调整/切歌打断：正常
        }
        catch (Exception ex)
        {
            _logger.Warn($"[MusicPlayer] 音量平滑调整异常（{ex.Message}）");
        }
    }

    /// <summary>音量渐变到目标（切歌淡出用，同步推进到 0 后返回）。</summary>
    private async Task FadeVolumeAsync(float target, int durationMs, CancellationToken ct)
    {
        _fadeCts?.Cancel();
        _fadeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = _fadeCts.Token;
        float startVolume = _output?.Volume ?? _volume;
        try
        {
            int steps = Math.Max(1, durationMs / 50);
            for (int i = steps - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                float v = startVolume * (i / (float)steps);
                if (_output is not null)
                {
                    _output.Volume = v;
                }

                await Task.Delay(durationMs / steps, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopRequested = true;
        StopInternal(reportStateChange: false);
        _fadeCts?.Dispose();
        _fadeCts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await ValueTask.CompletedTask;
    }
}
