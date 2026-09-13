using Windows.Win32;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>
/// 文件硬链接（B5b-③：把上一份快照里「未变文件」的副本复用进本次快照）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **为什么必须是硬链接，而不是符号链接**：符号链接指向**上一份快照的路径**，而保留策略
/// （GFS / 固定条数）随后就会删除旧快照 —— 删完之后链接指向空洞：用户以为有备份、双击打不开
/// = **静默丢数据**。硬链接与目标共享同一份数据（同一 inode），删掉旧快照目录后数据依然存活。
/// </para>
/// <para>
/// 🔴 **.NET 10 没有 <c>File.CreateHardLink</c>**（经引用程序集文档核实：只有 <c>CreateSymbolicLink</c>），
/// 故走 CsWin32 生成的 <c>PInvoke.CreateHardLink</c>（KERNEL32，签名由生成器给出，
/// 标注 <c>windows5.1.2600</c>，**比本类标注的 6.0.6000 更宽**，不会触发 CA1416）。
/// </para>
/// <para>
/// **失败一律返回 false**，绝不抛异常：调用方必须回退为真实复制。
/// <c>CreateHardLinkW</c> 是原子的——要么建好链接，要么什么都没留下，不存在半个链接。
/// </para>
/// <para>
/// 🔴 **本类刻意不做 <c>[SupportedOSPlatform]</c> 标注**：`SystemToolkit.Core` 编译目标是
/// <c>net10.0</c>（无平台上下文），如果这里标注 <c>windows6.0.6000</c>，CA1416 会要求
/// <c>BackupService</c> / <c>IBackupService.BackupRuleAsync</c> 一路标注上去，把平台约束
/// 泄漏到整个公开 API 面。改为**运行期守卫**（<c>IsWindowsVersionAtLeast</c>，分析器认这一形态）
/// —— 语义上也更准确：硬链接不可用时本就该返回 false 并回退，而不是让调用方编译不过。
/// </para>
/// </remarks>
internal static class FileLinker
{
    /// <summary>
    /// 为已存在的文件在指定路径建立一个硬链接。
    /// </summary>
    /// <param name="linkPath">新链接的路径（其父目录必须已存在）。</param>
    /// <param name="existingPath">已存在的文件（复用来源）。</param>
    /// <returns>
    /// 成功返回 true；**任何失败情形（来源缺失、目标已存在、跨卷、文件系统不支持硬链接、
    /// 权限不足、路径过长、系统版本不支持）一律返回 false**，由调用方回退为真实复制。
    /// </returns>
    internal static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            // 运行期守卫（同时满足 CA1416）：硬链接从 Windows 2000/XP 起就有，这里按本仓统一的
            // 最低支持版本（Vista 6.0.6000）判定；更低版本不可能承载本应用，按"不支持"回退即可。
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
            {
                return false;
            }

            // 先自己挡掉两种可预期的失败，避免无谓的 P/Invoke（也便于区分"来源没了"与"系统不支持"）
            if (!File.Exists(existingPath) || File.Exists(linkPath))
            {
                return false;
            }

            return PInvoke.CreateHardLink(linkPath, existingPath);
        }
        catch
        {
            // 路径过长 / 权限 / 文件系统不支持 …：一律回退"真实复制"，正确性由调用方保证
            return false;
        }
    }
}
