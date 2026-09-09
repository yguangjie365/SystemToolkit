using System.Globalization;
using System.Text.RegularExpressions;
using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 逐行 LRC 歌词解析与进度算法（MUSIC-3；自旧工程
/// <c>FileBackup.CSharp/…/LyricParser.cs</c>（375 行）搬移并按本项目模型改写）。
/// </summary>
/// <remarks>
/// <para><b>搬移范围</b>（任务板 MUSIC-3 删改清单，别多做也别少做）：
/// 删 <c>ParseYrc</c>、删 <c>BuildKaraokeLines</c> 的 YRC 分支、删 <c>GetLineProgress</c>
/// 的逐字分支、删 <c>FinalizeLineDurations</c> 的 <c>CharCount</c> clamp、
/// <c>KaraokeLine</c> → <see cref="LyricLine"/>。
/// ⚠️ 2026-09-09 用户裁决恢复逐字卡拉OK（对照 NexBox/Mineradio 算法）：
/// <see cref="ParseYrc"/> 与 <see cref="GetLineProgress"/> 逐字分支、CharCount clamp 均已恢复。</para>
/// <para><b>四个算法逐字未改</b>：行级插值 + smoothstep、水平滚动</para>
/// （startGate 0.08 / endGate 0.78）、<c>CalcActiveIndex</c>（最后一条 time ≤ now+0.05）、
/// 时长推断与 clamp（0.45–12s）。
/// <para><b>相对旧实现的修复</b>：旧 <c>ParseLrcEnhanced</c> 遇到不含
/// <c>[mm:ss.xx]</c> 的文本直接返回空列表，UI 只能显示「暂无歌词」——
/// 明明内嵌着歌词却告诉用户没有。本实现改为回填 <see cref="LyricDocument.PlainText"/>。</para>
/// </remarks>
public static partial class LyricParser
{
    /// <summary>时间标签 <c>[mm:ss.xx]</c>（分 2 位起、秒可带小数）。</summary>
    private static readonly Regex TimeTag = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

    /// <summary>无下一行可推断时的默认时长（秒），与旧实现一致。</summary>
    private const double DefaultDuration = 4.8;

    /// <summary>最短/最长时长 clamp（秒），与旧实现一致。</summary>
    private const double MinDuration = 0.45;
    private const double MaxDuration = 12.0;

    /// <summary>
    /// 解析歌词文本。
    /// </summary>
    /// <param name="lyric">LRC 正文（可为不含时间标签的纯文本）。</param>
    /// <param name="translation">可选的译文 LRC（按时间戳匹配，容差 0.5s）。</param>
    /// <param name="source">歌词来源（内嵌 / 外部文件）。</param>
    /// <returns>
    /// 有时间标签 → <see cref="LyricDocument.Lines"/> 填充、<c>PlainText</c> 为 null；
    /// 无时间标签但文本非空 → <c>Lines</c> 空、<c>PlainText</c> 为原文；
    /// 完全为空 → <see cref="LyricDocument.None"/>。
    /// </returns>
    public static LyricDocument Parse(string? lyric, string? translation = null,
        LyricSource source = LyricSource.Embedded)
    {
        if (string.IsNullOrWhiteSpace(lyric))
        {
            return LyricDocument.None();
        }

        List<LyricLine> lines = ParseTimedLines(lyric, translation);
        if (lines.Count > 0)
        {
            return new LyricDocument { Source = source, Lines = lines, PlainText = null };
        }

        // 无时间标签：整篇兜底（旧实现在此丢失歌词）
        return new LyricDocument { Source = source, Lines = [], PlainText = lyric.Trim() };
    }

    /// <summary>解析带时间标签的歌词行（无标签时返回空列表）。</summary>
    public static List<LyricLine> ParseTimedLines(string lyric, string? translation = null)
    {
        if (string.IsNullOrWhiteSpace(lyric))
        {
            return [];
        }

        Dictionary<double, string> transMap = BuildTranslationMap(translation);
        var lines = new List<LyricLine>();

        foreach (string raw in lyric.Split('\n'))
        {
            MatchCollection matches = TimeTag.Matches(raw);
            if (matches.Count == 0)
            {
                continue;
            }

            string text = TimeTag.Replace(raw, string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            foreach (Match m in matches)
            {
                lines.Add(new LyricLine
                {
                    Time = ParseTimestamp(m),
                    Duration = 0,
                    Text = text,
                    Translation = transMap.TryGetValue(ParseTimestamp(m), out string? t) ? t : null,
                });
            }
        }

        return FinalizeLineDurations(lines);
    }

    /// <summary>
    /// 解析 YRC/QRC 逐字歌词（2026-09-09 恢复；对照 NexBox parseYrc）。
    /// 行头 <c>[startMs,durMs]</c>；词标签兼容圆括号 <c>(ws,wd,0)词</c>（网易 YRC）
    /// 与尖括号 <c>&lt;ws,wd,0&gt;词</c>（QQ QRC）。词时间戳为绝对值，相对行头偏移自动判别。
    /// </summary>
    public static LyricDocument ParseYrc(string? yrc, string? translation = null)
    {
        List<LyricLine> lines = [];
        if (!string.IsNullOrWhiteSpace(yrc))
        {
            foreach (string rawLine in yrc.Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                Match lineMatch = LineHeadRegex().Match(rawLine);
                if (!lineMatch.Success)
                {
                    continue;
                }

                double lineStartMs = ParseMs(lineMatch.Groups[1].Value);
                double lineDurMs = ParseMs(lineMatch.Groups[2].Value);
                string body = lineMatch.Groups[3].Value;

                List<LyricWord> words = [];
                var textBuilder = new System.Text.StringBuilder();
                foreach (Match wm in WordTagRegex().Matches(body))
                {
                    string txt = Regex.Replace(wm.Groups[3].Value, @"\s+", " ");
                    if (txt.Length == 0)
                    {
                        continue;
                    }

                    double rawStart = ParseMs(wm.Groups[1].Value);
                    double rawDur = ParseMs(wm.Groups[2].Value);
                    // 词时间戳 ≥ 行头-500ms 视为绝对时间，否则为相对行头偏移
                    double absStartMs = rawStart >= lineStartMs - 500 ? rawStart : lineStartMs + rawStart;

                    int c0 = textBuilder.Length;
                    textBuilder.Append(txt);
                    words.Add(new LyricWord(txt, absStartMs / 1000.0, Math.Max(0.06, rawDur / 1000.0), c0, textBuilder.Length));
                }

                string fullText = textBuilder.ToString();
                if (fullText.Length == 0)
                {
                    fullText = Regex.Replace(body, @"[(<]\d+,\d+,\d+[)>]", " ");
                }

                // 去前导空白并按前导长度修正字符区间（对照 NexBox）
                int leading = 0;
                while (leading < fullText.Length && fullText[leading] == ' ')
                {
                    leading++;
                }

                fullText = Regex.Replace(fullText, @"\s+", " ").Trim();
                if (fullText.Length == 0)
                {
                    continue;
                }

                if (words.Count > 0)
                {
                    List<LyricWord> valid = [];
                    foreach (LyricWord w in words)
                    {
                        int c0 = Math.Max(0, Math.Min(fullText.Length, w.C0 - leading));
                        int c1 = Math.Max(c0, Math.Min(fullText.Length, w.C1 - leading));
                        if (c1 > c0)
                        {
                            valid.Add(w with { C0 = c0, C1 = c1 });
                        }
                    }

                    if (valid.Count == 0)
                    {
                        continue;
                    }

                    lines.Add(new LyricLine
                    {
                        Time = lineStartMs / 1000.0,
                        Duration = lineDurMs / 1000.0,
                        Text = fullText,
                        Words = valid,
                        CharCount = Math.Max(1, fullText.Length),
                    });
                }
                else
                {
                    lines.Add(new LyricLine
                    {
                        Time = lineStartMs / 1000.0,
                        Duration = lineDurMs / 1000.0,
                        Text = fullText,
                        CharCount = Math.Max(1, fullText.Length),
                    });
                }
            }
        }

        lines = FinalizeLineDurations(lines);

        // 译文合并：按 ±0.5s 就近匹配（对照 NexBox；YRC 时间与 LRC 时间很少完全相等）
        if (!string.IsNullOrWhiteSpace(translation))
        {
            for (int li = 0; li < lines.Count; li++)
            {
                LyricLine line = lines[li];
                string? best = null;
                double bestDiff = 0.5;
                foreach (string transLine in translation.Split(["\r\n", "\n"], StringSplitOptions.None))
                {
                    Match m = TransTimeRegex().Match(transLine);
                    if (!m.Success)
                    {
                        continue;
                    }

                    double time = ParseMs(m.Groups[1].Value) * 60 + ParseMs(m.Groups[2].Value);
                    string text = TransTimeRegex().Replace(transLine, "").Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    double diff = Math.Abs(time - line.Time);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = text;
                    }
                }

                if (best is not null)
                {
                    lines[li] = line with { Translation = best };
                }
            }
        }

        return new LyricDocument { Source = LyricSource.Online, Lines = lines };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial System.Text.RegularExpressions.Regex LineHeadRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"[(<](\d+),(\d+),\d+[)>]([^()<>]*)")]
    private static partial System.Text.RegularExpressions.Regex WordTagRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial System.Text.RegularExpressions.Regex TransTimeRegex();

    private static double ParseMs(string s)
        => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>
    /// 行进度（0~1）：行级线性插值 + smoothstep 缓动。
    /// 含 +0.02s 视觉补偿（让进度略超前于实际音频）。
    /// </summary>
    public static double GetLineProgress(LyricLine? line, LyricLine? nextLine, double currentTime)
    {
        if (line is null)
        {
            return 0;
        }

        // 逐字分支（对照 NexBox getLineProgress）：按词的字符区间精确推进
        if (line.Words is { Count: > 0 } words && line.CharCount > 0)
        {
            double nowWord = currentTime + 0.03; // 逐字视觉补偿略大（对照 NexBox）
            double last = 0;
            foreach (LyricWord w in words)
            {
                double ws = w.T;
                double we = w.T + Math.Max(0.08, w.D);
                if (nowWord < ws)
                {
                    return last;
                }

                double local = nowWord >= we ? 1 : (nowWord - ws) / Math.Max(0.08, we - ws);
                local = Math.Max(0, Math.Min(1, local));
                double p = (w.C0 + ((w.C1 - w.C0) * local)) / line.CharCount;
                last = Math.Max(last, p);
                if (nowWord < we)
                {
                    return last;
                }
            }

            return 1;
        }

        double now = currentTime + 0.02;
        double nextT = nextLine is not null && nextLine.Time > line.Time
            ? nextLine.Time
            : line.Time + (line.Duration > 0 ? line.Duration : DefaultDuration);
        double span = Math.Max(0.75, nextT - line.Time);
        double prog = Math.Max(0, Math.Min(1, (now - line.Time) / span));
        return prog * prog * (3 - 2 * prog); // smoothstep
    }

    /// <summary>
    /// 超长歌词水平滚动偏移：前 8% 静止（看清行首）→ 中段缓动 → 78% 后保持在最远处。
    /// </summary>
    public static double CalculateScrollOffset(double progress, double limit)
    {
        if (limit <= 0)
        {
            return 0;
        }

        double p = Math.Max(0, Math.Min(1, progress));
        const double startGate = 0.08;
        const double endGate = 0.78;

        if (p < startGate)
        {
            return 0;
        }

        if (p >= endGate)
        {
            return -limit;
        }

        double t = (p - startGate) / (endGate - startGate);
        double eased = t * t * (3 - 2 * t);
        return -limit * eased;
    }

    /// <summary>
    /// 当前播放时间对应的活跃行索引（最后一条 <c>Time ≤ currentTime + 0.05</c>）。
    /// 空列表返回 -1；时间早于第一行时返回 0（未来行尚未开始）。
    /// </summary>
    public static int CalcActiveIndex(List<LyricLine> lines, double currentTime)
    {
        if (lines.Count == 0)
        {
            return -1;
        }

        int idx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time <= currentTime + 0.05)
            {
                idx = i;
            }
            else
            {
                break;
            }
        }

        return idx < 0 ? 0 : idx;
    }

    /// <summary>把 <c>[mm:ss.xx]</c> 匹配转换为秒。</summary>
    private static double ParseTimestamp(Match m)
        => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
           + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

    /// <summary>构建译文时间索引（容差匹配在 <see cref="ParseTimedLines"/> 中按精确键查找）。</summary>
    private static Dictionary<double, string> BuildTranslationMap(string? translation)
    {
        var map = new Dictionary<double, string>();
        if (string.IsNullOrWhiteSpace(translation))
        {
            return map;
        }

        foreach (string line in translation.Split('\n'))
        {
            Match m = TimeTag.Match(line);
            if (!m.Success)
            {
                continue;
            }

            string text = TimeTag.Replace(line, string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            map[ParseTimestamp(m)] = text;
        }

        return map;
    }

    /// <summary>排序 + 由下一行推断时长 + clamp 到 0.45–12s。</summary>
    /// <summary>零宽/不可见字符（网易/QQ 歌词的时间轴占位，Trim 与 \s 均不匹配）。</summary>
    private static readonly System.Text.RegularExpressions.Regex InvisibleCharsRegex =
        new("[\u200b\u200c\u200d\u2060\ufeff]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 收口：剥离零宽不可见字符 → 丢弃清理后为空的行（🔴 空行渲染成 ~40px 空白容器，
    /// 连续多个在列表中形成大块空白——2026-09-09 截图实证）→ 排序 → 推断时长。
    /// </summary>
    private static List<LyricLine> FinalizeLineDurations(List<LyricLine> lines)
    {
        lines.RemoveAll(l => string.IsNullOrWhiteSpace(InvisibleCharsRegex.Replace(l.Text, "")));
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));

        for (int i = 0; i < lines.Count; i++)
        {
            // 剥离零宽字符后的文本才代表实际渲染宽度，CharCount 以清理后的为准
            LyricLine cleaned = lines[i] with { Text = InvisibleCharsRegex.Replace(lines[i].Text, "") };
            LyricLine? next = i + 1 < lines.Count ? lines[i + 1] : null;
            double inferred = next is not null && next.Time > cleaned.Time
                ? next.Time - cleaned.Time
                : DefaultDuration;
            double dur = cleaned.Duration;
            if (!double.IsFinite(dur) || dur <= 0)
            {
                dur = inferred;
            }

            dur = Math.Max(MinDuration, Math.Min(MaxDuration, dur));
            int charCount = Math.Max(1, Math.Max(cleaned.CharCount, cleaned.Text.Length));
            lines[i] = cleaned with { Duration = dur, CharCount = charCount };
        }

        return lines;
    }
}
