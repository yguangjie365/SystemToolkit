namespace SystemToolkit.Core.Software.Models;

/// <summary>手工维护的软件条目：不经 winget 安装，仅作清单展示与下载/安装入口。</summary>
public sealed class ManualSoftware
{
    /// <summary>软件名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话简介。</summary>
    public string Description { get; set; } = "";

    /// <summary>分类名（默认"其他"）。</summary>
    public string Category { get; set; } = "其他";

    /// <summary>图标（本地路径资源）。</summary>
    public string Icon { get; set; } = "";

    /// <summary>官方下载地址（可空，供跳转下载）。</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>本地安装包路径（可空，已下载时直接指向安装文件）。</summary>
    public string LocalInstallerPath { get; set; } = "";
}
