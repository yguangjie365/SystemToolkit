using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 音频播放引擎契约（Core 只声明，实现落 Infrastructure —— NAudio 的 WASAPI/WinMM 后端是 Windows-only，
/// 而 Core 必须保持 <c>net10.0</c> 跨平台且零 ProjectReference）。
/// </summary>
/// <remarks>
/// <para><b>落位对照</b>：与 <c>IFileWebServer</c>（Core 契约）↔ <c>FileWebServer</c>（Infrastructure 实现）
/// 同一套模式，模块只依赖本接口。</para>
/// <para>🔴 <b>实现类必须同时声明 <see cref="IDisposable"/> 与本接口的
/// <see cref="IAsyncDisposable"/></b>（<c>AsyncDisposableServiceGuardTests</c> 会拦）：
/// 宿主 <c>App.OnExit</c> 走的是同步 <c>ServiceProvider.Dispose()</c>，
/// 只实现 <see cref="IAsyncDisposable"/> 的单例会让 MS DI 抛
/// <c>InvalidOperationException</c>，表现为「关闭程序即崩溃」（退出码 0xE0434352），
/// 且此时 CrashLog 处理器已摘除、日志为空，极难定位。</para>
/// <para>🔴 <b>进度轮询必须留在实现内部</b>（<c>ModuleIsolationGuardTests</c> 禁止模块源码出现
/// <c>new Timer(</c> / <c>new Thread(</c> 字面量）：模块只订阅 <see cref="PositionChanged"/>。</para>
/// <para><b>不含</b>：10 段均衡器、FFT 频谱（旧 <c>MusicPlayerEngine</c> 有，但不在本轮
/// FR-MusicManager-01~03 范围，用户 2026-09-07 裁定排除）；也不含在线流播放——
/// <see cref="PlayAsync"/> 只接受本地文件路径。</para>
/// </remarks>
public interface IMusicPlaybackEngine : IAsyncDisposable
{
    /// <summary>当前播放状态。</summary>
    PlayState State { get; }

    /// <summary>当前曲目；<see cref="PlayState.Stopped"/> 时为 null。</summary>
    MusicSong? CurrentSong { get; }

    /// <summary>当前播放位置。未播放时为 <see cref="TimeSpan.Zero"/>。</summary>
    TimeSpan Position { get; }

    /// <summary>当前曲目总时长。未播放时为 <see cref="TimeSpan.Zero"/>。</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// 音量（0.0–1.0，超出范围由实现 clamp）。
    /// </summary>
    /// <remarks>
    /// 赋值时实现应<b>平滑过渡</b>到目标值而不是跳变（旧工程的 <c>FadeToAsync</c> 逻辑），
    /// 避免用户拖动音量条时产生爆音。平滑是实现细节，故契约里不暴露
    /// <c>FadeIn/FadeOut/FadeTo</c> 等方法——旧引擎那 5 个 public 淡入淡出方法
    /// 是给 UI 直接编排的，实践中只有「切歌」和「调音量」两个场景，收敛到
    /// <see cref="PlayAsync"/> 与 <see cref="Volume"/> 即可。
    /// </remarks>
    float Volume { get; set; }

    /// <summary>
    /// 静音开关。
    /// </summary>
    /// <remarks>静音期间修改 <see cref="Volume"/> 应被记住，取消静音后恢复到该值。</remarks>
    bool Muted { get; set; }

    /// <summary>
    /// 播放状态变化（含 <see cref="PlayState.Stopped"/> / <see cref="PlayState.Playing"/> / <see cref="PlayState.Paused"/> 之间的一切迁移）。
    /// </summary>
    /// <remarks>可能在非 UI 线程触发，订阅方负责编组到 Dispatcher。</remarks>
    event Action<PlayState, MusicSong?>? StateChanged;

    /// <summary>
    /// 播放进度变化（实现内部定时轮询触发，约每 500 ms 一次；<see cref="Seek"/> 后立即补发一次）。
    /// </summary>
    /// <remarks>
    /// 回调两个参数依次是当前位置与总时长。可能在非 UI 线程触发，订阅方负责编组到 Dispatcher。
    /// <see cref="PlayState.Paused"/> / <see cref="PlayState.Stopped"/> 期间不触发。
    /// </remarks>
    event Action<TimeSpan, TimeSpan>? PositionChanged;

    /// <summary>
    /// 当前曲目<b>自然播放结束</b>（到达流末尾）。自动切下一首的唯一触发源。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>语义必须严格</b>：用户主动 <see cref="Stop"/>、切歌（再次 <see cref="PlayAsync"/>）、
    /// 播放失败都<b>不得</b>触发本事件，否则会自动跳到下一首甚至递归跳完整张歌单。
    /// <para>旧引擎在这里是有缺陷的：它只订阅 NAudio 的 <c>PlaybackStopped</c>，
    /// 而该事件对「自然结束」和「主动 Stop」一视同仁，两者无法区分。
    /// 实现须自行加「本次停止是否由请求发起」的标志位来分流。</para>
    /// </remarks>
    event Action<MusicSong>? TrackEnded;

    /// <summary>
    /// 播放失败（文件被删除/移动、编解码器缺失、音频设备被独占等）。
    /// </summary>
    /// <remarks>
    /// 回调参数是可直接展示给用户的失败原因。
    /// <para>🔴 禁止静默失败：旧引擎把播放异常吞进 <c>catch</c> 只写日志，然后把状态置为
    /// <see cref="PlayState.Stopped"/>，UI 上表现为「点了播放没反应」，用户无从判断原因。
    /// 有了本事件，<see cref="PlayAsync"/> 就不需要为播放问题抛异常。</para>
    /// </remarks>
    event Action<string>? PlaybackFailed;

    /// <summary>
    /// 开始播放一个本地音频文件（已在播放时调用即切歌：实现应先平滑淡出旧曲再建链，消除硬切爆音）。
    /// </summary>
    /// <param name="filePath">音频文件绝对路径。</param>
    /// <param name="song">对应曲目元数据（用于 <see cref="CurrentSong"/> 与事件回传）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>起播完成（含淡入）的任务。</returns>
    /// <remarks>
    /// <b>不抛播放类异常</b>：文件读不了、解码失败、设备不可用一律走
    /// <see cref="PlaybackFailed"/> + <see cref="StateChanged"/>(Stopped, null)。
    /// 唯一会抛的是 <paramref name="ct"/> 被取消时的 <see cref="OperationCanceledException"/>。
    /// </remarks>
    Task PlayAsync(string filePath, MusicSong song, CancellationToken ct = default);

    /// <summary>暂停（仅 <see cref="PlayState.Playing"/> 时有效）。</summary>
    void Pause();

    /// <summary>继续播放（仅 <see cref="PlayState.Paused"/> 时有效）。</summary>
    void Resume();

    /// <summary>停止播放并释放音频链路（<b>不</b>触发 <see cref="TrackEnded"/>）。</summary>
    void Stop();

    /// <summary>
    /// 跳转到指定位置（超出 [0, <see cref="Duration"/>] 由实现 clamp）。
    /// </summary>
    /// <param name="position">目标位置。</param>
    /// <remarks>未播放时调用无效果。跳转后应立即补发一次 <see cref="PositionChanged"/>，
    /// 否则 UI 进度条要等下一个轮询周期（最长 500 ms）才跟上，拖动时手感明显滞后。</remarks>
    void Seek(TimeSpan position);
}
