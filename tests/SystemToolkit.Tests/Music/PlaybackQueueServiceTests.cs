using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 播放队列测试（MUSIC-5）：三分支选曲决策（List/Shuffle/One）、历史回退、去重、索引钳制。
/// 行为对照旧 MusicService（NextTrackAsync/PrevTrackAsync 摘取），Heartbeat 分支已删。
/// </summary>
public class PlaybackQueueServiceTests
{
    private static MusicSong Song(int n)
        => new() { Id = $"id-{n}", LocalPath = $@"C:\m\{n}.mp3", Name = $"Song{n}" };

    private static (PlaybackQueueService Q, List<MusicSong> Songs) MakeQueue(int count)
    {
        var songs = Enumerable.Range(1, count).Select(Song).ToList();
        var q = new PlaybackQueueService();
        q.SetQueue(songs, songs[0]);
        return (q, songs);
    }

    [Fact]
    public void SetQueue_LocatesStartAt_AndFallsBackToFirst()
    {
        var q = new PlaybackQueueService();
        var songs = Enumerable.Range(1, 3).Select(Song).ToList();

        q.SetQueue(songs, songs[2]);
        Assert.Equal("id-3", q.Current?.Id);

        q.SetQueue(songs, Song(99)); // 不在队列中 → 回落第一首
        Assert.Equal("id-1", q.Current?.Id);
    }

    [Fact]
    public void PickNext_ListMode_CyclesToFirstAfterLast()
    {
        (PlaybackQueueService q, List<MusicSong> songs) = MakeQueue(3);
        q.Mode = PlayMode.List;

        Assert.Equal("id-2", q.PickNext()?.Id);
        Assert.Equal("id-3", q.PickNext()?.Id);
        Assert.Equal("id-1", q.PickNext()?.Id); // 末尾回第一首
    }

    [Fact]
    public void PickNext_OneMode_RepeatsCurrentWithoutAdvancing()
    {
        (PlaybackQueueService q, _) = MakeQueue(3);
        q.Mode = PlayMode.One;

        Assert.Equal("id-1", q.PickNext()?.Id);
        Assert.Equal("id-1", q.PickNext()?.Id);
        Assert.Equal("id-1", q.Current?.Id);
    }

    [Fact]
    public void PickNext_ShuffleMode_NeverPicksCurrent_WhenMultipleSongs()
    {
        (PlaybackQueueService q, List<MusicSong> songs) = MakeQueue(6);
        q.Mode = PlayMode.Shuffle;

        // 每次决策前记录当前，断言返回值 ≠ 决策前当前（对照旧 Shuffle 分支「避开自己」语义）
        for (int i = 0; i < 50; i++)
        {
            string? before = q.Current?.Id;
            MusicSong? next = q.PickNext();
            Assert.NotNull(next);
            Assert.NotEqual(before, next.Id);
        }
    }

    [Fact]
    public void PickNext_SingleSong_ShuffleStillReturnsIt()
    {
        (PlaybackQueueService q, _) = MakeQueue(1);
        q.Mode = PlayMode.Shuffle;

        Assert.Equal("id-1", q.PickNext()?.Id);
    }

    [Fact]
    public void PickNext_EmptyQueue_ReturnsNull()
    {
        var q = new PlaybackQueueService();
        Assert.Null(q.PickNext());
        Assert.Null(q.PickPrevious());
    }

    [Fact]
    public void AddToQueue_DeduplicatesById()
    {
        (PlaybackQueueService q, List<MusicSong> songs) = MakeQueue(2);

        q.AddToQueue(songs[0]); // 已在队列 → 不重复
        Assert.Equal(2, q.Queue.Count);

        q.AddToQueue(Song(9)); // 新的 → 加入
        Assert.Equal(3, q.Queue.Count);
    }

    [Fact]
    public void RemoveFromQueue_CurrentStaysUntilGoneFromQueue()
    {
        (PlaybackQueueService q, List<MusicSong> songs) = MakeQueue(3);

        q.RemoveFromQueue("id-2"); // 移除非当前
        Assert.Equal("id-1", q.Current?.Id); // 当前不受影响
        Assert.Equal(2, q.Queue.Count);

        q.RemoveFromQueue("id-1"); // 移除当前
        Assert.Null(q.Current);
    }

    [Fact]
    public void PickPrevious_PopsHistoryFirst_ThenFallsBackToIndexCycle()
    {
        (PlaybackQueueService q, List<MusicSong> songs) = MakeQueue(3);
        q.ReportPlaybackStarted(songs[1]); // 上层上报：id-2 播过

        Assert.Equal("id-2", q.PickPrevious()?.Id); // 历史优先

        // 历史空 → 索引回退（当前 id-2 → id-1）
        Assert.Equal("id-1", q.PickPrevious()?.Id);
    }

    [Fact]
    public void ClearQueue_ResetsEverything()
    {
        (PlaybackQueueService q, List<MusicSong> songs) = MakeQueue(3);
        q.ReportPlaybackStarted(songs[0]);

        int queueEvents = 0, currentEvents = 0;
        q.QueueChanged += () => queueEvents++;
        q.CurrentChanged += () => currentEvents++;

        q.ClearQueue();

        Assert.Empty(q.Queue);
        Assert.Null(q.Current);
        Assert.Null(q.PickPrevious()); // 历史也清空
        Assert.Equal(1, queueEvents);
        Assert.Equal(1, currentEvents);
    }

    [Fact]
    public void Events_FireOnChange()
    {
        var q = new PlaybackQueueService();
        int queueEvents = 0, currentEvents = 0;
        q.QueueChanged += () => queueEvents++;
        q.CurrentChanged += () => currentEvents++;

        q.SetQueue(Enumerable.Range(1, 2).Select(Song));
        Assert.Equal(1, queueEvents);
        Assert.Equal(1, currentEvents);

        q.AddToQueue(Song(9));
        Assert.Equal(2, queueEvents);
        Assert.Equal(1, currentEvents); // 加入不影响当前
    }
}
