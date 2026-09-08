using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 播放队列契约：只负责「队列维护 + 按模式选曲决策」，<b>不碰播放引擎与网络</b>。
/// </summary>
/// <remarks>
/// <para><b>与旧工程的刻意偏离</b>（本批 MUSIC-5，理由记 TASKS 变更记录）：
/// 旧 <c>MusicService</c> 把队列、播放引擎调用、网易云网络请求揉在同一个 1197 行类里，
/// NextTrackAsync 直接 <c>await PlayAsync(...)</c>。本仓按分层拆开：队列只返回
/// 「该播哪首」（<see cref="PickNext"/> / <see cref="PickPrevious"/>），
/// 由上层（模块 VM，MUSIC-6）拿到决策后调用 <see cref="IMusicPlaybackEngine"/>。
/// 好处：Core 零引擎依赖、决策可单测、UI 可在切歌前插入自己的确认/日志逻辑。</para>
/// <para><b>播放历史</b>：队列不知道「是否真的播了」，由上层在引擎确认开播后调
/// <see cref="ReportPlaybackStarted"/> 上报；「上一曲」优先从该历史回退（对照旧
/// <c>PrevTrackAsync</c> 的 <c>_playHistory</c> 行为），历史空则按队列索引回退。</para>
/// </remarks>
public interface IPlaybackQueueService
{
    /// <summary>当前队列快照（只读；变更时触发 <see cref="QueueChanged"/>）。</summary>
    IReadOnlyList<MusicSong> Queue { get; }

    /// <summary>当前曲目（尚未播放也算——SetQueue 指定 startAt 即设定）；无当前时为 null。</summary>
    MusicSong? Current { get; }

    /// <summary>播放模式（List/Shuffle/One；Heartbeat 模式已按 MUSIC-1 裁定删除）。</summary>
    PlayMode Mode { get; set; }

    /// <summary>队列内容变化（加入/移除/清空/整组替换）。</summary>
    event Action? QueueChanged;

    /// <summary>当前曲目变化（切歌决策推进、整组替换定位）。</summary>
    event Action? CurrentChanged;

    /// <summary>
    /// 整组替换队列并定位当前曲目（对照旧 PlayAsync 里的「重建队列 + FindIndex 定位」段）。
    /// <paramref name="startAt"/> 为 null 或不在 songs 中时定位到第一首；songs 为空时清空队列。
    /// </summary>
    void SetQueue(IEnumerable<MusicSong> songs, MusicSong? startAt = null);

    /// <summary>加入队列末尾。按 <see cref="MusicSong.Id"/> 去重——已存在时不重复加入（本地曲库 Id 稳定，重复加入只会造成困惑；与旧版 AddToQueue 不去重的偏离记 TASKS）。</summary>
    void AddToQueue(MusicSong song);

    /// <summary>按 Id 从队列移除；移除的是当前曲目时当前曲目保留（正在播的引用不失效，对照旧版）。触发 QueueChanged。</summary>
    void RemoveFromQueue(string songId);

    /// <summary>清空队列与播放历史。触发 QueueChanged + CurrentChanged。</summary>
    void ClearQueue();

    /// <summary>
    /// 下一曲决策（并推进当前曲目）：One → 重复当前（不推进）；Shuffle → 随机且队列多于 1 首时避开当前；
    /// List → 索引 +1 循环。队列空返回 null（对照旧 NextTrackAsync 的三分支，删 Heartbeat）。
    /// </summary>
    MusicSong? PickNext();

    /// <summary>
    /// 上一曲决策（并推进当前曲目）：播放历史非空时回退最近一首（pop，对照旧 _playHistory 行为）；
    /// 历史空则索引 -1 循环（旧版不分模式）。队列空且历史空返回 null。
    /// </summary>
    MusicSong? PickPrevious();

    /// <summary>上层在引擎确认开播后上报；写入播放历史供 PickPrevious 回退。上报的曲目不在队列中也允许（如已从队列移除但仍在播）。</summary>
    void ReportPlaybackStarted(MusicSong song);
}
