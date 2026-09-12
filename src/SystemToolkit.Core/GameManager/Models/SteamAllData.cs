namespace SystemToolkit.Core.GameManager.Models;

/// <summary>聚合数据包（一次性返回所有 Steam 数据，减少 UI 往返请求）。</summary>
public sealed record SteamAllData
{
    /// <summary>安装与运行态。</summary>
    public SteamInstallInfo InstallInfo { get; init; } = new();

    /// <summary>记住的用户列表（按 MostRecent 倒序，当前用户在第一位）。</summary>
    public IReadOnlyList<SteamUser> Users { get; init; } = Array.Empty<SteamUser>();

    /// <summary>游戏库文件夹列表。</summary>
    public IReadOnlyList<SteamLibrary> Libraries { get; init; } = Array.Empty<SteamLibrary>();

    /// <summary>已安装游戏列表（按名称升序去重，AppID 唯一）。</summary>
    public IReadOnlyList<SteamGame> Games { get; init; } = Array.Empty<SteamGame>();

    /// <summary>
    /// 库存快照（B2，2026-09-13）：<b>已安装 ∪ 有游玩记录</b>，是 <see cref="Games"/> 的超集。
    /// 未安装 Steam 时为默认值（<see cref="SteamInventorySource.None"/>）。
    /// </summary>
    public SteamInventorySnapshot Inventory { get; init; } = new();
}
