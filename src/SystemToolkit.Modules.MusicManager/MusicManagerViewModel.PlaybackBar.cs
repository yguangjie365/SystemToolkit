using System.Windows.Input;
using SystemToolkit.Abstractions;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// MUSIC-7：为 Shell 底部迷你播放条提供 <see cref="IPlaybackBarSource"/> 映射
/// （隐式实现——WPF binding 要求 public 成员，显式接口实现不可见）。
/// 全部成员是对既有播放状态的单行别名，零新逻辑；变更通知由底层 SetProperty 承担。
/// </summary>
public partial class MusicManagerViewModel : IPlaybackBarSource
{
    /// <summary>是否已有当前曲目（播放条显隐闸）。</summary>
    public bool HasTrack => QueueCurrent is not null;

    /// <inheritdoc/>
    public string Title => CurrentTitle;

    /// <inheritdoc/>
    public string Subtitle => CurrentSub;

    /// <inheritdoc/>
    public object? Cover => CurrentCoverImage;

    /// <inheritdoc/>
    public double ProgressPercent => ProgressValue;

    /// <inheritdoc/>
    public string PositionText => PositionCurrentText;

    /// <inheritdoc/>
    public string DurationText => PositionDurationText;

    /// <inheritdoc/>
    public ICommand TogglePlayCommand => PlayPauseCommand;

    /// <inheritdoc/>
    public ICommand NextTrackCommand => NextCommand;

    /// <inheritdoc/>
    public ICommand PreviousTrackCommand => PreviousCommand;

    // IsPlaying / Volume 由 VM 既有同名 public 属性直接隐式满足接口，无需重复。

    /// <inheritdoc/>
    public IModule? NavigationModule { get; }

    /// <inheritdoc/>
    public void SeekToRatio(double ratio) => EndSeek(Math.Clamp(ratio, 0, 1) * 100);

    // HasTrack 的变更通知挂在 QueueCurrent 赋值点（Main 文件 SetNowPlaying 区），
    // 不用 OnQueueCurrentChanged partial hook——WPF 临时编译通道不跑源生成器（CS0759 实测）。
}
