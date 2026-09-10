using System.Globalization;
using System.Runtime.Versioning;
using SystemToolkit.Core.GameManager.Models;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 客户端管理服务（聚合 14 条 API，对应 NexBox steam.rs 14 条 Tauri 命令）。
/// <para>所有操作均为纯本地 + 少量可选 HTTP（头像在线兜底），与 Blazor/WPF 模块零耦合。</para>
/// <para>本服务本身无 Windows 互操作以外的平台敏感代码；为避免调用方误误用，
/// 在模块层注册时自行加 [SupportedOSPlatform("windows")] 断言即可。</para>
/// </summary>
public sealed partial class SteamService
{
    // =============== S4 库目录 ===============
    /// <summary>解析库目录（config/libraryfolders.vdf 新格式，回退旧格式；单库兜底）。</summary>
    [SupportedOSPlatform("windows")]
    public SteamLibrary[] ParseLibraryFolders(string steamInstallPath)
    {
        // 新格式（Steam 新版）：config/libraryfolders.vdf；回退旧格式：steamapps/libraryfolders.vdf
        string path = Path.Combine(steamInstallPath, "config", "libraryfolders.vdf");
        if (!File.Exists(path))
            path = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(path))
        {
            _logger.Warn("两个 libraryfolders.vdf 都不存在，回退 Steam 主目录作为单库");
            // 构造一个默认库：steam 主目录
            var main = new SteamLibrary
            {
                Index = 0,
                Path = steamInstallPath,
                Label = string.Empty,
                Apps = Array.Empty<string>(),
            };
            main = FillDriveSize(main);
            return new[] { main };
        }
        string raw = File.ReadAllText(path);
        VdfValue root = VdfParser.Parse(raw);
        // 找 "LibraryFolders" 根块（有的版本大小写不同 "libraryfolders"）
        VdfValue? libRoot = null;
        foreach ((string? k, VdfValue? v) in (root.GetObjEntries() ?? Array.Empty<KeyValuePair<string, VdfValue>>()))
        {
            if (string.Equals(k, "LibraryFolders", StringComparison.OrdinalIgnoreCase)
                || k == "libraryfolders" || k == "Libraryfolders")
            {
                libRoot = v;
                break;
            }
        }
        IReadOnlyList<KeyValuePair<string, VdfValue>>? entries = libRoot?.GetObjEntries() ?? root.GetObjEntries();
        if (entries is null)
            return Array.Empty<SteamLibrary>();

        var list = new List<SteamLibrary>(capacity: entries.Count);
        foreach ((string? key, VdfValue? val) in entries)
        {
            if (val.GetObjEntries() is null)
                continue;
            // 跳过 path/contentstatsid 这种顶层 string 条目（仅新格式）；必须是数字 key 或 含 path 的对象
            if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out uint idx))
                continue;

            string? rawPath = val.GetStr("path");
            string? libPath = string.IsNullOrEmpty(rawPath) ? null : SteamRegistry.NormalizePath(rawPath);
            if (string.IsNullOrEmpty(libPath))
                continue;
            ulong totalSize = ParseULong(val.GetStr("totalsize"));
            string label = val.GetStr("label") ?? string.Empty;

            var apps = new List<string>();
            VdfValue? appsObj = val.GetObj("apps");
            if (appsObj?.GetObjEntries() is { } appsEntries)
            {
                foreach ((string? id, VdfValue _) in appsEntries)
                    if (!string.IsNullOrEmpty(id) && uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        apps.Add(id);
            }
            var lib = new SteamLibrary
            {
                Index = idx,
                Path = libPath,
                Label = label,
                TotalSize = totalSize,
                Apps = apps,
            };
            lib = FillDriveSize(lib);
            list.Add(lib);
        }
        _logger.Info($"解析 libraryfolders.vdf：{list.Count} 个库");
        return list.OrderBy(l => l.Index).ToArray();
    }

    // =============== S5 已安装游戏 ===============
    /// <summary>
    /// 扫描每个库的 steamapps/appmanifest_*.acf；同时扫 steam userdata/*/config/localconfig.vdf 补游玩时长。
    /// </summary>
    /// <summary>
    /// 定位游戏封面（纯本地、不外呼）。
    /// 🔴 实测（2026-09-05，本机 Steam）：新版 Steam 把封面集中放在 **Steam 主安装目录**的
    /// `appcache\librarycache\{appid}\header.jpg`，**不在各库目录的 steamapps\librarycache**——
    /// 旧实现只按后者探测导致封面全部缺失。此处按「主目录 appcache → 库目录 steamapps → userdata」三级探测。
    /// <para>
    /// 🔴 候选顺序即显示效果（2026-09-06）：卡片封面容器是**横版**（约 2.1:1），而
    /// library_600x900 是**竖版**（2:3）——竖图用 UniformToFill 填横容器会按宽撑满、
    /// 高度达容器的 3.2 倍，只露出中间约 31% 横带（用户反馈「封面显示不完全」的根因）。
    /// 因此 header.jpg（460×215 横版，与容器比例匹配）必须排在竖版之前。
    /// </para>
    /// </summary>
    public static string? FindCoverArt(string steamInstallPath, string libraryPath, uint appId)
    {
        string id = appId.ToString(CultureInfo.InvariantCulture);
        var candidates = new List<string>();

        // ① 新版：Steam 主目录 appcache\librarycache\{appid}\
        //    横版优先——header.jpg（CDN 老命名，460×215）与 library_header.jpg（新版 Steam 本地命名，
        //    实测 2026-09-06：两种命名在不同游戏上并存，如 Sekiro=header.jpg、GoT=library_header.jpg），
        //    与卡片横版容器比例匹配，UniformToFill 只裁极少量；
        //    library_600x900*（竖版）与 portrait/hero 仅作缺失时的兜底。
        string mainCache = Path.Combine(steamInstallPath, "appcache", "librarycache", id);
        candidates.Add(Path.Combine(mainCache, "header.jpg"));
        candidates.Add(Path.Combine(mainCache, "library_header.jpg"));
        candidates.Add(Path.Combine(mainCache, "library_600x900_2x.jpg"));
        candidates.Add(Path.Combine(mainCache, "library_600x900.jpg"));
        candidates.Add(Path.Combine(mainCache, "portrait.png"));
        candidates.Add(Path.Combine(mainCache, "library_hero.jpg"));

        // ② 旧版 / 每库存一份：库目录 steamapps\librarycache\{appid}\ 与平铺旧命名
        string libCache = Path.Combine(libraryPath, "steamapps", "librarycache", id);
        candidates.Add(Path.Combine(libCache, "header.jpg"));
        candidates.Add(Path.Combine(libCache, "library_header.jpg"));
        candidates.Add(Path.Combine(libCache, "library_600x900_2x.jpg"));
        candidates.Add(Path.Combine(libCache, "library_600x900.jpg"));
        string flat = Path.Combine(libraryPath, "steamapps", "librarycache");
        candidates.Add(Path.Combine(flat, appId + "_header.jpg"));
        candidates.Add(Path.Combine(flat, appId + "_library_600x900.jpg"));

        // 🔴 尺寸下限：librarycache 里混有 32×32 的图标级 jpg（哈希名），
        // 若不加过滤会被当封面拉伸成糊图（实测：4/9 款游戏目录里只有 32×32 图）。
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            (int Width, int Height)? size = Utilities.ImageSizeProbe.ReadSize(candidate);
            if (size is null || size.Value.Width >= MinCoverWidth && size.Value.Height >= MinCoverHeight)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>封面最小像素（低于此值视为图标，不用作封面）。</summary>
    private const int MinCoverWidth = 200;
    private const int MinCoverHeight = 100;

    // =============== CDN 封面兜底 ===============

    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// 本地无封面时从 Steam 官方 CDN 下载到应用缓存目录（用户实测指定：
    /// https://cdn.cloudflare.steamstatic.com/steam/apps/{appid}/header.jpg，公开 CDN、URL 固定）。
    /// header.jpg（460×215 横版）优先匹配卡片比例；404 再试 library_600x900.jpg（竖版）。
    /// 任何失败返回 null（静默，UI 显示占位）——封面缺失不应报错打扰用户。
    /// </summary>
    public static async Task<string?> EnsureCoverFromCdnAsync(string cacheDir, uint appId, CancellationToken ct = default)
    {
        if (appId == 0)
            return null;
        try
        {
            string target = Path.Combine(cacheDir, appId + ".jpg");
            if (File.Exists(target))
                return target;

            Directory.CreateDirectory(cacheDir);
            string[] urls =
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
            ];
            foreach (string url in urls)
            {
                try
                {
                    using HttpResponseMessage resp = await CoverHttp.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        continue;

                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    // 防误存：CDN 偶尔回退到 32×32 图标或空体——尺寸过小不落盘
                    if (bytes.Length < 4096)
                        continue;

                    // 🟡 审查 2026-09-10（🟡-16）：改原子写（唯一 tmp + Move 覆盖）——
                    // 直写目标时若中断会留下半截 jpg，且下次因"文件已存在"不再重下。
                    SystemToolkit.Core.Utilities.AtomicFile.WriteAllBytes(target, bytes);
                    return target;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单 URL 失败（网络/404）继续下一个
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNonGameEntry(SteamGame g) =>
        g.AppId == 228983
        || g.Name.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase);

    /// <summary>扫描各库 appmanifest_*.acf，并从 userdata 的 localconfig.vdf 补游玩时长与最近游玩。</summary>
    [SupportedOSPlatform("windows")]
    public SteamGame[] ScanInstalledGames(string steamInstallPath, IReadOnlyList<SteamLibrary> libraries)
    {
        var games = new Dictionary<uint, SteamGame>();
        var scanDirs = new List<string>();
        var manifestFiles = new List<string>();
        var errors = new List<string>();

        if (libraries.Count == 0)
            return Array.Empty<SteamGame>();
        foreach (SteamLibrary lib in libraries)
        {
            string steamappsDir = Path.Combine(lib.Path, "steamapps");
            scanDirs.Add(steamappsDir);
            if (!Directory.Exists(steamappsDir))
                continue;
            foreach (string file in Directory.EnumerateFiles(steamappsDir, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                manifestFiles.Add(file);
                try
                {
                    SteamGame? g = ParseAppManifest(file, lib.Path);
                    if (g is null)
                        continue;
                    if (g.AppId == 0)
                        continue;
                    // 过滤非游戏：Steamworks Common Redistributables（appid 228983，Steam 公共运行库，
                    // 用户实测反馈不应出现在游戏列表）
                    if (IsNonGameEntry(g))
                        continue;
                    games[g.AppId] = g; // 允许同 ID 后覆盖（通常只出现一次）
                }
                catch (Exception e)
                {
                    errors.Add($"{file}: {e.Message}");
                    _logger.Warn($"解析 manifest 失败：{file} — {e.Message}");
                }
            }
        }

        // 补游玩时长：扫 userdata/*/config/localconfig.vdf
        try
        {
            string userdataDir = Path.Combine(steamInstallPath, "userdata");
            if (Directory.Exists(userdataDir))
            {
                var playtimes = new Dictionary<uint, (ulong mins, long lastPlayed)>();
                foreach (string userDir in Directory.EnumerateDirectories(userdataDir))
                {
                    string lcFile = Path.Combine(userDir, "config", "localconfig.vdf");
                    if (!File.Exists(lcFile))
                        continue;
                    try
                    {
                        VdfValue root = VdfParser.Parse(File.ReadAllText(lcFile));
                        VdfValue? software = DeepObject(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                        if (software?.GetObjEntries() is { } appEnts)
                        {
                            foreach ((string? appid, VdfValue? appObj) in appEnts)
                            {
                                if (!uint.TryParse(appid, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
                                    continue;
                                // 【坑】localconfig.vdf 里的字段名是 "Playtime"（旧版 "Playtime2"）；
                                // "playtime_forever" 是 Steam Web API 的字段名，本地文件里不存在，
                                // 曾导致游玩时长永远解析为 0（2026-09-03 修复，已用本机 localconfig.vdf 实测）。
                                ulong mins = ParseULong(appObj.GetStr("Playtime") ?? appObj.GetStr("Playtime2"));
                                long last = ParseLong(appObj.GetStr("LastPlayed"));
                                // 多用户取 max（REVIEW-3 G-4：旧实现 mins 较小时整条跳过，
                                // LastPlayed 取不到跨用户最大值 →「最近游玩」显示错误）
                                if (!playtimes.TryGetValue(id, out (ulong mins, long lastPlayed) cur))
                                {
                                    playtimes[id] = (mins, last);
                                }
                                else
                                {
                                    playtimes[id] = (Math.Max(mins, cur.mins), Math.Max(last, cur.lastPlayed));
                                }
                            }
                        }
                    }
                    catch (Exception e) { errors.Add($"localconfig({userDir}): {e.Message}"); }
                }
                foreach ((uint id, (ulong mins, long lastPlayed) pt) in playtimes)
                {
                    if (games.TryGetValue(id, out SteamGame? g))
                        games[id] = g with { PlaytimeMinutes = pt.mins, LastPlayed = pt.lastPlayed };
                }
            }
        }
        catch (Exception e) { errors.Add($"userdata 扫描: {e.Message}"); }

        // 补封面：按实测三级路径探测（主目录 appcache 优先）
        int coverHits = 0;
        try
        {
            foreach (uint id in games.Keys.ToArray())
            {
                SteamGame g = games[id];
                string? cover = FindCoverArt(steamInstallPath, g.LibraryPath, id);
                if (cover is not null)
                {
                    games[id] = g with { CoverImagePath = cover };
                    coverHits++;
                }
            }
        }
        catch (Exception e) { errors.Add($"封面探测: {e.Message}"); }

        SteamGame[] result = games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _logger.Info($"扫描游戏完成：{result.Length} 款，封面命中 {coverHits}，manifest 数={manifestFiles.Count}，错误={errors.Count}");
        return result;
    }

}
