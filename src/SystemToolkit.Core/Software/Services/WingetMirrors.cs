using SystemToolkit.Core.Software.Models;

namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// 常用 winget 镜像源清单。
/// </summary>
/// <remarks>
/// 只登记经过验证、与官方源结构一致的镜像；新增镜像前先确认它提供的是
/// 完整的 source（含索引文件），否则 <c>winget search</c> 会拿到空结果。
/// </remarks>
public static class WingetMirrors
{
    /// <summary>
    /// 中国科学技术大学开源软件镜像（winget 主源 + 字体源）。
    /// </summary>
    public static IReadOnlyList<WingetMirrorSource> Ustc { get; } =
    [
        new WingetMirrorSource("winget", "https://mirrors.ustc.edu.cn/winget-source"),
        new WingetMirrorSource("winget-font", "https://mirrors.ustc.edu.cn/winget-fonts")
    ];

    /// <summary>
    /// 官方默认源名称（「恢复官方源」时逐个执行 <c>source reset</c>）。
    /// 注意：与 <see cref="Ustc"/> 的源名一一对应，避免换源后残留镜像条目。
    /// </summary>
    public static IReadOnlyList<string> DefaultSourceNames { get; } = ["winget", "winget-font"];
}
