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

            long required = neededBytes + (long)marginMb * 1024 * 1024;
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
    /// 兼容封装：仅在明确判定不足时返回 false。
    /// <see cref="DiskSpaceCheck.Unknown"/> 时返回 true（不阻断备份/恢复），
    /// 需要区分“无法确认”的调用方请改用 <see cref="Check"/>。
    /// </summary>
    public static bool HasEnoughSpace(string path, long neededBytes, int marginMb = 128)
        => Check(path, neededBytes, marginMb) != DiskSpaceCheck.Insufficient;
}
