namespace SystemToolkit.Core.Software.Models;

/// <summary>环境软件目录：三类清单（winget 源 / 手工软件 / 驱动工具）的集合容器，用于导入导出。</summary>
public sealed class EnvCatalog
{
    /// <summary>winget 源软件清单。</summary>
    public List<WingetPackage> Winget { get; set; } = new List<WingetPackage>();

    /// <summary>手工软件清单（需自行下载或本地安装包）。</summary>
    public List<ManualSoftware> Manual { get; set; } = new List<ManualSoftware>();

    /// <summary>驱动工具清单（安装/更新驱动用的工具型软件）。</summary>
    public List<ManualSoftware> Driver { get; set; } = new List<ManualSoftware>();
}
