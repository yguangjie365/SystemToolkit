using SystemToolkit.Core.GameManager.Models;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 客户端管理服务（聚合 14 条 API，对应 NexBox steam.rs 14 条 Tauri 命令）。
/// <para>所有操作均为纯本地 + 少量可选 HTTP（头像在线兜底），与 Blazor/WPF 模块零耦合。</para>
/// <para>本服务本身无 Windows 互操作以外的平台敏感代码；为避免调用方误误用，
/// 在模块层注册时自行加 [SupportedOSPlatform("windows")] 断言即可。</para>
/// </summary>
public sealed partial class SteamService
{
    // =============== S8 / S9 / S10 / S11 启动/打开 URL ===============
    /// <summary>启动 Steam 客户端（steam.exe）。</summary>
    public bool LaunchClient()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        SteamInstallInfo info = GetInstallInfo();
        if (!info.Installed || info.InstallPath is null)
            return false;
        string exe = SteamProcessDetector.GetSteamExePath(info.InstallPath);
        if (exe is null || !File.Exists(exe))
            return false;
        try
        { SteamProcessDetector.LaunchSteam(exe); return true; }
        catch (Exception e) { _logger.Error("启动 Steam 失败", e); return false; }
    }

    /// <summary>启动游戏（steam://run/{AppId} 协议）。</summary>
    public bool LaunchGame(uint appId)
    {
        if (appId == 0)
            return false;
        return ShellOpen($"steam://run/{appId}");
    }

    /// <summary>打开商店页（浏览器/Steam 内置浏览器）。</summary>
    public bool OpenStorePage(uint appId)
    {
        if (appId == 0)
            return false;
        return ShellOpen($"https://store.steampowered.com/app/{appId}/");
    }

    /// <summary>打开游戏安装目录（资源管理器）。</summary>
    public bool OpenGameFolder(string libraryPath, string installDir)
    {
        if (string.IsNullOrEmpty(libraryPath) || string.IsNullOrEmpty(installDir))
            return false;
        string full = Path.Combine(libraryPath, "steamapps", "common", installDir);
        if (!Directory.Exists(full))
            return false;
        return ShellOpen(full);
    }

    // =============== S13 卸载游戏 ===============
    /// <summary>卸载游戏（steam://uninstall 协议引导 Steam 卸载流程，不直接删文件）。</summary>
    public bool UninstallGame(uint appId)
    {
        if (appId == 0)
            return false;
        return ShellOpen($"steam://uninstall/{appId}");
    }

    // =============== S15 打开库（Shell 协议 steam://open/games，用户侧栏操作触发） ===============
    /// <summary>打开 Steam 库页（steam://open/games）。</summary>
    public bool OpenLibrary()
    {
        try
        {
            return ShellOpen("steam://open/games");
        }
        catch (Exception e) { _logger.Error("打开 Steam 库失败", e); return false; }
    }

    // =============== S16 关闭 Steam（SteamProcessDetector.KillSteamAndWait 的 Facade 封装） ===============
    /// <returns>true = 当前无进程 / 或成功在时限内关闭；false = 进程仍存在。</returns>
    /// <summary>关闭运行中的 Steam（确认式关闭）。</summary>
    public bool KillRunningClient()
    {
        try
        {
            if (!SteamProcessDetector.IsRunning())
            {
                _logger.Info("请求关闭 Steam：当前无运行中的 Steam 进程。");
                return true;
            }
            bool ok = SteamProcessDetector.KillSteamAndWait();
            _logger.Info($"关闭 Steam 结果：{ok}");
            return ok;
        }
        catch (Exception e) { _logger.Error("关闭 Steam 进程异常", e); return false; }
    }

    // =============== S17 进程运行状态（UI 侧显示「运行中/未运行」辅助） ===============
    /// <summary>Steam 客户端是否正在运行。</summary>
    public bool IsClientRunning() => SteamProcessDetector.IsRunning();

}
