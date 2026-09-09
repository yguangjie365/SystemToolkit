using System.Windows.Media.Imaging;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 在线封面缩略图内存缓存（URL 键控；进程级）。列表虚拟化来回滚动时命中缓存不重下载。
/// 审查 O14（2026-09-10）：原为无界 ConcurrentDictionary——打开若干歌单即累积数百张全量位图常驻、
/// 永不释放（数百 MB）。改为有界 LRU：命中提升、超上限淘汰最旧，锁保护（封面加载可并发）。
/// </summary>
internal static class CoverThumbCache
{
    private const int Capacity = 300;
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource? Image)>> Map = new();
    private static readonly LinkedList<(string Key, BitmapSource? Image)> Lru = new();
    private static readonly object Gate = new();

    public static bool TryGet(string url, out BitmapSource? image)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(url, out LinkedListNode<(string Key, BitmapSource? Image)>? node))
            {
                // 命中 → 移到最新端
                Lru.Remove(node);
                Lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    public static void Store(string url, BitmapSource? image)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(url, out LinkedListNode<(string Key, BitmapSource? Image)>? existing))
            {
                existing.Value = (url, image);
                Lru.Remove(existing);
                Lru.AddFirst(existing);
                return;
            }

            Lru.AddFirst((url, image));
            Map[url] = Lru.First!;

            while (Map.Count > Capacity)
            {
                LinkedListNode<(string Key, BitmapSource? Image)> last = Lru.Last!;
                Lru.RemoveLast();
                Map.Remove(last.Value.Key);
            }
        }
    }
}
