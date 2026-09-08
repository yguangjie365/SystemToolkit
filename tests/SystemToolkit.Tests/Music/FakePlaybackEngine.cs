using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>可控假引擎：事件由测试手动触发，用于复现「引擎后台线程事件 → VM 跨线程改集合」崩溃。</summary>
public class FakePlaybackEngine : IMusicPlaybackEngine
{
    public PlayState State { get; private set; } = PlayState.Stopped;
    public MusicSong? CurrentSong { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(100);
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }

    public event Action<PlayState, MusicSong?>? StateChanged;
    public event Action<TimeSpan, TimeSpan>? PositionChanged;
    public event Action<MusicSong>? TrackEnded;
    public event Action<string>? PlaybackFailed;

    /// <summary>PlayAsync 收到的音源记录（OM-4 管线测试：断言代理 URL 与调用次数）。</summary>
    public List<string> PlayedSources { get; } = [];

    public Task PlayAsync(string filePath, MusicSong song, CancellationToken ct = default)
    {
        PlayedSources.Add(filePath);
        CurrentSong = song;
        State = PlayState.Playing;
        StateChanged?.Invoke(State, song);
        return Task.CompletedTask;
    }

    public void Pause() { }
    public void Resume() { }
    public void Stop() { }
    public void Seek(TimeSpan position) { }

    private void RaiseOnWorkerThread(Action trigger)
    {
        var thread = new Thread(() =>
        {
            try
            {
                trigger();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[diag] worker handler threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
    }

    /// <summary>模拟引擎在后台线程（Timer 回调）触发进度事件。</summary>
    public void RaisePositionFromWorkerThread(TimeSpan position)
    {
        Console.WriteLine($"[diag] RaisePosition: PositionChanged has subscriber = {PositionChanged is not null}");
        RaiseOnWorkerThread(() =>
        {
            Console.WriteLine($"[diag] worker invoking PositionChanged, subscribers={PositionChanged is not null}");
            PositionChanged?.Invoke(position, Duration);
        });
    }

    /// <summary>模拟引擎在后台线程触发「开始播放」状态变化（→ VM 会加载歌词、改集合）。</summary>
    public void RaiseStateChangedPlayingFromWorkerThread(MusicSong song)
    {
        RaiseOnWorkerThread(() => StateChanged?.Invoke(PlayState.Playing, song));
    }

    /// <summary>模拟自然播完（后台线程）。</summary>
    public void RaiseTrackEndedFromWorkerThread(MusicSong song)
    {
        RaiseOnWorkerThread(() => TrackEnded?.Invoke(song));
    }

    /// <summary>模拟播放失败（后台线程）。</summary>
    public void RaisePlaybackFailedFromWorkerThread(string message)
    {
        RaiseOnWorkerThread(() => PlaybackFailed?.Invoke(message));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
