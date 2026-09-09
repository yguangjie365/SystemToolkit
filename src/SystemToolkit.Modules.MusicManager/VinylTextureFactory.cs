using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 彩胶盘纹理工厂（NexBox VinylDisc 复刻，2026-09-09）。
/// Web 版用 SVG feTurbulence 湍流位移 + CSS repeating-radial/conic-gradient；
/// WPF 无对应能力，故运行时 <see cref="RenderTargetBitmap"/> 预渲染两张 700×700 纹理：
/// ① GrooveSource：唱片细纹（1px/6px 同心环）+ 7 道宽弧带（噪声径向抖动近似湍流）
///    + 双 radial 明暗斑驳——随盘旋转；
/// ② HighlightSource：conic 反光近似（分扇形白色渐变，215° 起三段）——固定不随盘转。
/// 模块生命周期内只生成一次（线程安全：由 View 构造（STA）触发）。
/// </summary>
public static class VinylTextureFactory
{
    private const int Size = 700;
    private static BitmapSource? _groove;
    private static BitmapSource? _highlight;

    /// <summary>随盘旋转的盘体纹理（细纹 + 湍流弧带 + 斑驳高光/阴影）。</summary>
    public static ImageSource GrooveSource => _groove ??= RenderGroove();

    /// <summary>固定不转的 conic 反光层（环境光高光）。</summary>
    public static ImageSource HighlightSource => _highlight ??= RenderHighlight();

    private static BitmapSource RenderGroove()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double cx = Size / 2.0, cy = Size / 2.0;

            // 1) 唱片细纹：1px 白 0.09 每 6px 一道（对照 repeating-radial-gradient）
            double r = 0;
            while (r < Size / 2.0)
            {
                dc.DrawEllipse(null, Fin(1.0, 0.09), new Point(cx, cy), r, r);
                r += 6;
            }

            // 2) 七道宽弧带（对照 bands 表）：宽度 16–30，透明度 0.08–0.11；
            //    feTurbulence 位移用"分段圆弧 + 伪随机径向抖动"近似——每 6° 一段，
            //    半径叠加平滑噪声（两层正弦叠加模拟低频湍流），视觉等效弯曲明暗胶纹
            (double Radius, bool White, double Width, double Opacity)[] bands =
            [
                (60, true, 26, 0.10), (105, false, 18, 0.08), (150, true, 30, 0.11),
                (205, false, 16, 0.08), (245, true, 24, 0.10), (290, false, 20, 0.08),
                (330, true, 22, 0.09),
            ];
            foreach ((double bandRadius, bool white, double width, double opacity) in bands)
            {
                Color color = white ? Colors.White : Colors.Black;
                var pen = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
                pen.Freeze();

                // 分段折线（极坐标采样 + 双频正弦径向抖动，seed 固定保证每次生成一致）
                const int steps = 120;
                StreamGeometry geo = new();
                using (StreamGeometryContext g = geo.Open())
                {
                    Point? prev = null;
                    for (int i = 0; i <= steps; i++)
                    {
                        double ang = i * 2 * Math.PI / steps;
                        // 双频正弦位移（近似 feTurbulence baseFrequency 0.005/0.018 的低频扰动）
                        double jitter =
                            (Math.Sin(ang * 3.0 + bandRadius) * 4.0)
                            + (Math.Sin(ang * 7.0 - bandRadius * 0.7) * 2.2);
                        double rr = bandRadius + jitter;
                        Point p = new(cx + rr * Math.Cos(ang), cy + rr * Math.Sin(ang));
                        if (prev is null)
                        {
                            g.BeginFigure(p, false, false);
                        }
                        else
                        {
                            g.LineTo(p, true, false);
                        }

                        prev = p;
                    }
                }

                geo.Freeze();
                dc.DrawGeometry(null, new Pen(pen, width), geo);
            }

            // 3) 双 radial 明暗斑驳（对照两个 radial-gradient 高光/阴影）
            AddRadialBlob(dc, cx + Size * (-0.08), cy + Size * (-0.11), Size * 0.46, Colors.White, 0.14);
            AddRadialBlob(dc, cx + Size * (0.08), cy + Size * (0.11), Size * 0.52, Colors.Black, 0.05);
        }

        return Freeze(visual);
    }

    private static BitmapSource RenderHighlight()
    {
        // conic-gradient(from 215deg, ...) 三段反光的扇形近似：
        // 每段用线性渐变扇形（角度中点法向），透明度按 NexBox 停靠表
        (double StartDeg, double SweepDeg, double Opacity)[] wedges =
        [
            (215, 34, 0.15), (249, 38, 0.04), (287, 50, 0.10), (337, 45, 0.0),
        ];

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double cx = Size / 2.0, cy = Size / 2.0, rr = Size / 2.0;
            foreach ((double startDeg, double sweepDeg, double opacity) in wedges)
            {
                if (opacity <= 0)
                {
                    continue;
                }

                double midRad = (startDeg + sweepDeg / 2) * Math.PI / 180;
                // 扇形渐变方向：沿角度中点的法线由透明到白
                var brush = new LinearGradientBrush(
                    Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb((byte)(opacity * 255), 255, 255, 255),
                    new Point(cx, cy),
                    new Point(cx + rr * Math.Cos(midRad), cy + rr * Math.Sin(midRad)));
                brush.Freeze();

                StreamGeometry geo = new();
                using (StreamGeometryContext g = geo.Open())
                {
                    double a0 = startDeg * Math.PI / 180;
                    double a1 = (startDeg + sweepDeg) * Math.PI / 180;
                    Point p0 = new(cx, cy);
                    Point p1 = new(cx + rr * Math.Cos(a0), cy + rr * Math.Sin(a0));
                    Point p2 = new(cx + rr * Math.Cos(a1), cy + rr * Math.Sin(a1));
                    g.BeginFigure(p0, true, false);
                    g.LineTo(p1, true, false);
                    // 扇形弧
                    g.ArcTo(p2, new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
                    g.LineTo(p0, true, false);
                }

                geo.Freeze();
                dc.DrawGeometry(brush, null, geo);
            }
        }

        return Freeze(visual);
    }

    private static void AddRadialBlob(DrawingContext dc, double cx, double cy, double radius, Color color, double opacity)
    {
        var brush = new RadialGradientBrush(
            Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B))
        {
            Center = new Point(cx / Size, cy / Size),
            GradientOrigin = new Point(cx / Size, cy / Size),
            RadiusX = radius / Size,
            RadiusY = radius / Size,
        };
        brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(0, 0, Size, Size));
    }

    private static Pen Fin(double thickness, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255));
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static BitmapSource Freeze(DrawingVisual visual)
    {
        var bmp = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
