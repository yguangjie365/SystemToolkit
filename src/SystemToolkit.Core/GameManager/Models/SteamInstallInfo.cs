namespace SystemToolkit.Core.GameManager.Models;

/// <summary>Steam 安装与运行态信息。</summary>
public sealed record SteamInstallInfo
{
    /// <summary>是否检测到 Steam 客户端已安装（注册表有 SteamPath / InstallPath）。</summary>
    public bool Installed { get; init; }

    /// <summary>安装目录绝对路径（如 "C:\\Program Files (x86)\\Steam"）。未安装时为 null。</summary>
    public string? InstallPath { get; init; }

    /// <summary>当前是否存在 steam.exe 进程（进程名不区分大小写比较）。</summary>
    public bool IsRunning { get; init; }
}
