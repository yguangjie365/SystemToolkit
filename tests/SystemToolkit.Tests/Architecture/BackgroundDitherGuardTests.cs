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
/// 播放器背景 dither 守卫（2026-09-19 建立）。
/// <para>
/// 背景三段渐变（<see cref="CoverColorFactory.VinylBackgroundGradient"/> 等）总跨度仅约 10 级、
/// 却横跨全屏 ⇒ 每级跨约 120px 的量化台阶会被人眼 Mach band 强化成可见的**斜向条纹**。
/// 正解是叠一层极低不透明度的噪声打断硬边。
/// </para>
/// <para>本守卫锁两件事：① 背景层确实挂了噪声层且配置正确；② 噪声源确实是随机分布（没被换成纯色）。</para>
/// </summary>
public sealed class BackgroundDitherGuardTests
{
    private const string XamlRel = @"src\SystemToolkit.Modules.MusicManager\MusicManagerView.xaml";

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

        Match m = Regex.Match(block, "Opacity=\"([0-9.]+)\"");
        Assert.True(m.Success, "dither 噪声层缺少 Opacity（观感旋钮）。");
        double opacity = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(opacity, 0.005, 0.10);
    }

    [Fact]
    public void NoiseSource_MustBeRandomNotFlat()
    {
        Exception? err = null;
        double sd = 0;
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
                int n = w * h;
                for (int i = 0; i < px.Length; i += 4)
                {
                    sum += px[i];
                }

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
    }
}
