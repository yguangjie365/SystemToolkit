namespace SystemToolkit.Core.Network.Models;

/// <summary>一条 DNS 预设：主/备均为 null 表示「自动（DHCP）」。</summary>
public sealed record DnsPreset(string Name, string? Primary, string? Secondary);

/// <summary>
/// 内置 DNS 预设清单（代码常量，不落 JSON）。
/// 排序原则（设计文档 §3）：大陆可用性优先——自动/阿里/DNSPod/114 在前，
/// Google / Cloudflare 在大陆环境常被干扰，移到末尾并在名称里注明。
/// </summary>
public static class DnsPresets
{
    /// <summary>内置预设清单（顺序即 UI 展示顺序：大陆可用性优先，境外 DNS 置底并注明连通性差异）。</summary>
    public static readonly IReadOnlyList<DnsPreset> BuiltIn = new[]
    {
        new DnsPreset("自动（DHCP）", null, null),
        new DnsPreset("阿里 DNS", "223.5.5.5", "223.6.6.6"),
        new DnsPreset("DNSPod", "119.29.29.29", "119.29.28.28"),
        new DnsPreset("114DNS", "114.114.114.114", "114.114.115.115"),
        new DnsPreset("Google（连通性因地而异）", "8.8.8.8", "8.8.4.4"),
        new DnsPreset("Cloudflare（连通性因地而异）", "1.1.1.1", "1.0.0.1"),
    };
}
