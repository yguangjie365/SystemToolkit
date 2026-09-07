namespace SystemToolkit.Core.Software.Models;

/// <summary>环境软件清单中的 winget 包条目。</summary>
public sealed class WingetPackage
{
    /// <summary>winget 包 ID（如 Microsoft.VisualStudioCode）。</summary>
    public string Id { get; set; } = "";

    /// <summary>软件显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话简介。</summary>
    public string Description { get; set; } = "";

    /// <summary>分类名（默认"其他"）。</summary>
    public string Category { get; set; } = "其他";

    /// <summary>图标（本地路径资源）。</summary>
    public string Icon { get; set; } = "";

    /// <summary>来源源名：winget 或 msstore。</summary>
    public string Source { get; set; } = "winget";

    /// <summary>是否为 Microsoft Store 应用。</summary>
    public bool IsMsStore => string.Equals(Source, "msstore", StringComparison.OrdinalIgnoreCase);
}
