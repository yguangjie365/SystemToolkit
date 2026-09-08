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
}
