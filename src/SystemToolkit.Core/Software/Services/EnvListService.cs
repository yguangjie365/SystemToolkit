using System.Text.Json;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Software.Services;

/// <summary>环境软件清单服务：三类清单（winget/手工/驱动工具）的加载、保存、导入导出与旧配置迁移。</summary>
public sealed partial class EnvListService
{
    /// <summary>winget 清单文件名（位于 EnvDir 下）。</summary>
    public const string WingetListFile = "env_winget_list.json";

    /// <summary>手工软件清单文件名（位于 EnvDir 下）。</summary>
    public const string ManualListFile = "env_manual_list.json";

    /// <summary>驱动工具清单文件名（位于 EnvDir 下）。</summary>
    public const string DriverListFile = "env_driver_list.json";

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>旧版配置根（旧工程产品名 + Roaming，违反 02 §六统一配置根——审查 M9）。</summary>
    private static readonly string LegacyEnvDir = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "FileBackupTool", "env");

    private readonly string _envDir;

    private readonly string _driverRoot;

    private readonly Action<string> _log;

    /// <summary>清单存储根目录（统一配置根 %LOCALAPPDATA%\SystemToolkit\env）。</summary>
    public string EnvDir => _envDir;

    /// <summary>注入存储根、驱动下载根与日志回调（测试可替换）；缺省迁移旧工程配置目录。</summary>
    public EnvListService(string? envDir = null, string? driverRoot = null, Action<string>? log = null)
    {
        // 统一配置根 %LOCALAPPDATA%\SystemToolkit\env（02 §六）；旧根数据一次性迁移（审查 M9）
        _envDir = envDir ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "env");
        _driverRoot = driverRoot ?? "D:\\Drivers";
        _log = log ?? ((Action<string>)delegate (string msg)
        {
            Console.WriteLine("[env] " + msg);
        });
        if (envDir is null)
        {
            MigrateLegacyDir(LegacyEnvDir, _envDir, _log);
        }
    }

    /// <summary>一次性迁移（审查 M9）：旧根存在且新根尚无清单时，把 *.json 复制到新根。
    /// 纯文件复制，失败仅留痕不阻断（缺失清单会按默认值重建）。internal 供迁移行为单测。</summary>
    internal static void MigrateLegacyDir(string legacyDir, string newDir, Action<string>? log = null)
    {
        try
        {
            if (!Directory.Exists(legacyDir) || Directory.Exists(newDir))
            {
                return; // 无旧数据，或新根已启用（避免覆盖既有数据）
            }

            Directory.CreateDirectory(newDir);
            int copied = 0;
            foreach (string file in Directory.EnumerateFiles(legacyDir, "*.json"))
            {
                File.Copy(file, Path.Combine(newDir, Path.GetFileName(file)));
                copied++;
            }

            if (copied > 0)
            {
                log?.Invoke($"已从旧配置目录迁移 {copied} 个清单文件：{legacyDir} → {newDir}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("旧配置迁移失败（不影响使用，清单将按默认值重建）：" + ex.Message);
        }
    }

    /// <summary>按前缀保留最新 keep 份时间戳文件（审查 L14：备份/损坏备份此前无上限累积）。
    /// 仅修剪「前缀 + yyyyMMdd_HHmmss」形态——不符合形态的同前缀文件不动（防误删，测试实证）。
    /// internal 供单测。</summary>
    internal static void PruneTimestampedFiles(string dir, string prefix, int keep, Action<string>? log = null)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir, prefix + "*")
                         .Where(f =>
                         {
                             string name = Path.GetFileName(f);
                             string stamp = name.Length > prefix.Length ? name[prefix.Length..] : "";
                             return TimestampPrefixRegex.IsMatch(stamp);
                         })
                         .OrderByDescending(f => f, StringComparer.Ordinal) // 同形态下 Ordinal 序即时间序
                         .Skip(keep))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("清理历史备份失败：" + ex.Message);
        }
    }

    /// <summary>时间戳前缀形态（yyyyMMdd_HHmmss…）。</summary>
    private static readonly System.Text.RegularExpressions.Regex TimestampPrefixRegex =
        new(@"^\d{8}_\d{6}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>加载完整目录：三类清单缺哪个按空清单补齐，不抛异常。</summary>
    public EnvCatalog Load()
    {
        Directory.CreateDirectory(_envDir);
        return new EnvCatalog
        {
            Winget = LoadWinget(),
            Manual = LoadManual(),
            Driver = LoadDriver()
        };
    }

    /// <summary>把当前三类清单导出为单个 JSON 文件（用于分享/迁移）。</summary>
    public void ExportCatalog(string path)
    {
        var data = new EnvCatalog
        {
            Winget = LoadWingetOnly(),
            Manual = (File.Exists(Path.Combine(_envDir, ManualListFile)) ? LoadManual() : new List<ManualSoftware>()),
            Driver = (File.Exists(Path.Combine(_envDir, DriverListFile)) ? LoadDriver() : new List<ManualSoftware>())
        };
        SaveJson(path, data);
    }

    /// <summary>
    /// 把当前三个清单备份到 env 目录下的时间戳文件（导入覆盖前调用）。
    /// </summary>
    /// <returns>备份文件路径；失败返回 null。</returns>
    public string? BackupCatalog()
    {
        try
        {
            Directory.CreateDirectory(_envDir);
            string path = Path.Combine(_envDir, $"catalog_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            ExportCatalog(path);
            PruneTimestampedFiles(_envDir, "catalog_backup_", keep: 5, _log);
            return path;
        }
        catch (Exception ex)
        {
            _log("备份清单失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>从外部 JSON 导入目录：整体校验，任一条目违规即整体拒绝并返回 null。</summary>
    public EnvCatalog? ImportCatalog(string path)
    {
        try
        {
            EnvCatalog? catalog = JsonSerializer.Deserialize<EnvCatalog>(File.ReadAllText(path), JsonOpts);
            if (catalog is null)
            {
                _log("导入清单失败：文件内容为空或不是有效的清单 JSON");
                return null;
            }
            // S5（REVIEW-2026-08-30）：导入的外部 JSON 之前只做反序列化、无字段校验，
            // 恶意条目会直接进入清单并被 winget 参数构造 / 浏览器唤起消费。
            // 现在整体校验，任何一条违规即拒绝导入（整体拒绝比静默剔除可预测——
            // 用户应知道清单不干净，而不是导入后少了一批条目）。
            List<string> errors = EnvCatalogValidator.Validate(catalog);
            if (errors.Count > 0)
            {
                _log($"导入清单被拒绝：{errors.Count} 条数据未通过校验（疑似损坏或被篡改）：");
                foreach (string e in errors.Take(10))
                {
                    _log("  - " + e);
                }
                if (errors.Count > 10)
                {
                    _log($"  …以及另外 {errors.Count - 10} 条");
                }
                return null;
            }
            return catalog;
        }
        catch (Exception ex)
        {
            _log("导入清单失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>覆盖保存 winget 清单（原子写）。</summary>
    public void SaveWinget(IEnumerable<WingetPackage> packages)
    {
        Directory.CreateDirectory(_envDir);
        SaveJson(Path.Combine(_envDir, WingetListFile), packages.ToList());
    }

    /// <summary>覆盖保存手工软件清单（原子写）。</summary>
    public void SaveManual(IEnumerable<ManualSoftware> software)
    {
        Directory.CreateDirectory(_envDir);
        SaveJson(Path.Combine(_envDir, ManualListFile), software.ToList());
    }

    /// <summary>覆盖保存驱动工具清单（原子写）。</summary>
    public void SaveDriver(IEnumerable<ManualSoftware> software)
    {
        Directory.CreateDirectory(_envDir);
        SaveJson(Path.Combine(_envDir, DriverListFile), software.ToList());
    }

    /// <summary>仅加载 winget 清单（文件缺失返回空列表）。</summary>
    public List<WingetPackage> LoadWingetOnly()
    {
        string text = Path.Combine(_envDir, WingetListFile);
        if (!File.Exists(text))
        {
            return new List<WingetPackage>();
        }
        return LoadJson(text, () => new List<WingetPackage>());
    }

    // 旧默认清单的 winget Id → 商店 Id 映射（2026-08-29 卡片全面切 msstore 源）。
    // 仅映射经实测确认有商店上架的应用；PowerShell 7 / Chrome / Firefox / Sublime Text
    // 商店未上架，不在映射内，保持 winget 源。
    private static readonly Dictionary<string, string> MsStoreIdMigration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.VisualStudioCode"] = "XP9KHM4BK9FZ7Q",
        ["Git.Git"] = "XPDFF77QZ71XD0",
        ["7zip.7zip"] = "XPDM3X7FL84X2K",
        ["Microsoft.PowerToys"] = "XP89DCGQ3K6VLD",
        ["Tencent.WeChat"] = "XPFCKBRNFZQ62G",
        ["Tencent.QQ"] = "XP99SPMSZ082XF",
        ["PotPlayer.PotPlayer"] = "XP8BSBGQW2DKS0",
    };

    private List<WingetPackage> LoadWinget()
    {
        string text = Path.Combine(_envDir, WingetListFile);
        if (!File.Exists(text))
        {
            List<WingetPackage> list = DefaultWingetPackages();
            SaveJson(text, list);
            return list;
        }
        List<WingetPackage> loaded = LoadJson(text, () => DefaultWingetPackages());
        // 一次性迁移：旧清单里仍是 winget 源、但已确认有商店上架的应用，
        // 改写为商店 Id + msstore 源（用户后来手动添加的条目不受影响）
        bool migrated = false;
        foreach (WingetPackage pkg in loaded)
        {
            if (!pkg.IsMsStore && MsStoreIdMigration.TryGetValue(pkg.Id, out string? storeId))
            {
                pkg.Id = storeId;
                pkg.Source = "msstore";
                migrated = true;
            }
        }
        if (migrated)
        {
            SaveJson(text, loaded);
            _log("已把清单中商店有上架的应用切换为 msstore 源");
        }
        return loaded;
    }

    private List<ManualSoftware> LoadManual()
    {
        string text = Path.Combine(_envDir, ManualListFile);
        if (!File.Exists(text))
        {
            List<ManualSoftware> list = DefaultManualSoftware();
            SaveJson(text, list);
            return list;
        }
        return LoadJson(text, () => DefaultManualSoftware());
    }

    private List<ManualSoftware> LoadDriver()
    {
        string text = Path.Combine(_envDir, DriverListFile);
        if (!File.Exists(text))
        {
            // 默认驱动条目不落盘（审查 M9）：指向 driverRoot 的建议值写入用户盘会诱导误存——
            // 用户在编辑界面保存时才真正持久化
            return DefaultDriverSoftware();
        }
        return LoadJson(text, () => DefaultDriverSoftware());
    }

    private T LoadJson<T>(string file, Func<T> fallback) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOpts) ?? fallback();
        }
        catch (Exception ex)
        {
            try
            {
                string destFileName = $"{file}.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                File.Copy(file, destFileName);
                PruneTimestampedFiles(_envDir, Path.GetFileName(file) + ".corrupt_", keep: 5, _log);
            }
            catch (Exception backupEx)
            {
                // 审查 2026-09-04（P2）：备份失败也要留痕，不能静默
                _log($"清单损坏备份失败：{Path.GetFileName(file)}（{backupEx.Message}）");
            }

            T rebuilt = fallback();
            // 审查 2026-09-04（P2）：立即回写默认值，避免磁盘保持损坏态、每次加载重复走损坏分支
            try
            {
                SaveJson(file, rebuilt);
            }
            catch (Exception rewriteEx)
            {
                _log($"默认清单回写失败：{Path.GetFileName(file)}（{rewriteEx.Message}）");
            }

            _log($"清单文件损坏，已重建默认：{Path.GetFileName(file)}（{ex.Message}）");
            return rebuilt;
        }
    }

    // 由 static 改为实例方法：保存失败必须走实例注入的 _log（可落日志面板/文件），
    // Console.WriteLine 在 WPF 进程中无人可见，等于静默丢失保存失败的线索
    private void SaveJson<T>(string file, T data)
    {
        try
        {
            string contents = JsonSerializer.Serialize(data, JsonOpts);
            // 原子写入：避免写入过程中断留下半截 JSON（统一实现见 AtomicFile）
            AtomicFile.WriteAllText(file, contents);
        }
        catch (Exception ex)
        {
            _log("保存清单失败：" + Path.GetFileName(file) + "（" + ex.Message + "）");
        }
    }

}
