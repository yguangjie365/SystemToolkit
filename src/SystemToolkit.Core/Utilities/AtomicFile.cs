using System.Collections.Concurrent;

namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 原子文件写入：先写同目录临时文件、再用 <see cref="File.Move(string, string, bool)"/> 覆盖目标。
/// <para>
/// 目的：配置文件（settings / rules / env 清单 / 快照 manifest）若在写入过程中被中断，
/// 非原子写入会留下半截 JSON 导致数据损坏且无法恢复。原子替换可保证目标文件
/// 要么完整保留旧内容、要么完整替换为新内容。
/// </para>
/// <para>此前这套 "写 .tmp + Move 覆盖" 的三行模式在 ConfigService / RuleManager /
/// EnvListService / SnapshotManager 各复制了一份，现统一收敛到此处。</para>
/// </summary>
public static class AtomicFile
{
    // per-path 静态锁：同一目标路径的并发 WriteAllText 串行化，避免
    // "多个线程同时写各自 tmp 再 Move 覆盖同一目标" 造成的丢失更新/IO 冲突。
    // 锁对象按完整路径懒创建（GetOrAdd 原子操作），不同路径互不阻塞。
    private static readonly ConcurrentDictionary<string, object> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>原子写入文本内容；必要时自动创建目标目录。</summary>
    public static void WriteAllText(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 锁必须覆盖"写 tmp + Move 替换"全程，否则两个线程的 Move 仍可能交错
        lock (Locks.GetOrAdd(Path.GetFullPath(path), _ => new object()))
        {
            // tmp 用唯一名：固定名 `path + ".tmp"` 在并发写入同一目标时会互相
            // 覆盖/误删对方的 tmp（一方 IOException 或丢失更新）
            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tmp, contents);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // 写入/替换失败时尽力清理本次残留的 tmp，避免垃圾文件累积；随后原样抛出
                try
                { File.Delete(tmp); }
                catch { /* 清理失败不影响原异常语义 */ }
                throw;
            }
        }
    }

    /// <summary>
    /// 原子写入二进制内容（同样的「唯一 tmp + Move 覆盖」）。
    /// <para>
    /// 🟡 审查 2026-09-10（🟡-16）：用于下载的封面/图标等二进制资源——
    /// 直写目标文件时若中断（网络断、进程退出）会在目标处留下半截图片且无法察觉。
    /// </para>
    /// </summary>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        lock (Locks.GetOrAdd(Path.GetFullPath(path), _ => new object()))
        {
            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(tmp, contents);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try
                { File.Delete(tmp); }
                catch { /* 清理失败不影响原异常语义 */ }
                throw;
            }
        }
    }
}
