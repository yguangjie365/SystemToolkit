using System.Globalization;
using System.Runtime.Versioning;
using SystemToolkit.Core.GameManager.Models;

using SystemToolkit.Core.GameManager.Cover;

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
        //  🔴 2026-09-13 补「语言后缀」变体（见 CoverAssetStems / CoverAssetLanguages 注释）：
        //    新版 Steam 会按**客户端界面语言**另存一份本地封面（如 library_header_schinese.jpg），
        //    而**无后缀的英文版可能根本不存在**——黑神话：悟空（2358720）本机目录里只有
        //    library_header_schinese.jpg + library_600x900_schinese.jpg，旧候选表因此全部落空，
        //    一路跌到序末的 library_hero.jpg（1920×620 的超宽库页背景图）；同一张 3.1:1 超宽图被
        //    卡片容器（≈1.6:1）与详情容器（1.94:1）各裁一套构图 → 实机反馈「详情页与卡片封面不一样」。
        string mainCache = Path.Combine(steamInstallPath, "appcache", "librarycache", id);
        AddCoverCandidates(candidates, mainCache);
        candidates.Add(Path.Combine(mainCache, "portrait.png"));
        candidates.Add(Path.Combine(mainCache, "library_hero.jpg"));

        // ② 旧版 / 每库存一份：库目录 steamapps\librarycache\{appid}\ 与平铺旧命名
        string libCache = Path.Combine(libraryPath, "steamapps", "librarycache", id);
        AddCoverCandidates(candidates, libCache);
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

    /// <summary>
    /// 本地封面**文件名族**（顺序即质量序）。横版的 <c>header</c> / <c>library_header</c> 与卡片横版容器
    /// 比例接近；<c>library_600x900*</c> 是竖版（2:3），仅作兜底——竖图填横容器只会露出中间一条横带。
    /// </summary>
    private static readonly string[] CoverAssetStems =
        new[] { "header", "library_header", "library_600x900_2x", "library_600x900" };

    /// <summary>
    /// 本地封面的**语言后缀**（顺序即优先序，中文用户优先）。新版 Steam 按客户端界面语言另存一份
    /// 本地封面，命名是 <c>{族}_{语言}.jpg</c>（如 <c>library_header_schinese.jpg</c>），
    /// 且**可能没有无后缀的英文版**——只按无后缀名探测会整族落空（详见 <see cref="FindCoverArt"/> 注释）。
    /// </summary>
    private static readonly string[] CoverAssetLanguages =
        new[] { "schinese", "tchinese", "english", "japanese", "koreana" };

    /// <summary>
    /// 往候选表追加某个缓存目录下的全部封面候选：每个「族」先探无后缀名，再依次探各语言后缀变体。
    /// 族与语言的嵌套顺序即优先级，命中即返回（由 <see cref="FindCoverArt"/> 短路口径执行）。
    /// </summary>
    private static void AddCoverCandidates(List<string> candidates, string cacheDir)
    {
        foreach (string stem in CoverAssetStems)
        {
            candidates.Add(Path.Combine(cacheDir, stem + ".jpg"));
            foreach (string lang in CoverAssetLanguages)
            {
                candidates.Add(Path.Combine(cacheDir, stem + "_" + lang + ".jpg"));
            }
        }
    }

    // =============== CDN 封面兜底 ===============

    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>默认失败记忆（惰性初始化）——测试可注入自己的实例，避免共享静态状态。</summary>
    private static readonly Lazy<CoverFailureMemory> LazyCoverFailures = new(() => new CoverFailureMemory());

    /// <summary>默认失败记忆实例（生产路径用）。</summary>
    internal static CoverFailureMemory DefaultCoverFailures => LazyCoverFailures.Value;

    /// <summary>
    /// **封面 CDN 候选链（显式化：顺序即优先级）** —— 从原内联列表提出，便于单测钉住顺序与内容。
    /// <list type="number">
    /// <item>appinfo 给出的相对路径（可能带 hash 子目录）——最准，优先</item>
    /// <item>新域固定 <c>header.jpg</c>（460×215 横版，贴合卡片比例）</item>
    /// <item>新域竖版 <c>library_600x900.jpg</c></item>
    /// <item>新域旧路径（不含 <c>store_item_assets</c> 段）</item>
    /// <item>旧域保底（截至 2026-09-13 实测 404，留着以防 Steam 回退）</item>
    /// </list>
    /// </summary>
    internal static List<string> BuildCoverCdnUrls(uint appId, string? headerImageSuffix)
    {
        const string SharedBase = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/";
        string id = appId.ToString(CultureInfo.InvariantCulture);
        var urls = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(headerImageSuffix))
        {
            // 已带 hash 子目录的相对路径：原样拼接（不要另加 header.jpg）
            urls.Add(SharedBase + id + "/" + headerImageSuffix);
        }

        urls.Add($"{SharedBase}{id}/header.jpg");
        urls.Add($"{SharedBase}{id}/library_600x900.jpg");
        urls.Add($"https://shared.fastly.steamstatic.com/steam/apps/{id}/header.jpg");
        urls.Add($"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg");
        return urls;
    }

    /// <summary>
    /// 本地无封面时从 Steam 官方 CDN 下载到应用缓存目录。
    /// <para>
    /// 🔴 <b>2026-09-13 修正域与路径</b>：原先只用
    /// <c>cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg</c> —— 该地址**已 404**
    /// （实机反馈「有些游戏获取不到封面」）。实测可用的是
    /// <c>shared.fastly.steamstatic.com/store_item_assets/steam/apps/{id}/…</c>，
    /// 且新式资源的文件名**带 hash 子目录**（如 <c>{hash}/header.jpg</c>），
    /// 而该相对路径就写在 <c>appinfo.vdf</c> 的 <c>common.header_image</c> 里 —— 故优先用它。
    /// </para>
    /// <para>
    /// 候选顺序：appinfo 给的相对路径 → 新域固定 header.jpg → 新域竖版 → 旧域（保底，当前 404）。
    /// header.jpg（460×215 横版）优先匹配卡片比例。任何失败返回 null（静默，UI 显示占位）。
    /// </para>
    /// </summary>
    /// <param name="cacheDir">封面缓存目录。</param>
    /// <param name="appId">AppId。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="headerImageSuffix">
    /// <c>appinfo.common.header_image</c> 给出的相对路径（可空）——形如 <c>header.jpg</c>
    /// 或 <c>{hash}/header.jpg</c>。空/缺省时退化为固定候选。
    /// </param>
    /// <param name="failures">
    /// 失败记忆（缺省用进程级默认实例）。命中记忆的候选**直接跳过**——
    /// 候选链里有已知必然失败的项（旧域 404），不记忆则每次刷新都要把整条链重试一遍。
    /// 只记确定性失败且带 TTL，见 <see cref="CoverFailureMemory"/>。
    /// </param>
    public static async Task<string?> EnsureCoverFromCdnAsync(
        string cacheDir,
        uint appId,
        CancellationToken ct = default,
        string? headerImageSuffix = null,
        CoverFailureMemory? failures = null)
    {
        if (appId == 0)
            return null;
        CoverFailureMemory memory = failures ?? DefaultCoverFailures;
        try
        {
            string target = Path.Combine(cacheDir, appId + ".jpg");
            if (File.Exists(target))
                return target;

            Directory.CreateDirectory(cacheDir);
            List<string> urls = BuildCoverCdnUrls(appId, headerImageSuffix);

            foreach (string url in urls)
            {
                // 失败记忆命中即跳过（只记确定性失败 + 带 TTL，见 CoverFailureMemory）
                if (memory.IsFailed(url, DateTimeOffset.Now))
                {
                    continue;
                }

                try
                {
                    using HttpResponseMessage resp = await CoverHttp.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // 🔴 只记"资源确实不在了"（404/410）；超时/5xx 不记——
                        //    否则一次网络抖动会演变成一整天的封面缺失
                        if (CoverFailureMemory.ShouldRemember((int)resp.StatusCode))
                        {
                            memory.MarkFailed(url, $"HTTP {(int)resp.StatusCode}", DateTimeOffset.Now);
                        }

                        continue;
                    }

                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    // 防误存：CDN 偶尔回退到 32×32 图标或空体——尺寸过小不落盘
                    if (bytes.Length < 4096)
                        continue;

                    // 🟡 审查 2026-09-10（🟡-16）：改原子写（唯一 tmp + Move 覆盖）——
                    // 直写目标时若中断会留下半截 jpg，且下次因"文件已存在"不再重下。
                    SystemToolkit.Core.Utilities.AtomicFile.WriteAllBytes(target, bytes);
                    memory.ClearFailed(url);
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
        catch (Exception ex)
        {
            // 🟠 审查 v8-🟠-5：原先这里是**裸 catch 直接 return null**，与"失败记忆内存态零同步"
            // 叠加后完全无痕 —— IsFailed/MarkFailed 在并发下抛的 ArgumentOutOfRangeException
            // 就落在这里被吃掉，表现为"每次刷新仍把整条候选链重试一遍"（失败记忆静默失效）。
            // 并发已由 CoverFailureMemory 内部互斥修掉；这里补上**异常可见**，让今后任何
            // 意外失败（如缓存目录不可建）都能被追到。
            // ⚠️ 逐 URL 的内层 catch 仍保持静默：那是有意的 continue（断网时逐条记会刷上百条），
            // 且真实失败已由调用方汇总成一条日志。
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Warn,
                "gamemanager",
                $"封面 CDN 兜底异常（AppId {appId}）：{ex.Message}",
                ex));
            return null;
        }
    }

    /// <summary>
    /// 非游戏条目判定（Steam 公共运行库等）。已安装扫描与库存扫描**共用同一口径**，
    /// 避免两处过滤条件不同导致"同一款游戏在卡片网格里消失、在库存里又出现"。
    /// </summary>
    /// <summary>
    /// 非游戏条目过滤（命中任一即排除）：
    /// <list type="number">
    /// <item>appinfo 里的类型**存在且不是 <c>Game</c>**——本地唯一的类型来源。实测本机 4 条冒牌货
    /// 都是 <c>type=Config</c>（appid 7 Steam Client / 760 Steam Screenshots /
    /// 241100 Steam Input Configs / 2371090 Steam Game Notes），它们经 localconfig 进了库存、
    /// 又被显示成「未安装的游戏」（2026-09-13 实机反馈）。</item>
    /// <item>AppID 命中黑名单 <see cref="NonGameAppIds"/>——类型判据**抓不到的** Valve 自带条目。</item>
    /// <item>名称为 <c>Steamworks Common Redistributables</c>。</item>
    /// </list>
    /// 🔴 <b>类型未知（空串）时判为「是游戏」</b>：appinfo 读不到时不能把整库判成非游戏——
    /// 宁可多留一条可疑项，也不丢真实游戏。
    /// </summary>
    /// <param name="appId">AppId。</param>
    /// <param name="name">显示名。</param>
    /// <param name="type">appinfo 里的类型（可空/可空串 = 未知）。默认空 = 只走后两条判据。</param>
    /// <remarks>
    /// <c>internal</c> 而非 private：让测试**直接钉这份实现**。此前测试因判据私有而只能"按同一规则复述"，
    /// 结果是改实现不会让测试变红——那种测试锁不住任何东西（2026-09-13 发现并改）。
    /// </remarks>
    internal static bool IsNonGame(uint appId, string name, string type = "")
    {
        if (type.Length > 0 && !type.Equals("Game", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NonGameAppIds.Contains(appId)
            || name.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Steam 自带、但 <c>appinfo</c> 里**类型就是 <c>Game</c>** 的条目——类型判据抓不到，只能白纸黑字列出来。
    /// <list type="bullet">
    /// <item><c>480</c> <c>Spacewar</c>：Valve 给 Steamworks 开发者用的示例 / 联机测试程序
    /// （可执行文件 <c>SteamWorksExample.exe</c>）。本机实测（2026-09-13）其 appinfo 为
    /// <c>common.type=Game</c>、发布者 Valve / Telltale Games，所以<b>必须</b>按 AppID 单列
    /// （2026-09-13 实机反馈要求屏蔽）。</item>
    /// <item><c>228983</c>：Steamworks 相关条目（沿用原有判据，防止类型来源缺失时漏放）。</item>
    /// </list>
    /// ⚠️ 新增条目**必须同时补测试**（<c>SteamLibraryPolicyTests</c>），否则删掉这条判据不会有任何用例变红。
    /// </summary>
    private static readonly HashSet<uint> NonGameAppIds = new() { 480, 228983 };

    private static bool IsNonGameEntry(SteamGame g) => IsNonGame(g.AppId, g.Name);

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
        // 🔴 解析逻辑已抽出为 ParseLocalConfigPlaytimes（SteamService.Inventory.cs）——
        // 与库存扫描共用同一实现：两处各写一份必然漂移（其中一处修了字段名、另一处没修）。
        Dictionary<uint, (ulong Minutes, long LastPlayed)> playtimes =
            ParseLocalConfigPlaytimes(steamInstallPath, errors);
        foreach ((uint id, (ulong Minutes, long LastPlayed) pt) in playtimes)
        {
            if (games.TryGetValue(id, out SteamGame? g))
                games[id] = g with { PlaytimeMinutes = pt.Minutes, LastPlayed = pt.LastPlayed };
        }

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
