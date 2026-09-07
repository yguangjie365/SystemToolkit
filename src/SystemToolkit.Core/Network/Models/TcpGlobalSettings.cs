namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// TCP 全局参数快照（<c>netsh interface tcp show global</c> 解析 + 注册表补充）。
/// <para>
/// <b>null = 解析失败降级</b>（缺行 / 乱码 / 标签不认识）——UI 显示「未知」，
/// 应用时跳过该项，绝不把「没读到」当成「已禁用」去写（设计文档 §3）。
/// </para>
/// </summary>
public sealed record TcpGlobalSettings(
    string? AutoTuningLevel,
    bool? RssEnabled,
    bool? EcnEnabled,

    // HKLM NetworkThrottlingIndex（注册表值，不属于 show global 输出）：
    // 默认 10（0xA）；0xFFFFFFFF 表示解除网络限流。null = 注册表读取失败。
    uint? NetworkThrottlingIndex,

    // 【M6c·P2-7】show global 其余有价值字段的只读展示（原值字符串，绝不布尔化——
    // RFC1323 的值是 allowed 非 enabled/disabled；「只展示不开放写」，观察一个版本周期再评估）
    string? InitialRto = null,
    string? CongestionProvider = null,
    string? RscState = null,
    string? Rfc1323Timestamps = null)
{
    /// <summary>NetworkThrottlingIndex 的默认值（0xA，Windows 出厂值）。</summary>
    public const uint ThrottlingDefault = 10;

    /// <summary>NetworkThrottlingIndex 的「解除限制」值（0xFFFFFFFF）。</summary>
    public const uint ThrottlingDisabled = 0xFFFFFFFF;
}
