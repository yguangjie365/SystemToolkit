using System.Text.Json;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>备份模块专用配置（自旧工程 AppSettings 裁剪：仅保留备份域字段，剥离音乐/互传/驱动混入项）。</summary>
public sealed class BackupAppSettings
{
    /// <summary>全局备份根目录（空 = 动态挑选剩余空间最大的固定盘）。</summary>
    public string BackupRoot { get; set; } = "";

    /// <summary>默认快照保留数（规则可覆盖）。</summary>
    public int MaxSnapshots { get; set; } = 7;

    /// <summary>并行复制工作线程数（钳 1..8）。</summary>
    public int MaxWorkers { get; set; } = 4;

    /// <summary>
    /// 恢复时的默认冲突处理策略（2026-09-07 新增，对齐旧版 AppSettings.RestoreConflictPolicy）。
    /// 取值见 <see cref="SystemToolkit.Core.Backup.Contracts.ConflictPolicy"/>：
    /// ask / overwrite / rename / skip；非法值一律钳回 rename。
    /// </summary>
    public string DefaultConflictPolicy { get; set; } = "rename";
}

/// <summary>
/// 备份配置服务：读写 <c>%APPDATA%/SystemToolkit/backup/settings.json</c>（snake_case + 原子写）。
/// 缺失/损坏时重建默认值；损坏文件备份为 .corrupt_* 后重写，绝不静默丢弃。
/// </summary>
public sealed class BackupConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 配置以 snake_case 字段持久化（.NET 属性 PascalCase 需映射）
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _configDir;
    private readonly string _settingsFile;
    private readonly Action<string> _log;

    /// <summary>当前生效的配置值（Load 后可用；Save 时序列化为 settings.json）。</summary>
    public BackupAppSettings Settings { get; private set; } = new();

    /// <summary>创建配置服务；<paramref name="configDir"/> 缺省 %APPDATA%\SystemToolkit\backup（测试注入临时目录）。</summary>
    public BackupConfigService(string? configDir = null, Action<string>? log = null)
    {
        _configDir = configDir ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SystemToolkit", "backup");
        _settingsFile = Path.Combine(_configDir, "settings.json");
        _log = log ?? (msg => { });
    }

    /// <summary>配置目录绝对路径（settings.json 所在位置；恢复引擎也将其列为受保护目录）。</summary>
    public string ConfigDir => _configDir;

    /// <summary>加载配置；缺失或损坏时使用默认值（损坏时备份原文件留痕）。</summary>
    public void Load()
    {
        Directory.CreateDirectory(_configDir);
        if (!File.Exists(_settingsFile))
        {
            EnsureBackupRoot();
            Save();
            return;
        }

        try
        {
            BackupAppSettings? data = JsonSerializer.Deserialize<BackupAppSettings>(File.ReadAllText(_settingsFile), JsonOpts);
            if (data is not null)
            {
                Settings = Sanitize(data);
            }
        }
        catch (Exception ex)
        {
            // 损坏：备份后重建（禁止静默丢弃用户配置）
            try
            {
                string bak = $"{_settingsFile}.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                File.Copy(_settingsFile, bak, overwrite: true);
                _log($"配置文件损坏，已备份为 {Path.GetFileName(bak)}：{ex.Message}");
            }
            catch
            {
                _log($"配置文件损坏且备份失败，使用默认值：{ex.Message}");
            }

            Settings = new BackupAppSettings();
            EnsureBackupRoot();
            Save();
        }
    }

    /// <summary>保存配置（原子写）。</summary>
    public void Save()
    {
        Directory.CreateDirectory(_configDir);
        AtomicFile.WriteAllText(_settingsFile, JsonSerializer.Serialize(Settings, JsonOpts));
    }

    /// <summary>动态挑选默认备份根：剩余空间最大的固定盘根下；不可用时兜底「文档\SystemToolkit\backup」。</summary>
    public void EnsureBackupRoot()
    {
        if (!string.IsNullOrWhiteSpace(Settings.BackupRoot))
        {
            return;
        }

        Settings.BackupRoot = PickDefaultBackupRoot();
    }

    /// <summary>挑选剩余空间最大的固定盘（驱动器类型 Fixed），无可用盘时兜底用户文档目录。</summary>
    public static string PickDefaultBackupRoot()
    {
        try
        {
            long best = 0;
            string? bestRoot = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                try
                {
                    long free = drive.AvailableFreeSpace;
                    if (free > best)
                    {
                        best = free;
                        bestRoot = drive.Name;
                    }
                }
                catch
                {
                    // 单盘查询失败跳过（如刚弹出的读卡器）
                }
            }

            if (bestRoot is not null)
            {
                return Path.Combine(bestRoot, "SystemToolkitBackup");
            }
        }
        catch
        {
            // 枚举驱动器整体失败走兜底
        }

        return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            "SystemToolkit", "backup");
    }

    /// <summary>钳制非法值（负数/越界），语义对齐旧 BackupConfigService.Sanitize。</summary>
    private static BackupAppSettings Sanitize(BackupAppSettings s)
    {
        s.MaxSnapshots = Math.Clamp(s.MaxSnapshots, 1, 100);
        s.MaxWorkers = Math.Clamp(s.MaxWorkers, 1, 8);
        s.DefaultConflictPolicy = (s.DefaultConflictPolicy ?? "").Trim().ToLowerInvariant() switch
        {
            "ask" => "ask",
            "overwrite" => "overwrite",
            "skip" => "skip",
            _ => "rename", // 未知/空一律回退到最安全的策略
        };
        return s;
    }
}
