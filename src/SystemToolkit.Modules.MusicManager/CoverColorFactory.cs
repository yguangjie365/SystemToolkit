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

    /// <summary>位图 → 主色刷（下采样像素后走 <see cref="PaletteMath"/>；冻结以便多线程安全）。</summary>
    public static SolidColorBrush FromBitmap(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        byte[] pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        PaletteMath.Rgb rgb = PaletteMath.PickDominant(pixels, source.PixelWidth * source.PixelHeight);
        return Create(rgb.R, rgb.G, rgb.B);
    }

    private static SolidColorBrush Create(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
