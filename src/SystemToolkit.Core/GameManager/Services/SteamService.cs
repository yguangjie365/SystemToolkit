using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Xml.Linq;
using SystemToolkit.Core.Contracts;
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
    private readonly ILogger _logger;
    private HttpClient? _http; // 仅头像在线兜底时使用，懒初始化

    /// <summary>构造：注入可选日志（缺省 NullLogger）。</summary>
    public SteamService(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    // =============== S1 聚合 ===============
    /// <summary>一次性返回安装信息+用户+库+游戏。失败时对应字段为空集合，从不抛异常。</summary>
    public SteamAllData GetAllData()
    {
        // 01 分册：跨平台 TFM 的 Core 内调用 Windows API 用运行时守卫（非 [SupportedOSPlatform]）
        if (!OperatingSystem.IsWindows())
        {
            return new SteamAllData();
        }

        SteamInstallInfo install = GetInstallInfo();
        SteamUser[] users = Array.Empty<SteamUser>();
        SteamLibrary[] libs = Array.Empty<SteamLibrary>();
        SteamGame[] games = Array.Empty<SteamGame>();
        if (install.Installed && install.InstallPath is not null)
        {
            try
            { users = ParseLoginUsers(install.InstallPath); }
            catch (Exception e) { _logger.Error("ParseLoginUsers 失败", e); }
            try
            { libs = ParseLibraryFolders(install.InstallPath); }
            catch (Exception e) { _logger.Error("ParseLibraryFolders 失败", e); }
            try
            { games = ScanInstalledGames(install.InstallPath, libs); }
            catch (Exception e) { _logger.Error("ScanInstalledGames 失败", e); }
        }
        return new SteamAllData
        {
            InstallInfo = install,
            Users = users,
            Libraries = libs,
            Games = games,
        };
    }

    // =============== S2 安装信息 ===============
    /// <summary>读取 Steam 安装与运行态（注册表定位 + 进程探测）。</summary>
    [SupportedOSPlatform("windows")]
    public SteamInstallInfo GetInstallInfo()
    {
        string? p = SteamRegistry.GetInstallPath();
        return new SteamInstallInfo
        {
            Installed = p is not null && Directory.Exists(p),
            InstallPath = p is not null && Directory.Exists(p) ? p : null,
            IsRunning = SteamProcessDetector.IsRunning(),
        };
    }

    // =============== S7 诊断 ===============
    /// <summary>诊断数据包（各 VDF 路径/原文/计数/错误明细，排障用）。</summary>
    [SupportedOSPlatform("windows")]
    public SteamDebugInfo GetDebugDump()
    {
        var info = new SteamInstallInfo();
        try
        { info = GetInstallInfo(); }
        catch { }
        if (info.InstallPath is null)
            return new SteamDebugInfo { SteamPath = info.InstallPath, SteamRunning = info.IsRunning };

        string lu = Path.Combine(info.InstallPath, "config", "loginusers.vdf");
        string luRaw = string.Empty, luParseErr = string.Empty;
        int luCount = 0;
        bool luExists = File.Exists(lu);
        if (luExists)
        {
            try
            {
                luRaw = File.ReadAllText(lu);
                luCount = ParseLoginUsers(info.InstallPath).Length;
            }
            catch (Exception e) { luParseErr = e.Message; }
        }
        string lf = Path.Combine(info.InstallPath, "config", "libraryfolders.vdf");
        if (!File.Exists(lf))
            lf = Path.Combine(info.InstallPath, "steamapps", "libraryfolders.vdf");
        bool lfExists = File.Exists(lf);
        string lfRaw = string.Empty, lfParseErr = string.Empty;
        int lfCount = 0;
        var dirs = new List<string>();
        var mf = new List<string>();
        var gameErrs = new List<string>();
        int gamesCount = 0;
        try
        {
            if (lfExists)
            {
                lfRaw = File.ReadAllText(lf);
                SteamLibrary[] libs = ParseLibraryFolders(info.InstallPath);
                lfCount = libs.Length;
                foreach (SteamLibrary lib in libs)
                {
                    string d = Path.Combine(lib.Path, "steamapps");
                    dirs.Add(d);
                    if (!Directory.Exists(d))
                        continue;
                    foreach (string f in Directory.EnumerateFiles(d, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                        mf.Add(f);
                }
                SteamGame[] games = ScanInstalledGames(info.InstallPath, libs);
                gamesCount = games.Length;
            }
        }
        catch (Exception e)
        {
            lfParseErr = e.Message;
            gameErrs.Add("scan: " + e.Message);
        }
        return new SteamDebugInfo
        {
            SteamPath = info.InstallPath,
            SteamRunning = info.IsRunning,
            LoginUsersPath = lu,
            LoginUsersExists = luExists,
            LoginUsersRaw = luRaw,
            LoginUsersParseError = luParseErr,
            LoginUsersCount = luCount,
            LibraryFoldersPath = lf,
            LibraryFoldersExists = lfExists,
            LibraryFoldersRaw = lfRaw,
            LibraryFoldersParseError = lfParseErr,
            LibraryFoldersCount = lfCount,
            SteamAppsDirsScanned = dirs,
            AppManifestFilesFound = mf,
            GamesCount = gamesCount,
            GamesParseErrors = gameErrs,
        };
    }

    // =========================================================================
    // 以下内部工具方法
    // =========================================================================
    private static SteamLibrary FillDriveSize(SteamLibrary lib)
    {
        try
        {
            string root = Path.IsPathRooted(lib.Path) ? Path.GetPathRoot(lib.Path)! : lib.Path;
            var di = new DriveInfo(root);
            if (lib.TotalSize == 0)
                lib = lib with { TotalSize = (ulong)Math.Max(0L, di.TotalSize) };
            ulong free = (ulong)Math.Max(0L, di.AvailableFreeSpace);
            lib = lib with { FreeSize = free };
        }
        catch { /* 驱动器未就绪 / 网络盘 */ }
        return lib;
    }

    private static SteamGame? ParseAppManifest(string file, string libraryPath)
    {
        string raw = File.ReadAllText(file);
        VdfValue root = VdfParser.Parse(raw);
        // 通常：{"AppState" { ... }}；也有的版本直接就是 AppState 对象
        VdfValue? appState = root.GetObj("AppState");
        VdfValue src = appState ?? root;
        if (src?.GetObjEntries() is null)
            return null;
        uint appId = ParseUInt(src.GetStr("appid"));
        string name = src.GetStr("name") ?? Path.GetFileNameWithoutExtension(file);
        string installDir = src.GetStr("installdir") ?? string.Empty;
        return new SteamGame
        {
            AppId = appId,
            Name = name,
            InstallDir = installDir,
            LibraryPath = libraryPath,
            SizeOnDisk = ParseULong(src.GetStr("SizeOnDisk")),
            StateFlags = ParseUInt(src.GetStr("StateFlags")),
            LastUpdated = ParseULong(src.GetStr("LastUpdated")),
            LastOwner = src.GetStr("LastOwner") ?? string.Empty,
            BuildId = ParseUInt(src.GetStr("buildid")),
            BytesToDownload = ParseULong(src.GetStr("BytesToDownload")),
            BytesDownloaded = ParseULong(src.GetStr("BytesDownloaded")),
        };
    }

    private static VdfValue? DeepObject(VdfValue root, params string[] path)
    {
        VdfValue? cur = root;
        foreach (string seg in path)
        {
            if (cur is null)
                return null;
            // REVIEW-3 G-9：大小写回退（Valve 对 UserLocalConfigStore 有时首字母大小写不同）。
            // 旧实现恒从 root 回退——深度≥2 段（如 "Software"）大小写不匹配时查错层级，
            // localconfig 时长静默丢失。回退必须查「上一层」（进入本段之前的节点）的 entries。
            VdfValue parent = cur;
            cur = cur.GetObj(seg);
            if (cur is null)
            {
                IReadOnlyList<KeyValuePair<string, VdfValue>>? ents = parent.GetObjEntries();
                if (ents is null)
                    return null;
                foreach ((string? k, VdfValue? v) in ents)
                {
                    if (string.Equals(k, seg, StringComparison.OrdinalIgnoreCase))
                    { cur = v; break; }
                }
            }
        }
        return cur;
    }

    private static bool ParseFlag(string? s)
    {
        if (s is null)
            return false;
        if (s == "1")
            return true;
        if (s == "0")
            return false;
        if (int.TryParse(s, out int v))
            return v != 0;
        return false;
    }

    private static uint ParseUInt(string? s) =>
        uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint u) ? u : 0;
    private static ulong ParseULong(string? s) =>
        ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u) ? u : 0;
    private static long ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long l) ? l : 0L;

    private static bool ShellOpen(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            // 🔴 2026-09-08（审查 S-5）：兜底不再用 `cmd /c start`——
            // cmd 会解析 & | % ^ < > 等元字符，且原先 QuoteArg 的 \" 转义对 cmd 无效；
            // 游戏路径/steam:// 参数含特殊字符时行为不可预期。
            // 改用 explorer.exe：它把参数当作单个路径或 URL，不经过 cmd 解析。
            // ArgumentList 由运行时转义，避免手工拼接引号。
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true,
                };
                psi.ArgumentList.Add(target);
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 命令行参数引号包裹。仅供 <c>-login 账户名</c> 这类<b>单个参数</b>拼接使用
    /// （SteamProcessDetector.LaunchSteam 接受整串 Arguments）。
    /// ⚠️ 不要用它构造 cmd.exe 的命令行——cmd 的元字符（&amp; | % ^ &lt; &gt;）在引号内仍可能被解释，
    /// 且这里的 " 转义对 cmd 无效（审查 S-5）。
    /// </summary>
    internal static string QuoteArg(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static string? FindAvatarCacheDir(string? steamInstallPath)
    {
        if (string.IsNullOrEmpty(steamInstallPath))
            return null;
        string[] candidates = new[]
        {
            Path.Combine(steamInstallPath, "config", "avatarcache"),
            Path.Combine(steamInstallPath, "userdata", "avatarcache"),
            Path.Combine(steamInstallPath, "avatarcache"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ExtractSteamInstallPath()
    {
        // 头像在线兜底时，我们还没拿到实例化路径；从注册表再读一遍
        if (OperatingSystem.IsWindows())
            return SteamRegistry.GetInstallPath();
        return null;
    }

    private static string? FindLocalAvatarPng(string? avatarcacheDir, string steam64)
    {
        if (string.IsNullOrEmpty(avatarcacheDir))
            return null;
        string exact = Path.Combine(avatarcacheDir, steam64 + ".png");
        if (File.Exists(exact))
            return exact;
        // 有些用户头像缓存叫 {steam64}_medium.png
        string mid = Path.Combine(avatarcacheDir, steam64 + "_medium.png");
        return File.Exists(mid) ? mid : null;
    }

    private async Task<(string? small, string? medium, string? full)> FetchAvatarOnlineAsync(string steam64, CancellationToken ct)
    {
        try
        {
            if (_http is null)
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = true };
                var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SystemToolkit/1.0 (+SteamAvatarFallback)");
                Volatile.Write(ref _http, client);
            }
            string xml = await _http.GetStringAsync($"https://steamcommunity.com/profiles/{steam64}/?xml=1", ct).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);
            XElement? profile = doc.Root;
            string? s = profile?.Element("avatarIcon")?.Value;
            string? m = profile?.Element("avatarMedium")?.Value;
            string? f = profile?.Element("avatarFull")?.Value;
            return (s, m, f);
        }
        catch (Exception e)
        {
            _logger.Warn($"在线拉取 Steam 头像失败 Steam64={steam64} — {e.Message}");
            return (null, null, null);
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
