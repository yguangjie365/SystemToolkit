namespace SystemToolkit.Core.GameManager.Models;

/// <summary>
/// <c>appinfo.vdf</c> 一条应用记录（B1，2026-09-13）。只保留本地解析可靠拿到的两个字段。
/// </summary>
public sealed record SteamAppInfoEntry
{
    /// <summary>
    /// 显示名（新客户端在根级 <c>name</c>，旧客户端在 <c>common.name</c>——两种布局都取）。
    /// 空串 = 该条目未携带名称（调用方退化为 <c>App {id}</c>）。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 应用类型（<c>common.type</c>，如 <c>Game</c> / <c>Tool</c> / <c>Demo</c> / <c>Application</c>）。
    /// <para>
    /// 这是**本地唯一**能区分「游戏 / 工具 / 演示版」的来源（<c>.acf</c> 与 localconfig 都没有该信息），
    /// 故随名称一并解析并保留；当前批次只消费 <see cref="Name"/>。
    /// </para>
    /// </summary>
    public string Type { get; init; } = string.Empty;
}
