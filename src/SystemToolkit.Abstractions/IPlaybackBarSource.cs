using System.ComponentModel;
using System.Windows.Input;

namespace SystemToolkit.Abstractions;

/// <summary>
/// Shell 底部迷你播放条的跨模块数据源契约（MUSIC-7，09 设计 §五）。
/// <para>
/// 🔴 只暴露原始类型与命令：Abstractions 零 WPF / 零 Core 引用——封面以
/// <see cref="object"/> 传递（实为模块侧构造的 WPF ImageSource，宿主直接赋给
/// <c>Image.Source</c>，全程无 cast 无类型依赖）。宿主只订阅，不感知音乐域模型。
/// </para>
/// <para>
/// 可空性即功能：Shell 用 <c>GetService&lt;IPlaybackBarSource&gt;()</c> 解析——
/// 音乐模块被禁（未注册）时返回 null，播放条整体隐藏；模块禁用瞬间的停播与
/// 设备句柄释放由禁用机制（V1-008）负责，契约不承载。
/// </para>
/// </summary>
/// <remarks>
/// 属性变更一律经 <see cref="INotifyPropertyChanged"/>（实现方为 CommunityToolkit
/// ObservableObject，隐式实现要求成员为 public 属性——显式接口实现 WPF binding 不可见）。
/// </remarks>
public interface IPlaybackBarSource : INotifyPropertyChanged
{
    /// <summary>是否已有当前曲目（无曲目时播放条不占位）。</summary>
    bool HasTrack { get; }

    /// <summary>
    /// 跳页导航目标 = 提供本源模块的 <see cref="IModule"/> 实例（宿主用引用相等定位，
    /// 🔴 不得按 Id 字符串分派——F-1 红线）。null 表示无跳转目标。
    /// </summary>
    IModule? NavigationModule { get; }

    /// <summary>曲名（未在播放时为占位文案）。</summary>
    string Title { get; }

    /// <summary>副标题（艺术家，缺省回退专辑/来源，与全屏播放器同口径）。</summary>
    string Subtitle { get; }

    /// <summary>封面（实为 ImageSource；null 时宿主显示♪占位）。</summary>
    object? Cover { get; }

    /// <summary>当前是否正在播放（决定 ⏯ 图标态）。</summary>
    bool IsPlaying { get; }

    /// <summary>进度百分比 0–100（与全屏播放器同一口径）。</summary>
    double ProgressPercent { get; }

    /// <summary>当前时间文本（mm:ss）。</summary>
    string PositionText { get; }

    /// <summary>总时长文本（mm:ss）。</summary>
    string DurationText { get; }

    /// <summary>音量 0–1（滑条双向绑定；写入即作用引擎）。</summary>
    double Volume { get; set; }

    /// <summary>播放 / 暂停。</summary>
    ICommand TogglePlayCommand { get; }

    /// <summary>下一首（命名避开实现方 IAsyncRelayCommand 同名属性——属性实现不支持协变返回）。</summary>
    ICommand NextTrackCommand { get; }

    /// <summary>上一首。</summary>
    ICommand PreviousTrackCommand { get; }

    /// <summary>按 0–1 比例定位进度（播放条顶缘细线点击/拖动）。</summary>
    /// <param name="ratio">目标位置比例，越界自动钳制。</param>
    void SeekToRatio(double ratio);
}
