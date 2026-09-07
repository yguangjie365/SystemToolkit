namespace SystemToolkit.Core.Overview.Models;

/// <summary>已安装程序条目（来自注册表 Uninstall 键）。</summary>
public sealed class InstalledProgram
{
    /// <summary>初始化条目（参数名不可随意改动，见 name 参数说明）。</summary>
    /// <param name="name">
    /// 参数名不可随意改动：磁盘缓存（%LOCALAPPDATA%\SystemToolkit\overview-cache.json）
    /// 靠 System.Text.Json 的「参数化构造」还原，要求每个构造参数名都能匹配到一个
    /// 属性名（大小写不敏感）。改名后旧缓存会<b>反序列化抛异常</b>，被降级为冷启动
    /// 采集（慢但结果正确）。改动前请先看 OverviewViewModelTests 的缓存契约用例。
    /// </param>
    /// <param name="version">版本号，可空。</param>
    /// <param name="publisher">发布者，可空。</param>
    /// <param name="installedOn">安装日期（yyyy-MM-dd），可空。</param>
    /// <param name="size">占用空间（格式化后），可空。</param>
    public InstalledProgram(string name, string? version, string? publisher, string? installedOn, string? size)
    {
        Name = name;
        Version = version;
        Publisher = publisher;
        InstalledOn = installedOn;
        Size = size;
    }

    /// <summary>程序显示名（必填，注册表无 DisplayName 的条目会被过滤）。</summary>
    public string Name { get; }

    /// <summary>版本号。</summary>
    public string? Version { get; }

    /// <summary>发布者。</summary>
    public string? Publisher { get; }

    /// <summary>安装日期（yyyy-MM-dd）。</summary>
    public string? InstalledOn { get; }

    /// <summary>占用空间（格式化后，如 “1.2 GB”）。</summary>
    public string? Size { get; }
}
