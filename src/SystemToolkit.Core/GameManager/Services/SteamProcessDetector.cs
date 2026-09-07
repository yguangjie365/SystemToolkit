using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 进程检测器：<c>IsRunning()</c>（对应 NexBox sysinfo::System.get_process_by_name "steam"）。
/// 进程名比较不区分大小写；注意 Steam 客户端主进程名就是 "steam.exe"。
/// </summary>
internal static class SteamProcessDetector
{
    /// <summary>本机是否存在正在运行的 steam.exe 进程（不计大小写）。</summary>
    public static bool IsRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Any();
        }
        catch (InvalidOperationException) { /* 瞬时被销毁 */ }
        catch (PlatformNotSupportedException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>返回主安装路径下 steam.exe 完整路径，否则 null。</summary>
    [return: NotNullIfNotNull(nameof(installPath))]
    public static string? GetSteamExePath(string? installPath)
    {
        if (string.IsNullOrEmpty(installPath))
            return null;
        return System.IO.Path.Combine(installPath, "steam.exe");
    }

    /// <summary>
    /// 关闭 Steam：taskkill /F /IM steam.exe + 循环最多 20 次 × 500ms 等退出，最多 10s。
    /// 返回 true 表示确认全退（或压根没在跑），false 表示超时仍有残留。
    /// </summary>
    public static bool KillSteamAndWait(int maxRetries = 20, int delayMs = 500)
    {
        if (!IsRunning())
            return true;
        try
        {
            using var psi = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/F /IM steam.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            psi.Start();
            psi.WaitForExit(milliseconds: 3000);
        }
        catch
        {
            // taskkill 失败也走 polling 等退出
        }
        for (int i = 0; i < Math.Max(1, maxRetries); i++)
        {
            if (!IsRunning())
                return true;
            System.Threading.Thread.Sleep(delayMs);
        }
        return !IsRunning();
    }

    /// <summary>
    /// 启动 Steam（可选带命令行参数）。不等待启动完成，立即返回 Process.Start 结果。
    /// </summary>
    public static Process? LaunchSteam(string steamExePath, string? args = null)
    {
        if (!System.IO.File.Exists(steamExePath))
            return null;
        var psi = new ProcessStartInfo
        {
            FileName = steamExePath,
            Arguments = args ?? string.Empty,
            UseShellExecute = true, // 使用操作系统登录态启动；否则 UAC 可能被触发
        };
        return Process.Start(psi);
    }
}
