using System.Runtime.InteropServices;

namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 当前用户的常用 Shell 文件夹路径（跨模块共用，避免各模块各自拼路径）。
/// <para>
/// 【为何不直接用 <c>%USERPROFILE%\Downloads</c>】用户可以在「下载 → 属性 → 位置」
/// 把它重定向到任意盘任意目录，硬拼会拿到一个并不存在的路径；
/// 因此优先走 Shell 已知文件夹 API（FOLDERID_Downloads）取<b>真实</b>位置。
/// </para>
/// <para>
/// 【为何不标 <c>[SupportedOSPlatform("windows")]</c>】本类要被 Core 内部（TFM 为跨平台 net10.0 的
/// <c>FileTransferService</c>）调用，标注后按 CA1416 会报「在所有平台可达的调用点调用了仅 Windows 支持的方法」
/// （Release 是警告即错误，直接构建失败）。改为内部用 <c>OperatingSystem.IsWindows()</c> 运行时守卫，
/// 与 <c>ConfigService</c> 现有写法一致。
/// </para>
/// </summary>
public static class UserFolders
{
    /// <summary>FOLDERID_Downloads（Windows SDK KnownFolders.h）。</summary>
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// 当前用户的「下载」文件夹（文件互传的默认共享根目录）。
    /// <para>
    /// 三级兜底：Shell 已知文件夹 → <c>%USERPROFILE%\Downloads</c> → 桌面。
    /// 保证任何情况下都返回可用路径，调用方无需再做空值判断。
    /// </para>
    /// </summary>
    public static string GetDownloadsFolder()
    {
        if (OperatingSystem.IsWindows()
            && TryGetKnownFolderPath(DownloadsFolderId, out string? known)
            && !string.IsNullOrWhiteSpace(known))
        {
            return known;
        }

        // 注意：历史教训——若 Core 下再出现 Environment 同名命名空间，裸写 Environment 会被抢先解析，
        // 必须全限定为 System.Environment。
        string profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return Path.Combine(profile, "Downloads");
        }

        return System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
    }

    /// <summary>
    /// 调用 SHGetKnownFolderPath 取已知文件夹真实路径。
    /// <para>任何失败（非 Windows / API 不可用 / 内存分配失败）都返回 <c>false</c>，由调用方兜底。</para>
    /// </summary>
    private static bool TryGetKnownFolderPath(Guid folderId, out string? path)
    {
        path = null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // 0 = KF_FLAG_DEFAULT；hToken 传 IntPtr.Zero 表示当前用户
            int hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out buffer);
            if (hr != 0 || buffer == IntPtr.Zero)
            {
                return false;
            }

            path = Marshal.PtrToStringUni(buffer);
            return !string.IsNullOrWhiteSpace(path);
        }
        catch
        {
            // P/Invoke 属系统边界，失败即退化，不上抛（调用方有兜底路径）
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
