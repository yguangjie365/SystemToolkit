namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 封面主色提取的<b>纯数学内核</b>（OM-6）：从封面像素选「彩胶 auto 主色」。
/// </summary>
/// <remarks>
/// <para>与 WPF 解耦：入参是扁平 RGBA 像素数组，出参是 RGB 三元组——任意宿主可测
/// （构造色块的测试不需要 STA/位图）。WPF 适配（BitmapSource→像素采样）在
/// <see cref="CoverPalette"/>。</para>
/// <para>算法对照 NexBox <c>makeVividColor + shiftLightness</c> 的思路：
/// ①降采样后取「最有代表性的色」（按 饱和度×亮度 加权的频次桶，弃近黑近白）；
/// ②<b>钳制</b>饱和度与亮度到可读区间（彩胶固定深色衬底上文字需可读，
/// 主色不得过亮/过灰——2026-09-08 研究报告中「钳制饱和/亮度保证可读」的实现落点）。
/// </para>
/// </remarks>
public static class PaletteMath
{
    /// <summary>内部色相-饱和度-亮度表示。</summary>
    public readonly record struct Hsl(double H, double S, double L);

    /// <summary>输出 RGB（0–255）。</summary>
    public readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>
    /// 从像素数组提取主色。
    /// </summary>
    /// <param name="rgba">RGBA 字节（每像素 4 字节）。</param>
    /// <param name="pixelCount">像素数（须 ≤ rgba.Length/4）。</param>
    /// <param name="clampSaturation">饱和度钳制下限（0–1；越靠 1 越艳）。</param>
    /// <param name="lightnessMin">亮度钳制下限（0–1，防近黑主色）。</param>
    /// <param name="lightnessMax">亮度钳制上限（0–1，防近白主色）。</param>
    /// <returns>代表主色；空/全透明输入返回中性深灰（调用方应展示「无封面」回退）。</returns>
    public static Rgb PickDominant(ReadOnlySpan<byte> rgba, int pixelCount, double clampSaturation = 0.35, double lightnessMin = 0.22, double lightnessMax = 0.72)
    {
        if (pixelCount <= 0 || rgba.Length < pixelCount * 4)
        {
            return new Rgb(0x44, 0x44, 0x41); // 中性深灰 = 不可用时回退
        }

        // ① 频次加权选代表色：降采样每像素统计（H 分桶，S/L 权重高的更可能当选）
        const int hueBuckets = 36;
        double[] weightSum = new double[hueBuckets];
        double[] sSum = new double[hueBuckets];
        double[] lSum = new double[hueBuckets];
        int[] count = new int[hueBuckets];

        for (int i = 0; i < pixelCount; i++)
        {
            int b = rgba[i * 4];
            int g = rgba[i * 4 + 1];
            int r = rgba[i * 4 + 2];
            int a = rgba[i * 4 + 3];
            if (a < 128)
            {
                continue; // 跳过透明像素（封面圆角/出血区域）
            }

            Hsl hsl = RgbToHsl(r, g, b);
            if (hsl.L < 0.05 || hsl.L > 0.95)
            {
                continue; // 弃近黑近白（无信息量）
            }

            int bucket = (int)(hsl.H / 360.0 * hueBuckets) % hueBuckets;
            weightSum[bucket] += hsl.S * (hsl.L > 0.5 ? 1 - hsl.L : hsl.L) + 0.05; // 饱和度×离中间灰距离，微增底噪防全零
            sSum[bucket] += hsl.S;
            lSum[bucket] += hsl.L;
            count[bucket]++;
        }

        int best = -1;
        double bestWeight = 0;
        for (int bucket = 0; bucket < hueBuckets; bucket++)
        {
            if (count[bucket] > 0 && weightSum[bucket] > bestWeight)
            {
                bestWeight = weightSum[bucket];
                best = bucket;
            }
        }

        if (best < 0)
        {
            return new Rgb(0x44, 0x44, 0x41);
        }

        // ② 代表色 = 桶内平均 HS + 钳制后的亮度
        double h = (best + 0.5) / hueBuckets * 360.0;
        double s = Math.Clamp(sSum[best] / count[best], clampSaturation, 1.0);
        double l = Math.Clamp(lSum[best] / count[best], lightnessMin, lightnessMax);
        Rgb rgb = HslToRgb(h, s, l);
        return rgb;
    }

    /// <summary>RGB（0–255）→ HSL（H 0–360，S/L 0–1）。</summary>
    public static Hsl RgbToHsl(int r, int g, int b)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2.0;
        double delta = max - min;

        if (delta < 1e-9)
        {
            return new Hsl(0, 0, l);
        }

        double s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double h;
        if (max == rd)
        {
            h = (gd - bd) / delta + (gd < bd ? 6 : 0);
        }
        else if (max == gd)
        {
            h = (bd - rd) / delta + 2;
        }
        else
        {
            h = (rd - gd) / delta + 4;
        }

        return new Hsl(h * 60, s, l);
    }

    /// <summary>HSL → RGB（0–255）。</summary>
    public static Rgb HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new Rgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    // ════════ NexBox 复刻补充（2026-09-09）：HSV、固定色板匹配、亮度微调、感知亮度、歌词双行拆分 ════════

    public readonly record struct Hsv(double H, double S, double V);

    /// <summary>RGB(0-255) → HSV。</summary>
    public static Hsv RgbToHsv(int r, int g, int b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double d = max - min;
        double v = max;
        double s = max <= 0 ? 0 : d / max;
        double h = 0;
        if (d > 0)
        {
            if (max == rd)
            {
                h = ((gd - bd) / d + (gd < bd ? 6 : 0)) * 60;
            }
            else if (max == gd)
            {
                h = ((bd - rd) / d + 2) * 60;
            }
            else
            {
                h = ((rd - gd) / d + 4) * 60;
            }
        }

        return new Hsv(h, s, v);
    }

    /// <summary>HSV → RGB(0-255)。</summary>
    public static Rgb HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>感知亮度 0–1（ITU-R BT.601 加权；&gt;0.5 视为浅底）。</summary>
    public static double Luminance(byte r, byte g, byte b)
        => (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;

    /// <summary>保持色相/饱和度微调亮度（±dL），钳制 [0.03,0.97]，返回 RGB。</summary>
    public static Rgb ShiftLightness(int r, int g, int b, double dL)
    {
        Hsl hsl = RgbToHsl(r, g, b);
        double nl = Math.Min(0.97, Math.Max(0.03, hsl.L + dL));
        return HslToRgb(hsl.H, hsl.S, nl);
    }

    /// <summary>
    /// 沉浸背景固定色板（对照 NexBox VIVID_PALETTE，均为深色调中性色）：
    /// 封面色相匹配最近色，保证任何封面都落到"耐看的深色"，而非直接压暗封面原色。
    /// </summary>
    public static readonly (string Name, double Hue, Rgb Rgb)[] VividPalette =
    [
        ("青", 194.0, new Rgb(18, 109, 131)),
        ("深蓝", 232.0, new Rgb(48, 54, 121)),
        ("深橙", 18.0, new Rgb(128, 65, 39)),
        ("紫粉", 313.0, new Rgb(126, 53, 110)),
        ("黄色", 44.0, new Rgb(126, 104, 31)),
        ("红色", 354.0, new Rgb(125, 44, 51)),
        ("绿色", 84.0, new Rgb(94, 128, 35)),
        ("灰色", -1.0, new Rgb(51, 51, 51)),
    ];

    /// <summary>
    /// 沉浸背景取色（对照 NexBox makeVividColor）：RGB→HSL 取色相，
    /// 饱和度 &lt; 0.08 视为灰色（不参与匹配）；在固定色板中找色相环最近色。
    /// </summary>
    public static Rgb MatchVividPalette(byte r, byte g, byte b)
    {
        Hsl hsl = RgbToHsl(r, g, b);
        if (hsl.S < 0.08)
        {
            return VividPalette[^1].Rgb;
        }

        Rgb best = VividPalette[0].Rgb;
        double bestDist = 361;
        foreach ((_, double hue, Rgb rgb) in VividPalette)
        {
            if (hue < 0)
            {
                continue;
            }

            double dist = Math.Min(Math.Abs(hsl.H - hue), 360 - Math.Abs(hsl.H - hue));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = rgb;
            }
        }

        return best;
    }

    /// <summary>
    /// 歌词双行拆分（对照 NexBox splitIntoLines）：多句按标点贪心均衡拆两行；
    /// 单长句在中位附近的空格/标点断点拆；短句保持单行。
    /// </summary>
    public static string SplitLyricIntoTwoLines(string text)
    {
        string t = text.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return t;
        }

        char[] separators = ['，', '。', '！', '？', '、', '；', ',', '.', '!', '?', ';'];
        string[] sentences = t.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sentences.Length <= 1)
        {
            string raw = sentences.Length == 1 ? sentences[0] : t;
            if (raw.Replace(" ", "").Length <= 14)
            {
                return raw;
            }

            int mid = raw.Length / 2;
            int cut = -1;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == ' ' || separators.Contains(raw[i]))
                {
                    if (cut < 0 || Math.Abs(i - mid) < Math.Abs(cut - mid))
                    {
                        cut = i + 1;
                    }
                }
            }

            if (cut > 0 && cut < raw.Length)
            {
                return raw[..cut].Trim() + "\n" + raw[cut..].Trim();
            }

            return raw[..mid].Trim() + "\n" + raw[mid..].Trim();
        }

        int totalLen = t.Replace(" ", "").Length;
        int half = totalLen / 2;
        List<string> line1 = [];
        int acc = 0;
        foreach (string s in sentences)
        {
            int sz = s.Replace(" ", "").Length;
            if (line1.Count > 0 && acc + sz > half)
            {
                break;
            }

            line1.Add(s);
            acc += sz;
        }

        if (line1.Count == 0)
        {
            return t;
        }

        string first = string.Concat(line1.Select(s => s + separatorOf(t, s)).ToArray()).Trim();
        string rest = string.Join("", sentences.Skip(line1.Count)).Trim();
        if (string.IsNullOrEmpty(rest))
        {
            // 全塞进第一行：挪最后一句到第二行
            string last = line1[^1];
            line1.RemoveAt(line1.Count - 1);
            string head = string.Concat(line1.Select(s => s + separatorOf(t, s)).ToArray()).Trim();
            return (string.IsNullOrEmpty(head) ? last : head + "\n" + last);
        }

        return first + "\n" + rest;
    }

    private static char separatorOf(string source, string sentence)
    {
        int idx = source.IndexOf(sentence, StringComparison.Ordinal);
        return idx >= 0 && idx + sentence.Length < source.Length ? source[idx + sentence.Length] : ' ';
    }
}
