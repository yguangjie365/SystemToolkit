using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 外部来源文本卫生工具。审查 O20（2026-09-10）：零宽/不可见字符此前只在 <c>LyricParser</c> 内剥离，
/// 其余外部文本入口（在线曲名/歌手、对端文件名、UDP 设备名、pnputil 设备描述、Steam 游戏名）原样入库，
/// 导致曲库搜索/去重/排序按含零宽的字符数失配、渲染出无字宽的空白。统一收口到此处供各 ingestion 点复用。
/// </summary>
public static class TextSanitizer
{
    // U+200B ZWSP / U+200C ZWNJ / U+200D ZWJ / U+2060 WordJoiner / U+FEFF BOM——Trim 与 \s 均不匹配
    private static readonly Regex Invisible = new("[\u200b\u200c\u200d\u2060\ufeff]", RegexOptions.Compiled);

    /// <summary>剥离零宽/不可见字符（不 trim、不改其余内容；null 原样返回）。用于一切不可信外部文本渲染/入库前。</summary>
    public static string? StripInvisible(string? text)
        => text is null ? null : Invisible.Replace(text, string.Empty);
}
