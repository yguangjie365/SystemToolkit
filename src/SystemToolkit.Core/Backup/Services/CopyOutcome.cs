using SystemToolkit.Core.Backup.Models;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>
/// 并发复制的产出汇总（B5b-③ 起把"复用 / 回退"计数一并带出，避免调用方再从条目里反推）。
/// </summary>
/// <param name="Entries">写入清单的条目（真实复制的与复用上一份快照的都在内）。</param>
/// <param name="Failures">失败明细（相对路径 + 原因）。</param>
/// <param name="ReusedCount">复用上一份快照、**本次未读取源文件**的条目数。</param>
/// <param name="LinkFallbackCount">
/// 判定可复用但硬链接失败、已回退为真实复制的条目数。
/// 🔴 单独计数是为了让"硬链接不可用"（跨卷 / FAT32 目标盘 / 旧副本被删）
/// **可见**——否则表现为"设了开启却还是那么慢"，属于静默失效。
/// </param>
internal sealed record CopyOutcome(
    List<FileEntry> Entries,
    List<string> Failures,
    int ReusedCount,
    int LinkFallbackCount);
