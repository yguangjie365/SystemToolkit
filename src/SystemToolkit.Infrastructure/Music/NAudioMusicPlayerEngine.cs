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
            _output.Volume = _muted ? 0f : 0f; // 起步 0 音量，淡入补到目标，避免首帧硬响
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
            TrackEnded?.Invoke(finished);
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
        _positionTimer = new System.Threading.Timer(_ =>
        {
            if (_reader is not null && _state == PlayState.Playing)
            {
                PositionChanged?.Invoke(Position, Duration);
            }
        }, null, 0, 500);
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
