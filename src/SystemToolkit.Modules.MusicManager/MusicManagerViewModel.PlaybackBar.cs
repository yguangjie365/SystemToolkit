using System.Windows.Input;
using SystemToolkit.Abstractions;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// MUSIC-7：为 Shell 迷你播放器提供 <see cref="IPlaybackBarSource"/> 映射
/// （隐式实现——WPF binding 要求 public 成员，显式接口实现不可见）。
/// 成员是对既有播放状态的单行别名，零新逻辑；
/// 🔴 别名表达式属性不会随底层属性自动通知——必须经 <see cref="WirePlaybackBarProjections"/>
/// 转发（2026-09-11 实机截图暴露：播放条标题/时间永远停在初始值）。
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

    /// <summary>
    /// 别名表达式属性的转发通知（构造尾调用；单例 VM 自订阅无泄漏问题）。
    /// QueueCurrent→HasTrack 也在此统一（主文件三处赋值点的手动通知保留作双保险）。
    /// </summary>
    private void WirePlaybackBarProjections() =>
        PropertyChanged += (_, e) =>
        {
            string? alias = e.PropertyName switch
            {
                nameof(CurrentTitle) => nameof(Title),
                nameof(CurrentSub) => nameof(Subtitle),
                nameof(CurrentCoverImage) => nameof(Cover),
                nameof(ProgressValue) => nameof(ProgressPercent),
                nameof(PositionCurrentText) => nameof(PositionText),
                nameof(PositionDurationText) => nameof(DurationText),
                nameof(QueueCurrent) => nameof(HasTrack),
                _ => null,
            };
            if (alias is not null)
            {
                OnPropertyChanged(alias);
            }
        };
}
