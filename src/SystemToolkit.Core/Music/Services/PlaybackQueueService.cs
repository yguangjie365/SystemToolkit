using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 播放队列实现：纯决策（不碰引擎/网络），行为对照旧 <c>MusicService</c> 的
/// NextTrackAsync / PrevTrackAsync / AddToQueue / RemoveFromQueue / ClearQueue 摘取。
/// 偏离点（按 Id 去重、决策与播放分离、历史由上层上报）见 <see cref="IPlaybackQueueService"/> 注释与 TASKS 变更记录。
/// </summary>
public sealed class PlaybackQueueService : IPlaybackQueueService
{
    private readonly List<MusicSong> _songs = [];
    private readonly List<MusicSong> _history = [];

    private MusicSong? _current;
    private PlayMode _mode = PlayMode.List;

    /// <inheritdoc />
    public IReadOnlyList<MusicSong> Queue => _songs.AsReadOnly();

    /// <inheritdoc />
    public MusicSong? Current => _current;

    /// <inheritdoc />
    public PlayMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
        }
    }

    /// <inheritdoc />
    public event Action? QueueChanged;

    /// <inheritdoc />
    public event Action? CurrentChanged;

    /// <inheritdoc />
    public void SetQueue(IEnumerable<MusicSong> songs, MusicSong? startAt = null)
    {
        _songs.Clear();
        foreach (MusicSong song in songs)
        {
            _songs.Add(song);
        }

        // 定位：startAt 在队列中按 Id 找；找不到或为空 → 第一首（对照旧 FindIndex 失败取 0）
        _current = startAt is not null && _songs.Any(s => s.Id == startAt.Id)
            ? startAt
            : _songs.FirstOrDefault();
        QueueChanged?.Invoke();
        CurrentChanged?.Invoke();
    }

    /// <inheritdoc />
    public void AddToQueue(MusicSong song)
    {
        // 按 Id 去重（本地曲库 Id = 路径 SHA-256，稳定；重复加入只会让队列里出现相同条目）
        if (_songs.Any(s => s.Id == song.Id))
        {
            return;
        }

        _songs.Add(song);
        QueueChanged?.Invoke();
    }

    /// <inheritdoc />
    public void RemoveFromQueue(string songId)
    {
        int idx = _songs.FindIndex(s => s.Id == songId);
        if (idx < 0)
        {
            return;
        }

        MusicSong? removed = _songs[idx];
        _songs.RemoveAt(idx);
        // 当前曲目保留（正在播的引用不失效），仅当队列里已无此 Id 时把当前置空
        if (_current is not null && _current.Id == songId && !_songs.Any(s => s.Id == songId))
        {
            _current = null;
            CurrentChanged?.Invoke();
        }

        QueueChanged?.Invoke();
    }

    /// <inheritdoc />
    public void ClearQueue()
    {
        _songs.Clear();
        _history.Clear();
        _current = null;
        QueueChanged?.Invoke();
        CurrentChanged?.Invoke();
    }

    /// <inheritdoc />
    public MusicSong? PickNext()
    {
        if (_songs.Count == 0)
        {
            return null;
        }

        switch (_mode)
        {
            // 单曲循环：重复当前（不推进索引；对照旧 NextTrackAsync 的 One 分支——重播 _currentSong）
            case PlayMode.One:
                return _current ?? _songs[0];

            // 随机：多于 1 首时避开当前（对照旧 Shuffle 分支的 next>=currentIndex 则 +1 语义——
            // 等价于「随机到别处」，这里用排除法实现，行为一致且不依赖索引）
            case PlayMode.Shuffle when _songs.Count > 1 && _current is not null:
                {
                    MusicSong pick;
                    do
                    {
                        pick = _songs[Random.Shared.Next(_songs.Count)];
                    }
                    while (pick.Id == _current.Id);

                    return AdvanceTo(pick);
                }

            case PlayMode.Shuffle:
                return AdvanceTo(_songs[Random.Shared.Next(_songs.Count)]);

            // 列表循环：索引 +1，末尾回第一首
            default:
                {
                    int idx = _current is null ? -1 : _songs.FindIndex(s => s.Id == _current.Id);
                    int next = idx + 1;
                    if (next >= _songs.Count)
                    {
                        next = 0;
                    }

                    return AdvanceTo(_songs[next]);
                }
        }
    }

    /// <inheritdoc />
    public MusicSong? PickPrevious()
    {
        // 历史优先（对照旧 PrevTrackAsync 的 _playHistory[^1] pop）
        if (_history.Count > 0)
        {
            MusicSong prev = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            return AdvanceTo(prev);
        }

        if (_songs.Count == 0)
        {
            return null;
        }

        // 历史空 → 索引 -1 循环（旧版不分模式）
        int idx = _current is null ? -1 : _songs.FindIndex(s => s.Id == _current.Id);
        int prevIdx = idx - 1;
        if (prevIdx < 0)
        {
            prevIdx = _songs.Count - 1;
        }

        return AdvanceTo(_songs[prevIdx]);
    }

    /// <inheritdoc />
    public void ReportPlaybackStarted(MusicSong song)
    {
        _history.Add(song);
    }

    /// <summary>推进当前曲目并触发 CurrentChanged。历史记录由 ReportPlaybackStarted 负责（真正开播才算数）。</summary>
    private MusicSong AdvanceTo(MusicSong song)
    {
        _current = song;
        CurrentChanged?.Invoke();
        return song;
    }
}
