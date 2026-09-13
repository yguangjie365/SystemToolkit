namespace SystemToolkit.Core.GameManager.Online;

/// <summary>
/// Steam Web API Key 存储契约（批次 4，2026-09-13）。
/// <para>
/// Key 用于调用 Steam 官方 <c>IPlayerService/GetOwnedGames</c> 拉取账号**完整**拥有清单
/// （本地源看不见的那部分：从未在本机安装、也没有游玩记录的游戏）。
/// </para>
/// </summary>
/// <remarks>
/// 🔴 实现必须加密落盘（DPAPI），**禁止明文**（项目红线：敏感数据禁明文存储）。
/// VM/服务层只经此抽象存取，不感知加密细节；实现位于 Infrastructure（DPAPI 为 Windows 专属），
/// 经 Shell 组合根注册。形状对齐既有的 <c>IOnlineCredentialStore</c>（音乐在线凭据）。
/// </remarks>
public interface ISteamApiKeyStore
{
    /// <summary>读取 Key；未存储或数据损坏时返回 <c>null</c>（视为未配置）。</summary>
    string? Get();

    /// <summary>
    /// 保存 Key（覆盖旧值）。传入 <c>null</c> 或空白串时**等同 <see cref="Clear"/>**——
    /// 由实现统一处理，避免每个调用方各写一遍判空（也避免"存了空串"这种自相矛盾的状态）。
    /// </summary>
    void Set(string? apiKey);

    /// <summary>清除 Key（用户主动清除，或确认 Key 失效时调用）。</summary>
    void Clear();
}
