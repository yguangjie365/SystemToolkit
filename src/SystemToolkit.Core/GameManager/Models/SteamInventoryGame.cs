namespace SystemToolkit.Core.GameManager.Models;

/// <summary>
/// 库存条目（B2，2026-09-13）：<b>已安装 ∪ 有游玩记录</b>的并集。
/// <para>
/// 与 <see cref="SteamGame"/> 的区别：<see cref="SteamGame"/> 只来自 <c>appmanifest_*.acf</c>（**必然是已安装**），
/// 而本类型额外容纳「玩过但已卸载」的条目（来源 localconfig.vdf + appinfo.vdf 补名），
/// 故带 <see cref="Installed"/> 标记。
/// </para>
/// <para>
/// ⚠️ 覆盖边界（本地源共有的局限）：本类型**不是**「账号拥有的全部游戏」——没有在线数据源时，
/// 从未在本机安装过、也没有游玩记录的游戏在本地完全无痕。完整库存需在线源（批次 4）。
/// </para>
/// </summary>
public sealed record SteamInventoryGame
{
    /// <summary>AppID。</summary>
    public uint AppId { get; init; }

    /// <summary>显示名（缺失时为 <c>App {id}</c>，见 <see cref="SteamInventorySnapshot"/>）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>是否已安装到本地（<c>.acf</c> 清单命中）。</summary>
    public bool Installed { get; init; }

    /// <summary>游玩时长（分钟；多用户取最大值）。0 = 从未游玩。</summary>
    public ulong PlaytimeMinutes { get; init; }

    /// <summary>最近游玩时间（unix 秒；0 = 从未游玩）。</summary>
    public long LastPlayed { get; init; }

    /// <summary>磁盘占用（字节；未安装恒为 0）。</summary>
    public ulong SizeOnDisk { get; init; }

    /// <summary>
    /// 安装状态位（<c>.acf</c> 的 <c>StateFlags</c>；未安装恒为 0）。
    /// <para>
    /// 卡片需要它区分「已安装完全」与「下载/更新中」——只靠 <see cref="Installed"/> 不够
    /// （正在下载的游戏也是 installed）。
    /// </para>
    /// </summary>
    public uint StateFlags { get; init; }

    /// <summary>安装子目录名（未安装为空串）。</summary>
    public string InstallDir { get; init; } = string.Empty;

    /// <summary>所属库路径（未安装为空串）。</summary>
    public string LibraryPath { get; init; } = string.Empty;
}
