namespace SystemToolkit.Core.GameManager.Utilities;

/// <summary>
/// 轻量图片尺寸探测（只读文件头，不解码整图、不引第三方/System.Drawing）。
/// 用途：Steam librarycache 里混有 32×32 的图标级 jpg（哈希名），
/// 直接当封面会拉伸糊成一团——按尺寸下限过滤，只取真正的封面图（实测 2026-09-05）。
/// </summary>
public static class ImageSizeProbe
{
    /// <summary>读取 PNG/JPEG 的像素尺寸；不是受支持格式或文件不可读时返回 null。</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[32];
            int read = fs.Read(head);
            if (read < 24)
            {
                return null;
            }

            // PNG: 89 50 4E 47 0D 0A 1A 0A，IHDR 宽高在 16..24（大端）
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            {
                int w = ReadBE(head[16..20]);
                int h = ReadBE(head[20..24]);
                return w > 0 && h > 0 ? (w, h) : null;
            }

            // JPEG: FF D8 开头，扫描 SOF 段取尺寸
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                return ReadJpegSize(fs);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height)? ReadJpegSize(FileStream fs)
    {
        fs.Position = 2;
        Span<byte> seg = stackalloc byte[4];
        while (true)
        {
            int n = fs.Read(seg);
            if (n < 4)
            {
                return null;
            }

            if (seg[0] != 0xFF)
            {
                return null;
            }

            byte marker = seg[1];
            int len = (seg[2] << 8) | seg[3];
            if (len < 2)
            {
                return null;
            }

            // SOF0/1/2/3/5/6/7/9/10/11/13/14/15：段内 高度(2) 宽度(2) 位于 len 之后第 1..5 字节
            bool isSof = marker is >= 0xC0 and <= 0xCF
                && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                Span<byte> sof = stackalloc byte[5];
                if (fs.Read(sof) < 5)
                {
                    return null;
                }

                int h = (sof[1] << 8) | sof[2];
                int w = (sof[3] << 8) | sof[4];
                return w > 0 && h > 0 ? (w, h) : null;
            }

            // 跳到下一 marker
            fs.Position += len - 2;
        }
    }

    private static int ReadBE(Span<byte> b) => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
}
