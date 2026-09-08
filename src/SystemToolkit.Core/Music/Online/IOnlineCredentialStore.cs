namespace SystemToolkit.Core.Music.Online;

/// <summary>
/// 在线音乐平台凭据存储契约：按平台存取登录 Cookie。
/// </summary>
/// <remarks>
/// 🔴 实现必须加密落盘（DPAPI），禁止明文（项目红线：敏感数据禁明文存储）。
/// VM/服务层只经此抽象存取，不感知加密细节；实现位于 Infrastructure
/// （DPAPI 为 Windows 专属），经 Shell 组合根注册。
/// </remarks>
public interface IOnlineCredentialStore
{
    /// <summary>读取平台 Cookie；未存储或数据损坏时返回 null（视为未登录）。</summary>
    string? GetCookie(OnlineProvider provider);

    /// <summary>加密保存平台 Cookie（覆盖旧值）。</summary>
    void SetCookie(OnlineProvider provider, string cookie);

    /// <summary>清除平台 Cookie（登出/失效时调用）。</summary>
    void Clear(OnlineProvider provider);
}
