using SystemToolkit.Modules.MusicManager;

namespace SystemToolkit.Tests.Music;

/// <summary>OM-6 主色提取纯数学内核：确定性 + 钳制 + 抗噪。</summary>
public class MusicPaletteTests
{
    private static byte[] Solid(int r, int g, int b, int count = 400, byte alpha = 255)
    {
        byte[] px = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            px[i * 4] = (byte)b;
            px[i * 4 + 1] = (byte)g;
            px[i * 4 + 2] = (byte)r;
            px[i * 4 + 3] = alpha;
        }
        return px;
    }

    private static byte[] TwoTone(byte[] a, byte[] b, int aCount, int bCount)
    {
        byte[] px = new byte[(aCount + bCount) * 4];
        Array.Copy(a, 0, px, 0, aCount * 4);
        Array.Copy(b, 0, px, aCount * 4, bCount * 4);
        return px;
    }

    [Fact]
    public void SolidRed_PicksRedFamily()
    {
        PaletteMath.Rgb rgb = PaletteMath.PickDominant(Solid(220, 40, 40), 400);

        // 红主色：R 明显大于 G/B，且非灰
        Assert.True(rgb.R > rgb.G + 60, $"实际 {rgb}");
        Assert.True(rgb.R > 120);
    }

    [Fact]
    public void DominantWins_WhenMajorityColorHasClearHue()
    {
        // 70% 蓝 + 30% 灰 → 主色落蓝族
        PaletteMath.Rgb rgb = PaletteMath.PickDominant(
            TwoTone(Solid(40, 80, 220, 700), Solid(200, 200, 200, 300), 700, 300), 1000);

        Assert.True(rgb.B > rgb.R + 50, $"实际 {rgb}");
    }

    [Fact]
    public void OverlyDarkAndBright_AreRejected_AndClampedToReadableBand()
    {
        // 极端深蓝与近白各半 → 代表亮度被钳到 [0.22, 0.72]（不产出黑/白主色）
        PaletteMath.Rgb rgb = PaletteMath.PickDominant(
            TwoTone(Solid(5, 10, 40, 500), Solid(250, 250, 250, 500), 500, 500), 1000);

        int luminance = (int)(0.299 * rgb.R + 0.587 * rgb.G + 0.114 * rgb.B);
        Assert.InRange(luminance, 20, 235);
        Assert.False(rgb.R >= 250 && rgb.G >= 250 && rgb.B >= 250); // 不是近白
    }

    [Fact]
    public void Deterministic_SameInputSameOutput()
    {
        byte[] px = TwoTone(Solid(30, 120, 200, 600), Solid(200, 120, 30, 400), 600, 400);

        PaletteMath.Rgb a = PaletteMath.PickDominant(px, 1000);
        PaletteMath.Rgb b = PaletteMath.PickDominant(px, 1000);

        Assert.Equal(a, b);
    }

    [Fact]
    public void EmptyOrTransparent_ReturnsNeutralFallback()
    {
        PaletteMath.Rgb empty = PaletteMath.PickDominant([], 0);
        PaletteMath.Rgb transparent = PaletteMath.PickDominant(Solid(200, 40, 40, 4, alpha: 0), 4); // alpha 0

        Assert.True(empty.R is > 40 and < 120); // 中性深灰区间
        Assert.Equal(empty, transparent);
    }

    [Fact]
    public void VividMinority_Wins_Over_LargeMutedBackground()
    {
        // OM-10 基准用例（《齐天大圣》封面的合成替身）：70% 低饱和紫底 + 30% 高饱和金 →
        // 取色应选金（QQ 同封面取金）；旧面积加权会取紫/红棕
        byte[] px = TwoTone(Solid(120, 80, 170, 700), Solid(212, 175, 55, 300), 700, 300);

        PaletteMath.Rgb rgb = PaletteMath.PickDominant(px, 1000);

        Assert.True(rgb.R > 140 && rgb.G > 110 && rgb.B < 110,
            $"小面积高饱和金应胜过大面积低饱和紫，实际 {rgb}");
    }

    [Fact]
    public void LowSaturationBackground_DoesNotVoteForHue()
    {
        // 高饱和青主体 + 大面积近灰底 → 灰底不计入色相桶（S<0.08 跳过），主色仍落青族
        byte[] px = TwoTone(Solid(30, 140, 180, 400), Solid(205, 205, 205, 600), 400, 600);

        PaletteMath.Rgb rgb = PaletteMath.PickDominant(px, 1000);

        Assert.True(rgb.B > rgb.R + 40, $"实际 {rgb}");
    }

    [Theory]
    [InlineData(255, 0, 0, 0)] // 纯红 → H=0
    [InlineData(0, 255, 0, 120)] // 纯绿 → H=120
    [InlineData(0, 0, 255, 240)] // 纯蓝 → H=240
    public void RgbToHsl_AnchorPoints(int r, int g, int b, double expectedH)
    {
        PaletteMath.Hsl hsl = PaletteMath.RgbToHsl(r, g, b);
        Assert.InRange(hsl.H, expectedH - 0.5, expectedH + 0.5);
    }
}
