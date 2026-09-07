namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// netsh 写操作：IP / DHCP / DNS / 适配器启停。所有方法逐行回传输出（含命令回显），
/// 供 VM 挂接 LogPanel；返回最后一个 netsh 的退出码（0 成功）。
/// </summary>
public interface INetConfigService
{
    /// <summary>适配器恢复 DHCP 自动获取 IP。</summary>
    Task<int> SetDhcpAsync(string adapter, Action<string> onLine);

    /// <summary>配置静态 IP；网关可为 null（无网关静态配置）。</summary>
    Task<int> SetStaticIpAsync(string adapter, string ip, string mask, string? gateway, Action<string> onLine);

    /// <summary>设置 DNS。primary 为 null → 恢复自动（DHCP）；secondary 可选。</summary>
    Task<int> SetDnsAsync(string adapter, string? primary, string? secondary, Action<string> onLine);

    /// <summary>启用 / 禁用适配器。</summary>
    Task<int> SetAdapterEnabledAsync(string adapter, bool enabled, Action<string> onLine);
}
