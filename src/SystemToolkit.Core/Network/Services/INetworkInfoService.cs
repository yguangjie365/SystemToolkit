using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>网络信息读取（适配器 + 系统代理）与代理写入。</summary>
public interface INetworkInfoService
{
    /// <summary>枚举适配器（BCL 采集；排除 Loopback / Tunnel 噪声项）。</summary>
    Task<IReadOnlyList<NetAdapterInfo>> GetAdaptersAsync();

    /// <summary>读系统代理（HKCU，无需管理员）。</summary>
    ProxyInfo GetSystemProxy();

    /// <summary>
    /// 写系统代理。内部 = 写注册表 + <b>WinINET 刷新通知</b>（缺后者浏览器不生效）。
    /// </summary>
    void SetSystemProxy(bool enabled, string? server, Action<string>? log = null);
}
