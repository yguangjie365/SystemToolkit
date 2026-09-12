namespace SystemToolkit.Core.GameManager.Models;

/// <summary>
/// 库存数据来源（三态，2026-09-13）。调用方据此如实标注"这份库存从哪来"，不得含糊。
/// </summary>
public enum SteamInventorySource
{
    /// <summary>无数据（未安装 Steam / 无本地痕迹）。</summary>
    None = 0,

    /// <summary>本地缓存：<c>.acf</c> 已安装清单 + localconfig 游玩记录 + appinfo 目录（批次 3）。</summary>
    LocalCache = 1,

    /// <summary>在线库存（批次 4 落地；失败时**降级**为 <see cref="LocalCache"/> 并填 <see cref="SteamInventorySnapshot.Error"/>）。</summary>
    Online = 2,
}

/// <summary>库存统计（B2）。</summary>
public sealed record SteamInventoryStats
{
    /// <summary>条目总数（已安装 + 未安装）。</summary>
    public int Total { get; init; }

    /// <summary>其中已安装数。</summary>
    public int Installed { get; init; }

    /// <summary>其中未安装数（玩过但已卸载）。</summary>
    public int NotInstalled { get; init; }

    /// <summary>总游玩时长（分钟）。</summary>
    public ulong TotalPlaytimeMinutes { get; init; }
}

/// <summary>
/// 库存快照（B2）：一次扫描的完整结果。数据源 / 统计 / 条目 / 错误四件套。
/// </summary>
public sealed record SteamInventorySnapshot
{
    /// <summary>数据来源（三态）。</summary>
    public SteamInventorySource Source { get; init; } = SteamInventorySource.None;

    /// <summary>统计。</summary>
    public SteamInventoryStats Stats { get; init; } = new();

    /// <summary>条目（按最近游玩倒序；从未游玩者按 AppID 升序排在末尾）。</summary>
    public IReadOnlyList<SteamInventoryGame> Games { get; init; } = Array.Empty<SteamInventoryGame>();

    /// <summary>
    /// 降级/失败原因（成功且未降级时为空）。
    /// 非空即表示 <see cref="Source"/> 不是期望的那一档——UI 必须如实展示，不得静默。
    /// </summary>
    public string? Error { get; init; }
}
