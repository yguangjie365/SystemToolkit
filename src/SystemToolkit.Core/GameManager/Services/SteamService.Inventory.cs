using System.Globalization;
using System.Runtime.Versioning;
using SystemToolkit.Core.GameManager.Models;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 客户端管理服务 —— <b>库存（B2 本地部分）</b> 分册（2026-09-13，游戏管理试点批次 3）。
/// <para>
/// 库存 = <b>已安装(.acf) ∪ 有游玩记录(localconfig)</b> 的并集。名称缺失时用 <c>appinfo.vdf</c> 补，
/// 仍缺则退化为 <c>App {id}</c>（**不猜**，也不丢弃条目）。
/// </para>
/// <para>
/// ⚠️ 覆盖边界（本地源固有，必须向用户如实交代）：这不是"账号拥有的全部游戏"。
/// 从未在本机安装、也没有游玩记录的游戏在本地**完全无痕**——完整库存需在线源（批次 4）。
/// </para>
/// </summary>
public sealed partial class SteamService
{
    /// <summary>
    /// 扫描本地库存（批次 3：仅本地源，不联网）。
    /// <para>
    /// 🔴 整个过程**零网络请求**；任何单点失败都降级为"少几个名称/少几个条目"并在
    /// <see cref="SteamInventorySnapshot.Error"/> 中如实记录，不抛异常、不静默。
    /// </para>
    /// <para>
    /// 性能说明：本方法内部会调用一次 <see cref="ScanInstalledGames"/>（它自身已解析一遍 localconfig），
    /// 之后再解析一次 localconfig 以取到**未安装**条目的游玩记录——多出一次小文件读取，
    /// 换来的是"不为库存再造一份已安装扫描逻辑"。调用方应在后台线程执行本方法。
    /// </para>
    /// </summary>
    /// <param name="steamInstallPath">Steam 安装目录（来自 <see cref="SteamRegistry.GetInstallPath"/>）。</param>
    /// <param name="libraries">已解析的库列表（<see cref="ParseLibraryFolders"/> 的结果）。</param>
    /// <param name="installedGames">
    /// 已扫描的已安装清单；<c>null</c> = 本方法自行调用 <see cref="ScanInstalledGames"/>。
    /// 调用方若手上已有（如 <see cref="GetAllData"/>），传进来可**省掉一次全库扫描**。
    /// </param>
    [SupportedOSPlatform("windows")]
    public SteamInventorySnapshot ScanInventoryLocal(
        string? steamInstallPath,
        IReadOnlyList<SteamLibrary>? libraries,
        IReadOnlyList<SteamGame>? installedGames = null)
    {
        if (string.IsNullOrWhiteSpace(steamInstallPath) || !Directory.Exists(steamInstallPath))
        {
            return new SteamInventorySnapshot(); // Source = None
        }

        string installPath = steamInstallPath;
        var errors = new List<string>();

        // ---- ① 已安装（.acf）：库存的基础 ----
        IReadOnlyList<SteamGame> installed;
        if (installedGames is not null)
        {
            installed = installedGames;
        }
        else
        {
            try
            {
                installed = ScanInstalledGames(installPath, libraries ?? Array.Empty<SteamLibrary>());
            }
            catch (Exception e)
            {
                installed = Array.Empty<SteamGame>();
                errors.Add("已安装扫描: " + e.Message);
                _logger.Error("库存扫描：已安装清单扫描失败", e);
            }
        }

        // ---- ② 游玩记录（localconfig）：补时长 + 带出"玩过但已卸载"的条目 ----
        Dictionary<uint, (ulong Minutes, long LastPlayed)> playtimes =
            ParseLocalConfigPlaytimes(installPath, errors);

        var merged = new Dictionary<uint, SteamInventoryGame>(installed.Count + playtimes.Count);
        foreach (SteamGame g in installed)
        {
            merged[g.AppId] = new SteamInventoryGame
            {
                AppId = g.AppId,
                Name = g.Name,
                Installed = true,
                PlaytimeMinutes = g.PlaytimeMinutes,
                LastPlayed = g.LastPlayed,
                SizeOnDisk = g.SizeOnDisk,
                StateFlags = g.StateFlags,
                InstallDir = g.InstallDir,
                LibraryPath = g.LibraryPath,
            };
        }

        foreach ((uint appId, (ulong minutes, long lastPlayed)) in playtimes)
        {
            if (merged.TryGetValue(appId, out SteamInventoryGame? existing))
            {
                // 多用户/多来源取最大（与 ScanInstalledGames 同口径）
                merged[appId] = existing with
                {
                    PlaytimeMinutes = Math.Max(existing.PlaytimeMinutes, minutes),
                    LastPlayed = Math.Max(existing.LastPlayed, lastPlayed),
                };
            }
            else
            {
                merged[appId] = new SteamInventoryGame
                {
                    AppId = appId,
                    Installed = false, // 有记录、无 .acf → 已卸载
                    PlaytimeMinutes = minutes,
                    LastPlayed = lastPlayed,
                };
            }
        }

        // ---- ③ 读 appinfo.vdf：既供补名（本地唯一的"未安装条目"名称来源），也供**类型过滤** ----
        // 🔴 必须**无条件**读——类型过滤依赖它。此前只在"缺名"时读，于是 type=Config 的
        // Steam 自带条目（appid 7 / 760 / 241100 / 2371090）混进列表，被显示成"未安装的游戏"
        // （2026-09-13 实机反馈：Steam Client / Steam Screenshots / Steam Input Configs / Steam Game Notes）。
        string appInfoPath = Path.Combine(installPath, "appcache", "appinfo.vdf");
        IReadOnlyDictionary<uint, SteamAppInfoEntry> appInfo = AppInfoVdfParser.Parse(appInfoPath);
        int appInfoCount = appInfo.Count;
        int namedFromAppInfo = 0;
        int missingNames = 0;

        foreach (uint appId in merged.Keys.ToArray())
        {
            SteamInventoryGame g = merged[appId];
            if (g.Name.Length > 0)
            {
                continue;
            }

            missingNames++;
            if (appInfo.TryGetValue(appId, out SteamAppInfoEntry? entry) && entry.Name.Length > 0)
            {
                merged[appId] = g with { Name = entry.Name };
                namedFromAppInfo++;
            }
        }

        if (appInfoCount == 0)
        {
            _logger.Warn(
                $"库存扫描：appinfo.vdf 解析为空（{appInfoPath}）——未安装条目名称将退化为 App {{id}}，"
                + "且无法按类型剔除 Steam 自带条目");
        }

        // 仍未命中的一律兜底为 App {id}（条目保留，不丢数据）
        foreach (uint appId in merged.Keys.ToArray())
        {
            if (merged[appId].Name.Length == 0)
            {
                merged[appId] = merged[appId] with { Name = "App " + appId.ToString(CultureInfo.InvariantCulture) };
            }
        }

        // ---- ④ 过滤非游戏（**含按 appinfo 类型**） + 排序 + 统计 ----
        var games = merged.Values
            .Where(g => !IsNonGame(g.AppId, g.Name, GetAppType(appInfo, g.AppId)))
            .OrderByDescending(g => g.LastPlayed)
            .ThenBy(g => g.AppId)
            .ToList();

        int installedCount = 0;
        ulong totalPlaytime = 0;
        foreach (SteamInventoryGame g in games)
        {
            if (g.Installed)
            {
                installedCount++;
            }

            totalPlaytime += g.PlaytimeMinutes;
        }

        var stats = new SteamInventoryStats
        {
            Total = games.Count,
            Installed = installedCount,
            NotInstalled = games.Count - installedCount,
            TotalPlaytimeMinutes = totalPlaytime,
        };

        _logger.Info(
            $"本地库存扫描完成：{stats.Total} 项（已安装 {stats.Installed} / 未安装 {stats.NotInstalled}），"
            + $"缺名 {missingNames} 项（appinfo 命中 {namedFromAppInfo} / 表 {appInfoCount} 条），错误 {errors.Count}");

        return new SteamInventorySnapshot
        {
            Source = SteamInventorySource.LocalCache,
            Stats = stats,
            Games = games,
            Error = errors.Count == 0 ? null : string.Join("；", errors),
        };
    }

    /// <summary>
    /// 取 appinfo 里该 AppId 的类型；表中没有该条目返回空串（= **类型未知**）。
    /// </summary>
    private static string GetAppType(IReadOnlyDictionary<uint, SteamAppInfoEntry> appInfo, uint appId)
        => appInfo.TryGetValue(appId, out SteamAppInfoEntry? entry) ? entry.Type : string.Empty;

    /// <summary>
    /// 解析 <c>userdata/*/config/localconfig.vdf</c> 的游玩记录（appId → 时长/最近游玩）。
    /// <para>
    /// 🔴 单一实现：<see cref="ScanInstalledGames"/> 与 <see cref="ScanInventoryLocal"/> 共用——
    /// 两处各写一份必然漂移（其中一处修了字段名、另一处没修）。
    /// </para>
    /// <para>
    /// 【坑】字段名是 <c>Playtime</c>（旧版 <c>Playtime2</c>）；<c>playtime_forever</c> 是
    /// Steam Web API 的字段名，本地文件里不存在——曾导致时长恒为 0（2026-09-03 用本机文件实测修复）。
    /// 多用户条目取 max（REVIEW-3 G-4：旧实现时长较小时整条跳过，LastPlayed 取不到跨用户最大值）。
    /// </para>
    /// </summary>
    /// <param name="steamInstallPath">Steam 安装目录。</param>
    /// <param name="errors">可选：收集非致命错误（沿用调用方的错误列表）。</param>
    internal static Dictionary<uint, (ulong Minutes, long LastPlayed)> ParseLocalConfigPlaytimes(
        string steamInstallPath,
        List<string>? errors = null)
    {
        var playtimes = new Dictionary<uint, (ulong Minutes, long LastPlayed)>();
        try
        {
            string userdataDir = Path.Combine(steamInstallPath, "userdata");
            if (!Directory.Exists(userdataDir))
            {
                return playtimes;
            }

            foreach (string userDir in Directory.EnumerateDirectories(userdataDir))
            {
                string localConfigFile = Path.Combine(userDir, "config", "localconfig.vdf");
                if (!File.Exists(localConfigFile))
                {
                    continue;
                }

                try
                {
                    VdfValue root = VdfParser.Parse(File.ReadAllText(localConfigFile));
                    VdfValue? apps = DeepObject(
                        root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                    if (apps?.GetObjEntries() is not { } appEntries)
                    {
                        continue;
                    }

                    foreach ((string? appIdText, VdfValue? appObj) in appEntries)
                    {
                        if (!uint.TryParse(appIdText, NumberStyles.None, CultureInfo.InvariantCulture, out uint appId))
                        {
                            continue;
                        }

                        ulong minutes = ParseULong(appObj.GetStr("Playtime") ?? appObj.GetStr("Playtime2"));
                        long lastPlayed = ParseLong(appObj.GetStr("LastPlayed"));
                        if (!playtimes.TryGetValue(appId, out (ulong Minutes, long LastPlayed) current))
                        {
                            playtimes[appId] = (minutes, lastPlayed);
                        }
                        else
                        {
                            playtimes[appId] = (Math.Max(minutes, current.Minutes), Math.Max(lastPlayed, current.LastPlayed));
                        }
                    }
                }
                catch (Exception e)
                {
                    errors?.Add($"localconfig({userDir}): {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            errors?.Add("userdata 扫描: " + e.Message);
        }

        return playtimes;
    }
}
