using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SystemToolkit.Modules.MusicManager;
using Xunit;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 播放器背景守卫（2026-09-19 建立）。
/// <para>
/// 背景：大跨度低对比的渐变在 8-bit 下每级要跨上百像素，量化台阶会被人眼 Mach band 强化成
/// **可见的斜向条纹**（用户实测：最长平坦区段 446px；同屏纯色侧栏则无条纹）。
/// 结论分两步：① **彩胶背景直接改纯色**（确定性消除，与 DPI/GPU/合成路径无关）；
/// ② 沉浸/现代两风格仍是渐变 ⇒ 由 dither 噪声层负责打断硬边。
/// </para>
/// <para>本守卫锁三件事：① 彩胶背景必须是纯色（不得改回渐变）；② dither 噪声层配置正确且**仅对非彩胶生效**；
/// ③ 噪声源确实是随机分布（没被换成低对比纹理）。</para>
/// </summary>
public sealed class BackgroundDitherGuardTests
{
    private const string XamlRel = @"src\SystemToolkit.Modules.MusicManager\MusicManagerView.xaml";

    [Fact]
    public void VinylBackground_MustBeFlatColor()
    {
        Exception? err = null;
        bool darkFlat = false;
        bool lightFlat = false;
        var t = new Thread(() =>
        {
            try
            {
                ViewLoadSmokeGuardTests.EnsureApplication();
                darkFlat = CoverColorFactory.VinylBackground(true) is SolidColorBrush;
                lightFlat = CoverColorFactory.VinylBackground(false) is SolidColorBrush;
            }
            catch (Exception e)
            {
                err = e;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(err);

        // 渐变正是本缺陷的根源：三段总跨度约 10 级、横跨约 1900×1280 ⇒ 每级约 120px 的量化台阶
        // 被 Mach band 强化成可见斜向条纹。dither 只能部分压制（实测 446px → 67px），故改纯色。
        Assert.True(darkFlat, "深色档彩胶背景必须是纯色（SolidColorBrush）—— 改回渐变会让斜向条纹复发。");
        Assert.True(lightFlat, "浅色档彩胶背景必须是纯色（SolidColorBrush）—— 改回渐变会让斜向条纹复发。");
    }

    [Fact]
    public void BackgroundGrid_MustCarryDitherNoiseLayer()
    {
        string xaml = File.ReadAllText(Path.Combine(ViewLoadSmokeGuardTests.RepoRoot(), XamlRel));

        int i = xaml.IndexOf("Grid.RowSpan=\"3\"", StringComparison.Ordinal);
        Assert.True(i > 0, "未找到背景层 Grid（Grid.RowSpan=\"3\"）—— 扫描面疑似失效。");

        // 取背景 Grid 的整段（到其闭合标签；留足余量）
        string block = xaml.Substring(i, Math.Min(2200, xaml.Length - i));

        Assert.Contains("VinylTextureFactory.NoiseSource", block, StringComparison.Ordinal);
        Assert.Contains("TileMode=\"Tile\"", block, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", block, StringComparison.Ordinal);

        // dither 只对**仍是渐变**的沉浸/现代两风格有意义；彩胶已改纯色，必须对它隐藏，
        // 否则纯色背景上会平白多出一层噪点。
        Assert.Contains("IsVinylStyle", block, StringComparison.Ordinal);
        Assert.Contains("InverseToVis", block, StringComparison.Ordinal);

        // 🔴 低透明度必须写在噪声位图的 alpha 通道里，**不能**用 Rectangle.Opacity：
        // Opacity<1 会让 WPF 走 8-bit 中间渲染表面，把 2% 的噪声在中间层就量化掉
        // （实测：用 Opacity=0.02 时最长平坦区段只从 446px 降到 67px）。
        Match m = Regex.Match(block, "<Rectangle[^>]*Opacity=");
        Assert.False(m.Success,
            "dither 噪声层不得使用 Rectangle.Opacity —— 请把低透明度写进位图 alpha（VinylTextureFactory.NoiseAlpha）。");
    }

    [Fact]
    public void NoiseSource_MustBeRandomNotFlat()
    {
        Exception? err = null;
        double sd = 0;
        double alphaMean = 0;
        int edge = 0;
        var t = new Thread(() =>
        {
            try
            {
                ViewLoadSmokeGuardTests.EnsureApplication();
                if (VinylTextureFactory.NoiseSource is not BitmapSource bmp)
                {
                    err = new InvalidOperationException("NoiseSource 不是 BitmapSource。");
                    return;
                }

                int w = bmp.PixelWidth;
                int h = bmp.PixelHeight;
                edge = w;
                byte[] px = new byte[w * h * 4];
                bmp.CopyPixels(px, w * 4, 0);

                double sum = 0;
                double sumA = 0;
                int n = w * h;
                for (int i = 0; i < px.Length; i += 4)
                {
                    sum += px[i];
                    sumA += px[i + 3];
                }

                alphaMean = sumA / n;
                double mean = sum / n;
                double acc = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double d = px[i] - mean;
                    acc += d * d;
                }

                sd = Math.Sqrt(acc / n);
            }
            catch (Exception e)
            {
                err = e;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(err);

        Assert.True(edge >= 64, $"噪声源尺寸过小（{edge}），平铺周期会肉眼可见。");
        // 均匀白噪声的理论标准差 ≈ 73.6；明显低于此值说明被换成了偏纯色/低对比纹理
        Assert.True(sd >= 40, $"噪声源标准差只有 {sd:F1}（理论 ≈73.6）—— 疑似退化为低对比纹理，打散色阶带的能力不足。");
        // 低透明度必须体现在 alpha 通道（约 2-3%）；若接近 255 说明又回退到 Rectangle.Opacity 方案
        Assert.InRange(alphaMean, 3, 40);
    }
}
