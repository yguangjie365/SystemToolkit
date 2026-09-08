using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 在线封面缩略图内存缓存（URL 键控；进程级）。列表虚拟化来回滚动时命中缓存不重下载。
/// </summary>
internal static class CoverThumbCache
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new();

    public static bool TryGet(string url, out BitmapSource? image) => Cache.TryGetValue(url, out image);

    public static void Store(string url, BitmapSource? image) => Cache[url] = image;
}
