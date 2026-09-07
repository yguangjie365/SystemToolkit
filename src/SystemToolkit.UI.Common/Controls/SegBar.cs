using System.Windows;
using System.Windows.Media;

namespace SystemToolkit.UI.Common.Controls;

/// <summary>
/// 分段式进度条（参考仪表盘的 seg-bar）：把 0-100 的值切成 N 段小块，按比例点亮。
/// 自绘控件（OnRender），无模板依赖，避免第三方隐式样式渗透（设计系统纪律）。
/// </summary>
public class SegBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SegBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(SegBar),
        new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsRender),
        v => Convert.ToInt32(v) > 0);

    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(SegBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SegBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>当前值（0-100）。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>分段数。</summary>
    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>点亮段画刷（令牌色）。</summary>
    public Brush? ActiveBrush
    {
        get => (Brush?)GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    /// <summary>未点亮段画刷（轨道色）。</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private const double Gap = 2.0;

    static SegBar()
    {
        // 基线高度：未显式设置 Height 时给一个可用默认
        HeightProperty.OverrideMetadata(typeof(SegBar), new FrameworkPropertyMetadata(7.0));
    }

    // OnRender 依赖 ActualWidth，尺寸变化不会触发 AffectsRender——须显式重绘（审查 L13：
    // 否则窗口缩放期间按旧宽绘制，Overview 场景被 2s 刷新掩盖，静态值场景永久错位）
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0 || Segments <= 0)
        {
            return;
        }

        Brush track = TrackBrush ?? Brushes.Transparent;
        Brush active = ActiveBrush ?? Brushes.Gray;
        double segWidth = Math.Max(1.0, (width - Gap * (Segments - 1)) / Segments);
        double radius = 1.5;
        int litCount = (int)Math.Round(Clamp(Value, 0, 100) / 100.0 * Segments);

        for (int i = 0; i < Segments; i++)
        {
            Brush brush = i < litCount ? active : track;
            var rect = new Rect(i * (segWidth + Gap), 0, segWidth, height);
            dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
        }
    }

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
}
