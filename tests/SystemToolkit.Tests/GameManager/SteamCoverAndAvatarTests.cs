using SystemToolkit.Core.GameManager.Services;
using SystemToolkit.Core.GameManager.Utilities;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// 封面探测与头像路径守卫。
/// 实测基线（2026-09-05 本机）：封面缓存在 Steam 主目录 appcache\librarycache\{appid}\header.jpg（460×215），
/// 另有 32×32 图标级哈希 jpg 混入——必须按尺寸下限过滤，否则拉伸糊图。
/// </summary>
public class SteamCoverAndAvatarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"stk_cover_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>构造最小合法 JPEG（仅用于尺寸探测：SOI + APP0 + SOF0）。</summary>
    private static byte[] MakeJpeg(int width, int height)
    {
        var ms = new MemoryStream();
        void W(params byte[] b) => ms.Write(b);
        W(0xFF, 0xD8);                                  // SOI
        W(0xFF, 0xE0, 0x00, 0x10);                      // APP0 len=16
        W(0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00);
        W(0xFF, 0xC0, 0x00, 0x11, 0x08);                // SOF0 len=17, precision=8
        W((byte)(height >> 8), (byte)(height & 0xFF));
        W((byte)(width >> 8), (byte)(width & 0xFF));
        W(0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01);
        W(0xFF, 0xD9);                                  // EOI
        return ms.ToArray();
    }

    private static byte[] MakePng(int width, int height)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void W(params byte[] b) => ms.Write(b);
        W(0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52); // IHDR len=13
        W((byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)(width & 0xFF));
        W((byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)(height & 0xFF));
        W(0x08, 0x02, 0x00, 0x00, 0x00);                   // bit depth/color type/…
        return ms.ToArray();
    }

    [Fact]
    public void ReadSize_Jpeg_ParsesWidthHeight()
    {
        string p = Path.Combine(_root, "a.jpg");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(p, MakeJpeg(460, 215));

        (int Width, int Height)? size = ImageSizeProbe.ReadSize(p);

        Assert.NotNull(size);
        Assert.Equal((460, 215), (size.Value.Width, size.Value.Height));
    }

    [Fact]
    public void ReadSize_Png_ParsesWidthHeight()
    {
        string p = Path.Combine(_root, "a.png");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(p, MakePng(600, 900));

        (int Width, int Height)? size = ImageSizeProbe.ReadSize(p);

        Assert.NotNull(size);
        Assert.Equal((600, 900), (size.Value.Width, size.Value.Height));
    }

    [Fact]
    public void ReadSize_NonImageOrMissing_ReturnsNull()
    {
        string p = Path.Combine(_root, "x.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(p, "not an image");

        Assert.Null(ImageSizeProbe.ReadSize(p));
        Assert.Null(ImageSizeProbe.ReadSize(Path.Combine(_root, "不存在.jpg")));
    }

    [Fact]
    public void FindCoverArt_MainAppCacheTakesPriority_ReturnsHeader()
    {
        string steam = Path.Combine(_root, "steam");
        string lib = Path.Combine(_root, "lib");
        string app = Path.Combine(steam, "appcache", "librarycache", "1234");
        Directory.CreateDirectory(app);
        File.WriteAllBytes(Path.Combine(app, "header.jpg"), MakeJpeg(460, 215));

        string? hit = SteamService.FindCoverArt(steam, lib, 1234);

        Assert.NotNull(hit);
        Assert.Equal(Path.Combine(app, "header.jpg"), hit);
    }

    [Fact]
    public void FindCoverArt_LibraryFallback_ReturnsLegacyLayout()
    {
        string steam = Path.Combine(_root, "steam");
        string lib = Path.Combine(_root, "lib");
        string app = Path.Combine(lib, "steamapps", "librarycache", "5678");
        Directory.CreateDirectory(app);
        File.WriteAllBytes(Path.Combine(app, "library_600x900_2x.jpg"), MakeJpeg(600, 900));

        string? hit = SteamService.FindCoverArt(steam, lib, 5678);

        Assert.NotNull(hit);
        Assert.EndsWith("library_600x900_2x.jpg", hit!);
    }

    [Fact]
    public void FindCoverArt_OnlyIconSizedImage_ReturnsNull()
    {
        // 实测场景：4/9 款游戏目录里只有 32×32 的哈希 jpg
        string steam = Path.Combine(_root, "steam");
        string lib = Path.Combine(_root, "lib");
        string app = Path.Combine(steam, "appcache", "librarycache", "9999");
        Directory.CreateDirectory(app);
        File.WriteAllBytes(Path.Combine(app, "06a62ea3a0cb8c177a0e336696d174ceacd003f4.jpg"), MakeJpeg(32, 32));

        Assert.Null(SteamService.FindCoverArt(steam, lib, 9999));
    }

    [Fact]
    public void FindCoverArt_NoCacheDirectory_ReturnsNull()
    {
        string steam = Path.Combine(_root, "steam");
        string lib = Path.Combine(_root, "lib");
        Directory.CreateDirectory(steam);

        Assert.Null(SteamService.FindCoverArt(steam, lib, 4321));
    }
}
