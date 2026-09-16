using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 主题对比度守卫的**纯函数内核**（WCAG 2.1 相对亮度 / 对比度 / 半透明合成 / 主题包令牌解析）。
/// <para>
/// 与配对表分文件：断言与例外登记在 <c>ThemeContrastGuardTests</c>，算法在 <c>ThemeContrastMath</c>
/// （01 分册 §三 文件长度上限；也便于算法被反向验证直接直测——03 §十：判据放宽到
/// <c>internal</c>/<c>public</c> 才钉得住实现）。
/// </para>
/// </summary>
public static class ThemeContrastMath
{
    /// <summary><c>&lt;Color x:Key="…"&gt;#hex&lt;/Color&gt;</c>（含 8 位 AARRGGBB）。</summary>
    private static readonly Regex ColorElement = new(
        @"<Color\s+x:Key=""(?<key>[^""]+)""\s*>\s*(?<hex>#[0-9A-Fa-f]{3,8})\s*</Color>", RegexOptions.Compiled);

    /// <summary>画刷的显式 hex 取值（<c>&lt;SolidColorBrush … Color="#…" /&gt;</c>）。</summary>
    private static readonly Regex BrushLiteral = new(
        @"<SolidColorBrush\s+x:Key=""(?<key>[^""]+)""\s+Color=""(?<hex>#[0-9A-Fa-f]{3,8})""", RegexOptions.Compiled);

    /// <summary>画刷的别名取值（<c>Color="{StaticResource Color_X}"</c>）——主题包大量使用此写法。</summary>
    private static readonly Regex BrushAlias = new(
        @"<SolidColorBrush\s+x:Key=""(?<key>[^""]+)""\s+Color=""\{StaticResource\s+(?<ref>[A-Za-z0-9_]+)\}""", RegexOptions.Compiled);

    /// <summary>
    /// 解析主题包 XAML 中的颜色取值：key → hex。别名（<c>{StaticResource Color_X}</c>）展开到底，
    /// 多级引用按需循环至收敛。渐变画刷（无单一取值）不入表——它们不是可比较的实色。
    /// </summary>
    public static Dictionary<string, string> ParseTokens(string xaml)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match m in ColorElement.Matches(xaml))
        {
            tokens[m.Groups["key"].Value] = Normalize(m.Groups["hex"].Value);
        }

        foreach (Match m in BrushLiteral.Matches(xaml))
        {
            tokens[m.Groups["key"].Value] = Normalize(m.Groups["hex"].Value);
        }

        foreach (Match m in BrushAlias.Matches(xaml))
        {
            aliases[m.Groups["key"].Value] = m.Groups["ref"].Value;
        }

        for (int pass = 0; pass < 4 && aliases.Count > 0; pass++)
        {
            var resolved = new List<string>();
            foreach ((string key, string target) in aliases)
            {
                if (tokens.TryGetValue(target, out string? hex))
                {
                    tokens[key] = hex;
                    resolved.Add(key);
                }
            }

            foreach (string key in resolved)
            {
                aliases.Remove(key);
            }
        }

        return tokens;
    }

    /// <summary>WCAG 2.1 对比度（1.0–21.0）。</summary>
    public static double ContrastRatio(string fgHex, string bgHex)
    {
        double a = RelativeLuminance(fgHex);
        double b = RelativeLuminance(bgHex);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>WCAG 2.1 相对亮度（sRGB 分量线性化后按 0.2126/0.7152/0.0722 加权）。</summary>
    public static double RelativeLuminance(string hex)
    {
        (int r, int g, int b, _) = Split(hex);
        return (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));
    }

    /// <summary>
    /// 半透明色按 alpha 合成到不透明底上（over 运算符）。
    /// 例：浅包 <c>#1FEF4444</c> 压白底 → <c>#FDE9E9</c>，与 04 规范 §3.1 记录的
    /// 「DangerSoft(#FDE8E8) 底」一致（末位取整差异）；该合成口径已由
    /// <c>ThemeContrastGuardTests</c> 用既成结论 5.51:1 锚定。
    /// </summary>
    public static string Composite(string overlayHex, string baseHex)
    {
        (int or, int og, int ob, int oa) = Split(overlayHex);
        (int br, int bg, int bb, _) = Split(baseHex);
        double a = oa / 255d;

        int r = (int)Math.Round((a * or) + ((1 - a) * br));
        int g = (int)Math.Round((a * og) + ((1 - a) * bg));
        int b = (int)Math.Round((a * ob) + ((1 - a) * bb));
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>sRGB 分量线性化（WCAG 2.1 定义中的 0.03928 分段）。</summary>
    private static double Linear(int component)
    {
        double s = component / 255d;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>拆 hex：支持 <c>#RGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c>（主题包三种都出现过）。</summary>
    private static (int R, int G, int B, int A) Split(string hex)
    {
        string h = Normalize(hex).TrimStart('#');
        if (h.Length == 3)
        {
            h = string.Concat(h.Select(c => new string(c, 2)));
        }

        return h.Length switch
        {
            6 => (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16), 255),
            8 => (Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16), Convert.ToInt32(h[6..8], 16), Convert.ToInt32(h[..2], 16)),
            _ => throw new InvalidOperationException($"无法解析的颜色字面量：{hex}"),
        };
    }

    /// <summary>统一成带 <c>#</c> 前缀的形式（仅用于解析与键比较，不影响取值）。</summary>
    private static string Normalize(string hex) => hex.StartsWith('#') ? hex : "#" + hex;
}
