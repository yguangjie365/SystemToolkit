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
    /// 库存扫描据此剔除 Steam 自带条目（<c>type=Config</c> 之类）。
    /// </para>
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// 简体中文名（<c>common.name_localized.schinese</c>）。空串 = 该条目未提供中文名。
    /// <para>
    /// 🔴 <c>common.name</c> **恒为英文原名**（如「Black Myth: Wukong」）——想在界面上显示中文，
    /// 只能取本字段（2026-09-13 实机反馈「黑神话：悟空显示成英文名」）。
    /// </para>
    /// </summary>
    public string NameSchinese { get; init; } = string.Empty;

    /// <summary>繁体中文名（<c>common.name_localized.tchinese</c>）。简中缺失时的次选。</summary>
    public string NameTchinese { get; init; } = string.Empty;

    /// <summary>简体中文头图（<c>common.header_image.schinese</c>）——**不含域名的相对路径**。</summary>
    public string HeaderImageSchinese { get; init; } = string.Empty;

    /// <summary>英文头图（<c>common.header_image.english</c>）——**不含域名的相对路径**。</summary>
    public string HeaderImageEnglish { get; init; } = string.Empty;

    /// <summary>繁体中文头图（<c>common.header_image.tchinese</c>）。</summary>
    public string HeaderImageTchinese { get; init; } = string.Empty;

    /// <summary>
    /// **真中文名**：简中 → 繁中 → 空串（**不回退英文**——调用方需要区分"有中文名"与"只有原名"）。
    /// </summary>
    public string ChineseName =>
        NameSchinese.Length > 0 ? NameSchinese
        : NameTchinese;

    /// <summary>
    /// 优先头图相对路径：简中 → 英 → 繁中 → 空串（调用方据此退化为固定候选）。
    /// <para>
    /// 实测（2026-09-13）该值有两种形态：<c>header.jpg</c>（老式）与
    /// <c>{hash}/header.jpg</c>（新式，带 hash 子目录）——**都必须拼到
    /// <c>shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appid}/</c> 之后**。
    /// 原先用的 <c>cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg</c> 已 404。
    /// </para>
    /// </summary>
    public string PreferredHeaderImage =>
        HeaderImageSchinese.Length > 0 ? HeaderImageSchinese
        : HeaderImageEnglish.Length > 0 ? HeaderImageEnglish
        : HeaderImageTchinese;
}
