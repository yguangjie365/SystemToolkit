using System.Text.Json;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>快照管理：目录布局、manifest/meta 读写、上限清理、孤儿清理。</summary>
public sealed class SnapshotManager
{
    /// <summary>快照完整清单文件名（含 Files 文件明细）。</summary>
    public const string ManifestName = "manifest.json";

    /// <summary>轻量元数据文件名（不含 Files 明细，列表页快速读取用）。</summary>
    public const string MetaName = "meta.json";

    /// <summary>快照内的数据文件子目录名。</summary>
    public const string FilesDir = "files";

    /// <summary>规则目录下存放各快照的子目录名。</summary>
    public const string SnapshotsDir = "snapshots";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _ruleBase;
    private readonly List<string> _extraBases = [];
    private readonly ILogger _logger;

    /// <summary>以规则备份基目录构造；通常经 <see cref="FromRule"/> 创建，以同时纳入旧版遗留目录。</summary>
    public SnapshotManager(string ruleBase, ILogger? logger = null)
    {
        _ruleBase = ruleBase;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>按规则构造：主目录为 Rule_&lt;ID8&gt;，同时发现旧版遗留目录。</summary>
    public static SnapshotManager FromRule(BackupRule rule, string globalRoot, ILogger? logger = null)
    {
        var mgr = new SnapshotManager(rule.BackupBaseDir(globalRoot), logger);
        mgr._extraBases.AddRange(rule.LegacyBaseDirs(globalRoot));
        return mgr;
    }

    /// <summary>本规则的备份基目录（Rule_xxx 形式的主目录）。</summary>
    public string RuleBase => _ruleBase;

    /// <summary>本规则的快照根目录（基目录 + snapshots 子目录）。</summary>
    public string SnapRoot => Path.Combine(_ruleBase, SnapshotsDir);

    private IEnumerable<string> AllBases()
    {
        yield return _ruleBase;
        foreach (string b in _extraBases)
            yield return b;
    }

    /// <summary>判断目录是否位于本规则的任一快照基目录（主 SnapRoot 或旧版遗留目录）之下。</summary>
    public bool IsSnapshotDirAllowed(string dir)
        => new[] { SnapRoot }.Concat(_extraBases).Any(baseDir => PathUtil.IsUnder(dir, baseDir));

    // ------------------------------------------------------------------
    // 快照目录枚举（按时间戳降序，正确处理同秒 _N 序号）
    // ------------------------------------------------------------------
    /// <summary>枚举主目录与旧版遗留目录下全部快照目录，按创建时间新→旧排序（同秒 _N 序号参与数字比较）。</summary>
    public List<string> AllSnapshotDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        foreach (string baseDir in AllBases())
        {
            string snapRoot = Path.Combine(baseDir, SnapshotsDir);
            if (!Directory.Exists(snapRoot))
                continue;
            try
            {
                foreach (string d in Directory.GetDirectories(snapRoot))
                {
                    string name = Path.GetFileName(d);
                    // 时间戳：纯数字 或 数字_序号
                    if (IsTimestampName(name) && seen.Add(d))
                        dirs.Add(d);
                }
            }
            catch { /* 跳过不可读 */ }
        }
        // 按 (时间戳, 数字序号) 降序：先比时间戳，再比序号（数字，避免 _10 排 _2 前）
        dirs.Sort((a, b) =>
        {
            (string Base, int Seq) ka = SortKey(a);
            (string Base, int Seq) kb = SortKey(b);
            int cmp = string.CompareOrdinal(kb.Base, ka.Base);
            return cmp != 0 ? cmp : kb.Seq.CompareTo(ka.Seq);
        });
        return dirs;
    }

    private static bool IsTimestampName(string name)
    {
        if (name.Length < 15)
            return false;
        string baseName = name;
        // 同秒冲突形如 20260810_100000_2：最后一个下划线在 idx>8 处且后面是序号
        int idx = name.LastIndexOf('_');
        if (idx > 8 && int.TryParse(name[(idx + 1)..], out _))
            baseName = name[..idx];
        return baseName.Length == 15 && baseName[8] == '_' &&
               baseName[..8].All(char.IsDigit) && baseName[9..].All(char.IsDigit);
    }

    private static (string Base, int Seq) SortKey(string dir)
    {
        string name = Path.GetFileName(dir);
        string baseName = name;
        int seq = 0;
        // 同秒冲突形如 20260810_100000_2：取序号做数字比较，避免 _10 排在 _2 前
        int idx = name.LastIndexOf('_');
        if (idx > 8 && int.TryParse(name[(idx + 1)..], out int n) && name[..idx].Length == 15)
        {
            baseName = name[..idx];
            seq = n;
        }
        return (baseName, seq);
    }

    // ------------------------------------------------------------------
    // manifest / meta 读写
    // ------------------------------------------------------------------
    /// <summary>读取快照完整清单 manifest.json；缺失或损坏时返回 null（并留痕 Warn）。</summary>
    public SnapshotInfo? ReadSnapshot(string snapDir)
    {
        string mf = Path.Combine(snapDir, ManifestName);
        if (!File.Exists(mf))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllText(mf), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.Warn($"读取 manifest 失败：{snapDir}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>轻量读取：优先 meta.json，缺则解析 manifest 头部（不含 files）。</summary>
    public SnapshotInfo? ReadSnapshotLight(string snapDir)
    {
        string meta = Path.Combine(snapDir, MetaName);
        try
        {
            if (File.Exists(meta))
            {
                SnapshotInfo? info = JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllText(meta), JsonOpts);
                if (info is not null)
                {
                    info.Files = [];
                    return info;
                }
            }
        }
        catch { /* 回退到 manifest */ }

        string mf = Path.Combine(snapDir, ManifestName);
        if (!File.Exists(mf))
            return null;
        try
        {
            SnapshotInfo? info = JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllText(mf), JsonOpts);
            if (info is not null)
                info.Files = [];
            return info;
        }
        catch (Exception ex)
        {
            _logger.Warn($"读取快照元数据失败：{snapDir}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>取最新的可读快照（按新→旧顺序返回首个 manifest 可解析者）；无快照返回 null。</summary>
    public SnapshotInfo? LatestSnapshot()
    {
        foreach (string d in AllSnapshotDirs())
        {
            SnapshotInfo? info = ReadSnapshot(d);
            if (info is not null)
                return info;
        }
        return null;
    }

    /// <summary>返回所有快照的轻量元数据（不含 files），新→旧。跳过损坏项。</summary>
    public List<SnapshotInfo> SnapshotsLight()
    {
        var result = new List<SnapshotInfo>();
        foreach (string d in AllSnapshotDirs())
        {
            SnapshotInfo? info = ReadSnapshotLight(d);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    /// <summary>写入快照完整清单 manifest.json（原子写）。</summary>
    public void WriteSnapshot(string snapDir, SnapshotInfo info)
    {
        WriteJsonAtomic(Path.Combine(snapDir, ManifestName), info);
    }

    /// <summary>写入轻量元数据 meta.json（剔除 Files 明细，原子写）。</summary>
    public void WriteMeta(string snapDir, SnapshotInfo info)
    {
        var light = new SnapshotInfo
        {
            SnapshotId = info.SnapshotId,
            RuleId = info.RuleId,
            RuleName = info.RuleName,
            CreatedAt = info.CreatedAt,
            SourcePath = info.SourcePath,
            SourcePaths = info.SourcePaths,
            BackupPath = info.BackupPath,
            FileCount = info.FileCount,
            TotalSize = info.TotalSize,
            Status = info.Status,
            ChecksumStatus = info.ChecksumStatus,
        };
        WriteJsonAtomic(Path.Combine(snapDir, MetaName), light);
    }

    /// <summary>原子写入 JSON（先写 .tmp 再替换），复用统一的 <see cref="AtomicFile"/> 实现。</summary>
    public static void WriteJsonAtomic(string path, object payload)
        => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOpts));

    // ------------------------------------------------------------------
    // 目录创建 / 删除 / 上限清理
    // ------------------------------------------------------------------
    /// <summary>创建新快照目录（yyyyMMdd_HHmmss 命名，同秒冲突追加 _N 序号）及 files 子目录，返回目录完整路径。</summary>
    public string CreateSnapshotDir()
    {
        Directory.CreateDirectory(SnapRoot);
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string snap = Path.Combine(SnapRoot, ts);
        int i = 1;
        while (Directory.Exists(snap))
        {
            snap = Path.Combine(SnapRoot, $"{ts}_{i}");
            i++;
        }
        Directory.CreateDirectory(Path.Combine(snap, FilesDir));
        return snap;
    }

    /// <summary>递归删除一个快照目录；目录不存在时静默返回，删除失败抛 IOException。</summary>
    public void DeleteSnapshot(string snapDir)
    {
        if (!Directory.Exists(snapDir))
            return;
        try
        {
            Directory.Delete(snapDir, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException($"删除快照失败：{ex.Message}");
        }
    }

    /// <summary>超过上限删除最旧快照。返回被删除目录列表。</summary>
    public List<string> EnforceLimit(int maxSnapshots)
    {
        List<string> dirs = AllSnapshotDirs();
        var removed = new List<string>();
        while (dirs.Count > Math.Max(1, maxSnapshots))
        {
            string oldest = dirs[^1];
            try
            {
                DeleteSnapshot(oldest);
                removed.Add(oldest);
            }
            catch (Exception ex)
            {
                _logger.Warn($"清理旧快照失败：{ex.Message}");
                break;
            }
            dirs.RemoveAt(dirs.Count - 1);
        }
        return removed;
    }

    /// <summary>
    /// 删除规则的全部快照及规则专属目录（含旧版遗留目录）。
    /// 返回删除的快照数；删除后规则目录树（Rule_xxx、旧版 &lt;规则名&gt;_&lt;ID8&gt;）一并清理，不留空壳。
    /// </summary>
    public int DeleteAllSnapshots()
    {
        int count = 0;
        foreach (string d in AllSnapshotDirs())
        {
            try
            { DeleteSnapshot(d); count++; }
            catch { /* 跳过 */ }
        }
        // 快照删完后清理规则专属目录本身：否则会残留空的 Rule_xxx/snapshots 空壳文件夹
        foreach (string baseDir in AllBases())
        {
            try
            {
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, recursive: true);
            }
            catch { /* 目录可能被占用（如资源管理器锁定），保留由用户手动清理 */ }
        }
        return count;
    }

    // ------------------------------------------------------------------
    // 孤儿清理
    // ------------------------------------------------------------------
    /// <summary>
    /// 清理无元数据的孤儿快照目录（上次备份中断遗留的半成品，无 manifest/meta 即不可恢复）。
    /// 只删除<strong>不含任何数据文件</strong>的空壳目录；目录内一旦有实际数据，说明
    /// “数据已复制但元数据未写入”，必须保留供人工检查，绝不静默删除用户数据
    /// （与 BackupService 的失败保留策略严格对齐，见 BackupService 异常分支）。
    /// 返回清理数量。用于应用启动时自动打扫，避免孤儿目录累积。
    /// </summary>
    /// <param name="onPreserved">保留含数据目录时的告警回调（仅写文件日志用户看不见，调用方经此上 UI）。</param>
    public int CleanupOrphans(Action<string>? onPreserved = null)
    {
        int count = 0;
        foreach (string d in AllSnapshotDirs())
        {
            string mf = Path.Combine(d, ManifestName);
            string meta = Path.Combine(d, MetaName);
            if (File.Exists(mf) || File.Exists(meta))
            {
                continue; // 有元数据，是正常快照，不是孤儿
            }

            // 已含数据：按 BackupService 的保留策略处理，写入告警由调用方呈现给用户
            if (HasAnyDataFile(d))
            {
                _logger.Warn($"发现未完成的快照目录（已有数据但无元数据），已保留供人工检查：{d}");
                // 文件日志对用户不可见：经回调把保留告警上报到调用方（如 UI 日志面板）
                onPreserved?.Invoke($"保留快照目录（含数据但无元数据，请人工检查）：{d}");
                continue;
            }

            try
            {
                Directory.Delete(d, recursive: true);
                count++;
                _logger.Warn($"已清理空的孤儿快照目录：{d}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"清理孤儿快照目录失败：{d}（{ex.Message}）");
            }
        }
        return count;
    }

    /// <summary>
    /// 目录（含子目录）中是否存在任意文件。枚举失败（权限/占用）时保守返回 true，
    /// 以免误删无法确认内容的数据。
    /// </summary>
    private static bool HasAnyDataFile(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return true;
        }
    }
}
