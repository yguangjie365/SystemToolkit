using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SystemToolkit.Modules.GameManager;

/// <summary>
/// 封面路径 → 限宽解码的 <see cref="BitmapImage"/>（REVIEW-3 S-4）。
/// <para>
/// 旧实现直绑 string 路径：WPF 按原始尺寸解码（library_600x900_2x.jpg 为 1200×1800，
/// 卡片只需 ~480 物理像素宽 → 4 倍冗余内存），且代码注释声称"按 300 宽解码"与实现相悖。
/// 本转换器强制 <c>DecodePixelWidth</c> 等比降采样；<c>CacheOption=OnLoad</c> 立即读完
/// 文件字节，不长期占用 Steam librarycache 文件句柄。
/// </para>
/// </summary>
public sealed class CoverImageConverter : IValueConverter
{
    /// <summary>解码目标宽度（物理像素）：卡片宽 320 逻辑 × 1.5 DPI ≈ 480。</summary>
    public int DecodeWidth { get; set; } = 480;

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze(); // 跨线程安全 + 释放解码器资源
            return bmp;
        }
        catch
        {
            // 坏图/文件被占用 → 返回 null 走字母占位（封面缺失不应打扰用户）
            return null;
        }
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
