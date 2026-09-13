namespace SystemToolkit.Core.Utilities;

/// <summary>磁盘空间检查结果。</summary>
public enum DiskSpaceCheck
{
    /// <summary>空间充足。</summary>
    Enough,

    /// <summary>空间不足。</summary>
    Insufficient,

    /// <summary>
    /// 无法确定：驱动器未就绪、UNC 网络路径、权限不足或路径非法等。
    /// 与“充足”严格区分——调用方不应把它当作放行信号。
    /// </summary>
    Unknown,
}

/// <summary>磁盘剩余空间预检工具，供备份/导出等大写入量操作在执行前把关。</summary>
public static class DiskSpaceUtil
{
    /// <summary>
    /// 检查目标路径所在驱动器的剩余空间是否足够。
    /// <para>
    /// 关键：无法判定时返回 <see cref="DiskSpaceCheck.Unknown"/> 而不是 true。
    /// 此前任何异常都 <c>return true</c>，导致备份到网络共享或未就绪驱动器时
    /// 空间预检形同虚设，往往复制到一半才发现空间耗尽。
    /// </para>
    /// </summary>
    /// <param name="path">目标路径（取其所在驱动器根）。</param>
    /// <param name="neededBytes">所需字节数。</param>
    /// <param name="marginMb">额外留出的安全余量（MB）。</param>
    public static DiskSpaceCheck Check(string path, long neededBytes, int marginMb = 128)
    {
        try
        {
            string pathRoot = Path.GetPathRoot(Path.GetFullPath(path))!;
            if (string.IsNullOrEmpty(pathRoot))
            {
                return DiskSpaceCheck.Unknown;
            }

            var driveInfo = new DriveInfo(pathRoot);
            if (!driveInfo.IsReady)
            {
                return DiskSpaceCheck.Unknown;
            }

            // 🔴 饱和加法（2026-09-13）：neededBytes 可能来自**对端声明**（文件互传的 FileSize），
            // 一个 long.MaxValue 级别的荒谬值会让 neededBytes + margin 溢出成负数 →
            // "剩余空间 > 负数" 恒真 = 误判充足，预检形同虚设。饱和到 long.MaxValue 让它必然判不足。
            // 负数同样按 0 处理：否则 required 可能为负，同样得到永恒的"充足"。
            long need = neededBytes > 0 ? neededBytes : 0;
            long margin = (long)marginMb * 1024 * 1024;
            long required = need > long.MaxValue - margin ? long.MaxValue : need + margin;
            return driveInfo.AvailableFreeSpace > required
                ? DiskSpaceCheck.Enough
                : DiskSpaceCheck.Insufficient;
        }
        catch
        {
            return DiskSpaceCheck.Unknown;
        }
    }

    /// <summary>
    /// 取目标路径所在卷的剩余空间（字节）；无法判定时返回 null。
    /// <para>
    /// 与 <see cref="Check"/> 的分工：<c>Check</c> 回答"够不够"，本方法回答"到底还剩多少"——
    /// 确认门弹窗要如实显示数字（"剩余 62.4 GB"），只给"充足/不足"不足以让人判断。
    /// 同样是**无法判定即 null**，不得用 0 冒充"没有空间"。
    /// </para>
    /// </summary>
    public static long? TryGetAvailableFreeBytes(string path)
    {
        try
        {
            string pathRoot = Path.GetPathRoot(Path.GetFullPath(path))!;
            if (string.IsNullOrEmpty(pathRoot))
            {
                return null;
            }

            var driveInfo = new DriveInfo(pathRoot);
            return driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 兼容封装：仅在明确判定不足时返回 false。
    /// <see cref="DiskSpaceCheck.Unknown"/> 时返回 true（不阻断备份/恢复），
    /// 需要区分“无法确认”的调用方请改用 <see cref="Check"/>。
    /// </summary>
    public static bool HasEnoughSpace(string path, long neededBytes, int marginMb = 128)
        => Check(path, neededBytes, marginMb) != DiskSpaceCheck.Insufficient;
}
