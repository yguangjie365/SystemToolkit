using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UiEditor.Core;

/// <summary>
/// 令牌纪律预检：写盘前扫描目标文本，命中"裸值"即拒绝落盘。M1 保守只拦两条最硬的红线——
/// 硬编码色值（<c>#RGB/#RRGGBB/#AARRGGBB</c>）与硬编码字号（<c>FontSize="数字"</c>）。
/// 间距/宽高裸值在本项目存量广泛（受 <c>UiTokenRatchet</c> 基线只减不增约束），M1 不误伤，留后续。
/// </summary>
public static class BareValueChecker
{
    private static readonly Regex HexColor = new(
        @"(?<![0-9A-Za-z])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})\b", RegexOptions.Compiled);
    private static readonly Regex NumericFontSize = new(@"\bFontSize=""\d+(\.\d+)?""", RegexOptions.Compiled);

    public static IReadOnlyList<string> Scan(string text)
    {
        var violations = new List<string>();
        foreach (Match m in HexColor.Matches(text))
        {
            violations.Add($"硬编码色值 {m.Value}（应改 Brush_*/Color_* 令牌）");
        }

        foreach (Match m in NumericFontSize.Matches(text))
        {
            violations.Add($"硬编码字号 {m.Value}（应改 Font_Size* 令牌）");
        }

        return violations;
    }
}
