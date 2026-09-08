namespace SystemToolkit.Core.Music.Models;

/// <summary>歌词来源（歌词 Tab 顶部标注「内嵌」/「外部文件」，无歌词时走占位态）。</summary>
public enum LyricSource
{
    /// <summary>无歌词。</summary>
    None,

    /// <summary>内嵌歌词（ID3 USLT / Vorbis LYRICS / MP4 ©lyr 帧）。</summary>
    Embedded,

    /// <summary>同名外部 <c>.lrc</c> 文件。</summary>
    SidecarFile,

    /// <summary>在线歌词（OM-5：平台目录服务返回的 LRC；本地/在线同一解析管道）。</summary>
    Online,
}

/// <summary>
/// 一行歌词。
/// </summary>
/// <remarks>
/// <para>结构对照旧工程 <c>KaraokeLine</c>，但删去了三个逐字卡拉OK 专用成员
/// （<c>Words</c> / <c>HasKaraoke</c> / <c>CharCount</c>）及配套的 <c>LyricWord</c> 类型。
/// 🔴 复用纪律要求的偏离说明：这三者只由 <c>LyricParser.ParseYrc</c> 填充，
/// 而 YRC 逐字歌词<b>只有</b>网易云/QQ 音乐接口会下发，属二阶段第三方平台能力
/// （且按 AGENTS.md 红线须走独立 WebView2 进程，数据通路要重新设计）。
/// 本阶段两个歌词来源——内嵌 USLT 与外部 <c>.lrc</c>——都是逐行 LRC，
/// <c>Words</c> 恒为 null，留着就是不可达分支，还要为死代码写测试。</para>
/// <para>相应地，<c>LyricParser.GetLineProgress</c> 的逐字分支在搬移时一并去掉，
/// 只保留行级线性插值 + smoothstep 缓动那条路径（算法逐字未改）。</para>
/// </remarks>
public sealed record LyricLine
{
    /// <summary>行开始时间（秒）。</summary>
    public double Time { get; init; }

    /// <summary>行持续时间（秒，已由 <c>FinalizeLineDurations</c> 推断并 clamp 到 0.45–12）。</summary>
    public double Duration { get; init; }

    /// <summary>整行文本（已去时间标签、已合并空白）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>译文（按时间戳匹配；无译文时为 null）。</summary>
    public string? Translation { get; init; }
}

/// <summary>
/// 一首歌的歌词文档：来源 + 已解析的行 + 无时间标签时的纯文本兜底。
/// </summary>
/// <remarks>
/// <para><see cref="PlainText"/> 不是冗余字段：内嵌 USLT 帧里<b>很常见</b>的是
/// 不带 <c>[mm:ss.xx]</c> 时间标签的纯文本歌词。旧工程的 <c>ParseLrcEnhanced</c>
/// 遇到没有时间标签的文本会直接返回空列表，UI 只能显示「暂无歌词」——
/// 明明文件里有歌词却告诉用户没有，属于功能性缺失。</para>
/// <para>因此约定：<see cref="Lines"/> 非空 → 逐行滚动高亮；
/// <see cref="Lines"/> 空但 <see cref="PlainText"/> 非空 → 静态整篇展示；
/// 两者都空 → 占位态。</para>
/// </remarks>
public sealed record LyricDocument
{
    /// <summary>歌词来源。</summary>
    public LyricSource Source { get; init; } = LyricSource.None;

    /// <summary>已解析的带时间标签歌词行（按时间升序）。无时间标签时为空列表。</summary>
    public List<LyricLine> Lines { get; init; } = [];

    /// <summary>无时间标签时的原始歌词全文；有时间标签时为 null。</summary>
    public string? PlainText { get; init; }

    /// <summary>是否完全没有歌词（UI 据此走占位态）。</summary>
    public bool IsEmpty => Lines.Count == 0 && string.IsNullOrWhiteSpace(PlainText);

    /// <summary>是否是逐行滚动模式（false 时应静态整篇展示）。</summary>
    public bool IsTimed => Lines.Count > 0;

    /// <summary>无歌词的空文档。</summary>
    public static LyricDocument None() => new();
}
