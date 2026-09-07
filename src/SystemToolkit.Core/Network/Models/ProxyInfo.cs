namespace SystemToolkit.Core.Network.Models;

/// <summary>系统代理（WinINET / IE 代理）状态。</summary>
public sealed record ProxyInfo(bool Enabled, string? Server);

/// <summary>
/// <see cref="ProxyInfo"/> ↔ HKCU「Internet Settings」注册表值的<b>纯映射</b>。
/// 刻意抽成无副作用静态类：注册表读写本身是系统边界不做单测，
/// 但值的换算逻辑（int ↔ bool、空白服务器串归一为 null）必须可测。
/// </summary>
public static class ProxyRegistryMapping
{
    /// <summary>ProxyEnable 注册表值（REG_DWORD）：1 启用 / 0 停用。</summary>
    public static int ToEnableValue(bool enabled) => enabled ? 1 : 0;

    /// <summary>从注册表两个值还原代理状态；服务器串为空白时归一为 null。</summary>
    public static ProxyInfo FromValues(int proxyEnable, string? proxyServer)
    {
        string? server = string.IsNullOrWhiteSpace(proxyServer) ? null : proxyServer!.Trim();
        return new ProxyInfo(proxyEnable != 0, server);
    }
}
