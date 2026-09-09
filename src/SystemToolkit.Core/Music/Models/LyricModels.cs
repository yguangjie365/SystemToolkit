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
/// <para>结构对照旧工程 <c>KaraokeLine</c>。2026-09-09 用户裁决恢复逐字卡拉OK（对照 NexBox）：此前的"偏离说明"作废，
/// <c>LyricParser.ParseYrc</c> 与 <c>LyricParser.GetLineProgress</c> 的逐字分支按原算法
/// 恢复（网易 YRC / QQ QRC 接口均能下发）。</para>
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

    /// <summary>逐字数据（有 YRC/QRC 时非 null；卡拉OK逐字填充用）。</summary>
    public IReadOnlyList<LyricWord>? Words { get; init; }

    /// <summary>整行字符数（逐字进度按字符区间换算；恒 ≥1）。</summary>
    public int CharCount { get; init; } = 1;
}

/// <summary>逐字数据：词文本 + 起止时间 + 在整行文本中的字符区间 [C0,C1)。</summary>
public sealed record LyricWord(string Text, double T, double D, int C0, int C1);

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
