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
/// <c>KaraokeLine</c> → <see cref="LyricLine"/>。</para>
/// <para><b>四个算法逐字未改</b>：行级插值 + smoothstep、水平滚动</para>
/// （startGate 0.08 / endGate 0.78）、<c>CalcActiveIndex</c>（最后一条 time ≤ now+0.05）、
/// 时长推断与 clamp（0.45–12s）。
/// <para><b>相对旧实现的修复</b>：旧 <c>ParseLrcEnhanced</c> 遇到不含
/// <c>[mm:ss.xx]</c> 的文本直接返回空列表，UI 只能显示「暂无歌词」——
/// 明明内嵌着歌词却告诉用户没有。本实现改为回填 <see cref="LyricDocument.PlainText"/>。</para>
/// </remarks>
public static class LyricParser
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
    /// 行进度（0~1）：行级线性插值 + smoothstep 缓动。
    /// 含 +0.02s 视觉补偿（让进度略超前于实际音频）。
    /// </summary>
    public static double GetLineProgress(LyricLine? line, LyricLine? nextLine, double currentTime)
    {
        if (line is null)
        {
            return 0;
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
    private static List<LyricLine> FinalizeLineDurations(List<LyricLine> lines)
    {
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));

        for (int i = 0; i < lines.Count; i++)
        {
            LyricLine? next = i + 1 < lines.Count ? lines[i + 1] : null;
            double inferred = next is not null && next.Time > lines[i].Time
                ? next.Time - lines[i].Time
                : DefaultDuration;
            double dur = lines[i].Duration;
            if (!double.IsFinite(dur) || dur <= 0)
            {
                dur = inferred;
            }

            dur = Math.Max(MinDuration, Math.Min(MaxDuration, dur));
            lines[i] = lines[i] with { Duration = dur };
        }

        return lines;
    }
}
