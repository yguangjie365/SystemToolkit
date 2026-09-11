using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 封面主色刷工厂（OM-6）：BitmapSource → 钳制主色 SolidColorBrush。
/// </summary>
/// <remarks>
/// <para>🔴 <b>为何独立成文件</b>：本类是项目唯一的运行时色创建点（每首封面主色不同，
/// 无法用静态设计令牌表达——令牌是编译期固定的）。<c>CsBrushLiteralGuardTests</c> 的
/// <c>AllowedFiles</c> 显式豁免本文件；任何新增运行时色工厂都应并入本类，而不是在别处
/// 散布 <c>Color.FromRgb</c> 字面量。</para>
/// </remarks>
public static class CoverColorFactory
{
    /// <summary>无封面时的中性深灰主色（视觉与主题深海军蓝表面一致）。</summary>
    public static readonly SolidColorBrush Neutral = Create(0x44, 0x44, 0x41);

    /// <summary>
    /// 彩胶风格浅背景：主色高亮度、低饱和的近白染色（对照 NexBox 透明彩胶的浅底微染）。
    /// </summary>
    public static SolidColorBrush LightTint(SolidColorBrush accent)
    {
        PaletteMath.Hsl hsl = PaletteMath.RgbToHsl(accent.Color.R, accent.Color.G, accent.Color.B);
        PaletteMath.Rgb rgb = PaletteMath.HslToRgb(hsl.H, 0.18, 0.92);
        return Create(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// 沉浸风格深背景：主色压暗加深（对照 NexBox 沉浸的深色主色渐变底）。
    /// </summary>
    /// <param name="lightness">
    /// 目标明度（默认 0.30）。深色主题下现代背景要更暗一档（用 0.16）——
    /// 复用同一套 HSL 派生，避免为深色档另造颜色字面量。
    /// </param>
    public static SolidColorBrush DarkImmersive(SolidColorBrush accent, double lightness = 0.30)
    {
        PaletteMath.Hsl hsl = PaletteMath.RgbToHsl(accent.Color.R, accent.Color.G, accent.Color.B);
        PaletteMath.Rgb rgb = PaletteMath.HslToRgb(hsl.H, Math.Max(hsl.S, 0.40), lightness);
        return Create(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// 现代风格中调背景：主色中亮度中饱和（对照 NexBox 现代的灰紫中调底）。
    /// </summary>
    public static SolidColorBrush MidTone(SolidColorBrush accent)
    {
        PaletteMath.Hsl hsl = PaletteMath.RgbToHsl(accent.Color.R, accent.Color.G, accent.Color.B);
        PaletteMath.Rgb rgb = PaletteMath.HslToRgb(hsl.H, Math.Min(Math.Max(hsl.S, 0.15), 0.30), 0.74);
        return Create(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>位图 → 主色刷（下采样像素后走 <see cref="PaletteMath"/>；冻结以便多线程安全）。</summary>
    public static SolidColorBrush FromBitmap(BitmapSource source)
    {
        // 🟡 审查 2026-09-10（🟡-24）：stride 按 Bgra32（4 字节/像素）计算，
        // 源若不是 Bgra32（索引色 GIF、Gray8、Rgb24、带 Alpha 的 Pbgra32 等都是不同布局），
        // CopyPixels 会因参数与实际像素格式不符抛 ArgumentException。
        // 统一先转 Bgra32 再读——转换有开销，但对封面这种一次性操作可忽略。
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        PaletteMath.Rgb rgb = PaletteMath.PickDominant(pixels, bgra.PixelWidth * bgra.PixelHeight);
        return Create(rgb.R, rgb.G, rgb.B);
    }

    // ════════ NexBox 复刻补充（2026-09-09，对照 MusicPage.tsx / VinylDisc.tsx） ════════

    /// <summary>
    /// 彩胶页背景（对照 NexBox）：固定浅灰三段渐变 160°（#dfdfe2/#d8d8dc/#d1d1d6）——
    /// 不随封面变化，彩色只在盘体。
    /// </summary>
    public static Brush VinylBackgroundGradient(bool dark = false)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = GradientPoint(160),
            EndPoint = GradientEnd(160),
        };
        if (dark)
        {
            // 深色主题档：同结构的三段深灰（对照浅色档 #DFDFE2/#D8D8DC/#D1D1D6 的暗色镜像）。
            // 🔴 2026-09-11：原来彩胶背景是**固定浅灰**（不随封面），深色主题下与宿主的深色
            // 按钮底/深侧栏直接冲突——深背景上压深灰按钮即「黑块」。深色档由此而来。
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1E, 0x20, 0x24), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x23, 0x26, 0x2B), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x19, 0x1B, 0x1F), 1.0));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xDF, 0xDF, 0xE2), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD8, 0xD8, 0xDC), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD1, 0xD1, 0xD6), 1.0));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>沉浸页背景（对照 immersiveBgGradient）：色板匹配色 90° 三段（深 0% → 本色 50% → 浅 100%）。</summary>
    public static (Color Base, Brush Gradient) ImmersionBackgroundGradient(SolidColorBrush accent)
    {
        (SolidColorBrush baseBrush, SolidColorBrush deep, SolidColorBrush light) = ImmersiveVivid(accent);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop(deep.Color, 0.0));
        brush.GradientStops.Add(new GradientStop(baseBrush.Color, 0.5));
        brush.GradientStops.Add(new GradientStop(light.Color, 1.0));
        brush.Freeze();
        return (baseBrush.Color, brush);
    }

    /// <summary>沉浸基色默认值（封面未装载前的中性深蓝；Color.FromRgb 唯一收敛点纪律）。</summary>
    public static Color ImmersionDefaultVivid { get; } = Color.FromRgb(0x30, 0x36, 0x79);

    /// <summary>高亮色·深档（亮底用：封面主色 HSL 亮度 −0.18，保持可读）。</summary>
    public static SolidColorBrush AccentDeep(SolidColorBrush accent)
    {
        PaletteMath.Rgb rgb = PaletteMath.ShiftLightness(accent.Color.R, accent.Color.G, accent.Color.B, -0.18);
        return FromRgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>高亮色·浅档（深底用：主色 HSL 亮度 +0.35）。</summary>
    public static SolidColorBrush AccentLight(Color accent)
    {
        PaletteMath.Rgb rgb = PaletteMath.ShiftLightness(accent.R, accent.G, accent.B, 0.35);
        return FromRgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>现代页背景（对照 modernBgGradient）：封面原色 135°，0–40% 平铺、100% 压暗 ×0.25。</summary>
    public static Brush ModernBackgroundGradient(SolidColorBrush accent, bool dark = false)
    {
        // 深色主题档：封面原色**不再铺满**（浅封面会把整个播放器带亮，与宿主深色割裂），
        // 改走压暗版（HSL 明度 0.30，收尾 0.16），只保留封面色相作品牌感。
        SolidColorBrush baseColor = dark ? DarkImmersive(accent) : accent;
        SolidColorBrush tail = dark ? DarkImmersive(accent, 0.16) : ModernDark(accent);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(baseColor.Color, 0.0));
        brush.GradientStops.Add(new GradientStop(baseColor.Color, 0.4));
        brush.GradientStops.Add(new GradientStop(tail.Color, 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>彩胶盘光晕（对照 VinylDisc 光晕层）：accent 径向 0.30 → 0.13(42%) → 透明(68%)。</summary>
    public static Brush VinylGlow(SolidColorBrush accent)
    {
        Color c = accent.Color;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x4D, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x21, c.R, c.G, c.B), 0.42),
                new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.68),
            },
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 沉浸水波单环刷（对照 ImmersiveRippleField）：左波用背景色加深（L×0.62）、
    /// 右波用提亮（L+0.18）的九停靠点同心波环，保留色相（非黑白）。
    /// </summary>
    public static Brush ImmersionRippleRing(Color baseColor, bool isLeftWave)
    {
        PaletteMath.Hsl hsl = PaletteMath.RgbToHsl(baseColor.R, baseColor.G, baseColor.B);
        double ringL = isLeftWave ? Math.Max(0.16, hsl.L * 0.62) : Math.Min(0.72, hsl.L + 0.18);
        PaletteMath.Rgb ring = PaletteMath.HslToRgb(hsl.H, hsl.S, ringL);
        var brush = new RadialGradientBrush();
        foreach ((double offset, double alpha) in new[]
        {
            (0.0, 0.0), (0.12, 1.0), (0.22, 0.0), (0.34, 0.83),
            (0.46, 0.0), (0.58, 0.6), (0.70, 0.0), (0.84, 0.33), (1.0, 0.0),
        })
        {
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(alpha * 0x99), ring.R, ring.G, ring.B), offset));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>CSS 渐变角 → WPF StartPoint（0°=向上，顺时针）。</summary>
    private static Point GradientPoint(double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180;
        return new Point(0.5 - Math.Sin(rad) / 2, 0.5 + Math.Cos(rad) / 2);
    }

    /// <summary>CSS 渐变角 → WPF EndPoint。</summary>
    private static Point GradientEnd(double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180;
        return new Point(0.5 + Math.Sin(rad) / 2, 0.5 - Math.Cos(rad) / 2);
    }

    /// <summary>RGB 直构冻结刷（NexBox 复刻的文字色出口；字面色只允许出现在本文件）。</summary>
    public static SolidColorBrush FromRgb(byte r, byte g, byte b) => Create(r, g, b);

    /// <summary>
    /// 彩胶主色（对照 vinylAccent）：封面主色 → HSV 钳制饱和度 [0.48,0.92]、明度 [0.34,0.66]，
    /// 保证胶片色在浅灰底上既鲜艳又可读；custom 固定色由调用方直接传入。
    /// </summary>
    public static SolidColorBrush VinylAccent(SolidColorBrush accent)
    {
        PaletteMath.Hsv hsv = PaletteMath.RgbToHsv(accent.Color.R, accent.Color.G, accent.Color.B);
        double s = Math.Min(0.92, Math.Max(0.48, hsv.S));
        double v = Math.Min(0.66, Math.Max(0.34, hsv.V));
        PaletteMath.Rgb rgb = PaletteMath.HsvToRgb(hsv.H, s, v);
        return Create(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>沉浸背景三段（对照 makeVividColor）：封面色相匹配固定深色板 → 本色 + 深/浅（±0.07 亮度）。</summary>
    public static (SolidColorBrush Base, SolidColorBrush Deep, SolidColorBrush Light) ImmersiveVivid(SolidColorBrush accent)
    {
        PaletteMath.Rgb matched = PaletteMath.MatchVividPalette(accent.Color.R, accent.Color.G, accent.Color.B);
        PaletteMath.Rgb deep = PaletteMath.ShiftLightness(matched.R, matched.G, matched.B, -0.07);
        PaletteMath.Rgb light = PaletteMath.ShiftLightness(matched.R, matched.G, matched.B, +0.07);
        return (Create(matched.R, matched.G, matched.B), Create(deep.R, deep.G, deep.B), Create(light.R, light.G, light.B));
    }

    /// <summary>现代背景右缘压暗（对照 modernBgDark）：封面原色 × 0.25。</summary>
    public static SolidColorBrush ModernDark(SolidColorBrush accent)
        => Create((byte)(accent.Color.R * 0.25), (byte)(accent.Color.G * 0.25), (byte)(accent.Color.B * 0.25));

    /// <summary>浅底判定（对照 coverColor.isLight）：感知亮度 &gt; 0.5 → 深色文字。</summary>
    public static bool IsLight(SolidColorBrush brush)
        => PaletteMath.Luminance(brush.Color.R, brush.Color.G, brush.Color.B) > 0.5;

    /// <summary>RGB + alpha 直构冻结刷（半透明文字色；冻结刷不可改 Opacity——实测教训）。</summary>
    public static SolidColorBrush FromRgbWithOpacity(byte r, byte g, byte b, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Create(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
