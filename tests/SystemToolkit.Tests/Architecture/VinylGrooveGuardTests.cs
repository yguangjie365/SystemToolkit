using System;
using System.Globalization;
using System.Threading;
using System.Windows.Media.Imaging;
using SystemToolkit.Modules.MusicManager;
using Xunit;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 彩胶盘「细纹层」守卫（2026-09-19 建立）。
/// <para>
/// 背景：<see cref="VinylTextureFactory.GrooveSource"/> 原含一层「细纹」（1px 白 0.09、每 6px 一道）。
/// 纹理按 700×700 @96DPI 生成 ⇒ 逻辑 700 DIP；实机盘径 720 DIP @150% DPI ⇒ 物理 1080px、
/// 纹理放大 1.54× ⇒ 细纹周期由 6 变 <b>9.4px</b>、对比约 <b>9 luma</b> ⇒ 人眼（Mach band）读作
/// <b>条纹</b>而非"质感"；且封面圆（占盘径 70%）内被照片遮盖，只有外圈（半径 70%~100%）显露出来
/// —— 恰是最显眼处。用户实机反馈"唱片周围还有一些条纹"。该层已于 2026-09-19 移除。
/// </para>
/// <para>
/// 判据（测<b>行为</b>而非实现）：沿径向取 alpha 剖面 a[]，比较<b>同相位</b>与<b>半相位</b>的平均差 ——
/// <c>score = mean|a[i]-a[i+6]| − mean|a[i]-a[i+3]|</c>。
/// 存在 6px 细纹时，同相位落在"线上对线上/空白对空白"（差≈0），半相位落在"线上对空白"（差≈α峰值）
/// ⇒ score 显著为负；无细纹时两者皆≈0 ⇒ score≈0。
/// </para>
/// <para>
/// 采样区间取纹理半径 302..315：已避开 7 道宽弧带的硬边（band5 实际 281..301、band6 实际 315..337，
/// 均含 ±6.2 的径向抖动）。
/// </para>
/// </summary>
public sealed class VinylGrooveGuardTests
{
    [Fact]
    public void GrooveTexture_MustNotContainFineGrooveLines()
    {
        Exception? err = null;
        double meanSame = 0;
        double meanHalf = 0;
        double score = 0;
        var t = new Thread(() =>
        {
            try
            {
                ViewLoadSmokeGuardTests.EnsureApplication();
                var bmp = (BitmapSource)VinylTextureFactory.GrooveSource;
                int w = bmp.PixelWidth;
                int h = bmp.PixelHeight;
                byte[] px = new byte[w * h * 4];
                bmp.CopyPixels(px, w * 4, 0);

                int cx = w / 2;
                int cy = h / 2;
                const int r0 = 302;
                const int r1 = 315;
                int n = r1 - r0 + 1;
                double[] a = new double[n];
                for (int i = 0; i < n; i++)
                {
                    int y = cy - (r0 + i);
                    a[i] = px[(((y * w) + cx) * 4) + 3];
                }

                double s6 = 0;
                double s3 = 0;
                int c6 = 0;
                int c3 = 0;
                for (int i = 0; i < n; i++)
                {
                    if (i + 6 < n)
                    {
                        s6 += Math.Abs(a[i] - a[i + 6]);
                        c6++;
                    }

                    if (i + 3 < n)
                    {
                        s3 += Math.Abs(a[i] - a[i + 3]);
                        c3++;
                    }
                }

                meanSame = s6 / c6;
                meanHalf = s3 / c3;
                score = meanSame - meanHalf;
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

        Assert.True(score > -2.5,
            string.Format(CultureInfo.InvariantCulture,
                "GrooveSource 疑似又出现 6px 周期的细纹层：score={0:F2}（期望 > -2.5；"
                + "有细纹时实测约 -7.7）。mean|d6|={1:F2} mean|d3|={2:F2}。"
                + "该层在 150% DPI 的实机盘径下会放大成 9.4px 周期的可见条纹。",
                score, meanSame, meanHalf));
    }
}
