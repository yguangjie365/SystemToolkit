using System.Text.Json;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>备份规则库：rules.json 的加载/保存与规则的增删改、启停、排序、导入导出。</summary>
public sealed class RuleManager
{
    /// <summary>规则库文件名（位于配置目录下）。</summary>
    public const string RulesFileName = "rules.json";

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _rulesFile;

    private readonly List<BackupRule> _rules = new List<BackupRule>();

    /// <summary>串行化全部读写的实例锁（REVIEW-3 S-2：启动补做后台线程与 UI 线程并发
    /// 增删/MarkRun，无锁 List 会抛 InvalidOperationException 或丢失更新）。</summary>
    private readonly object _gate = new();

    private readonly ILogger _logger;

    /// <summary>当前内存中的全部规则（列表顺序即 UI 展示顺序）。返回快照副本——调用方
    /// 遍历期间规则库仍可被并发修改，迭代内部 List 会抛异常（REVIEW-3 S-2）。</summary>
    public IReadOnlyList<BackupRule> All
    {
        get
        {
            lock (_gate)
            {
                return _rules.ToList();
            }
        }
    }

    /// <summary>创建规则库并立即加载；configDir 缺省为 %APPDATA% 下 SystemToolkit/rules（老用户首次自动迁移 FileBackupTool 旧规则库）。</summary>
    public RuleManager(string? configDir = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        if (configDir == null)
        {
            configDir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SystemToolkit", "rules");
            _rulesFile = Path.Combine(configDir, "rules.json");
            MigrateLegacyRulesFile(configDir); // 仅默认目录迁移：测试注入的隔离目录绝不掺入真机数据
        }
        else
        {
            _rulesFile = Path.Combine(configDir, "rules.json");
        }
        Load();
    }

    /// <summary>
    /// 一次性迁移：旧应用（FileBackupTool）目录存在 rules.json 且新位置缺失时原样复制，
    /// 保留老用户已有规则；新位置已有数据则不动。
    /// </summary>
    private void MigrateLegacyRulesFile(string newConfigDir)
    {
        try
        {
            if (File.Exists(_rulesFile))
            {
                return;
            }

            string legacy = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "FileBackupTool", "rules.json");
            if (File.Exists(legacy))
            {
                Directory.CreateDirectory(newConfigDir);
                File.Copy(legacy, _rulesFile);
                _logger.Info($"已从旧应用目录迁移规则库：{legacy} → {_rulesFile}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("规则库迁移失败（不影响新建与使用）", ex);
        }
    }

    /// <summary>丢弃内存状态并从磁盘重新加载规则。</summary>
    public void Reload()
    {
        Load();
    }

    /// <summary>从 rules.json 加载规则；文件缺失时保持空库，损坏时备份原文件后重建。</summary>
    public void Load()
    {
        lock (_gate)
        {
            _rules.Clear();
            if (!File.Exists(_rulesFile))
            {
                return;
            }
            try
            {
                List<BackupRule> list = JsonSerializer.Deserialize<List<BackupRule>>(File.ReadAllText(_rulesFile), JsonOpts)!;
                if (list != null)
                {
                    _rules.AddRange(list);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string destFileName = $"{_rulesFile}.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                    File.Copy(_rulesFile, destFileName);
                }
                catch
                {
                }
                _logger.Warn("规则库加载失败，已备份重建：" + ex.Message);
            }
        }
    }

    /// <summary>把全部规则原子写入 rules.json；失败抛 <see cref="RuleException"/>。</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                string contents = JsonSerializer.Serialize(_rules, JsonOpts);
                // 原子写入：避免写入过程中断留下半截 JSON（统一实现见 AtomicFile）
                AtomicFile.WriteAllText(_rulesFile, contents);
            }
            catch (Exception ex)
            {
                throw new RuleException("保存规则库失败：" + ex.Message);
            }
        }
    }

    /// <summary>枚举已启用的规则（备份执行入口只消费启用的规则）。</summary>
    public IEnumerable<BackupRule> EnabledRules()
    {
        lock (_gate)
        {
            return _rules.Where((BackupRule r) => r.Enabled).ToList();
        }
    }

    /// <summary>按 ID 查找规则；不存在时返回 null。</summary>
    public BackupRule? Get(string ruleId)
    {
        lock (_gate)
        {
            return _rules.FirstOrDefault((BackupRule r) => r.RuleId == ruleId);
        }
    }

    /// <summary>新增规则，或按 ID 原地替换同名 ID 的既有规则，随后立即保存。</summary>
    public void Add(BackupRule rule)
    {
        lock (_gate)
        {
            rule.UpdatedAt = Now();
            int num = _rules.FindIndex((BackupRule r) => r.RuleId == rule.RuleId);
            if (num >= 0)
            {
                _rules[num] = rule;
            }
            else
            {
                _rules.Add(rule);
            }
            Save();
        }
    }

    /// <summary>更新规则（语义同 <see cref="Add"/>：按 ID 原地替换并保存）。</summary>
    public void Update(BackupRule rule)
    {
        Add(rule);
    }

    /// <summary>按 ID 删除规则并保存；命中返回 true，未找到返回 false。</summary>
    public bool Remove(string ruleId)
    {
        lock (_gate)
        {
            int count = _rules.Count;
            _rules.RemoveAll((BackupRule r) => r.RuleId == ruleId);
            if (_rules.Count != count)
            {
                Save();
                return true;
            }
            return false;
        }
    }

    /// <summary>设置规则启用状态并保存；规则不存在时静默忽略。</summary>
    public void SetEnabled(string ruleId, bool enabled)
    {
        lock (_gate)
        {
            BackupRule? backupRule = Get(ruleId);
            if (backupRule != null)
            {
                backupRule.Enabled = enabled;
                backupRule.UpdatedAt = Now();
                Save();
            }
        }
    }

    /// <summary>记录定时备份执行日期（LastRunDate，yyyy-MM-dd）并保存；规则不存在时静默忽略。</summary>
    public void MarkRun(string ruleId, string runDate)
    {
        lock (_gate)
        {
            BackupRule? rule = Get(ruleId);
            if (rule is not null)
            {
                rule.LastRunDate = runDate;
                rule.UpdatedAt = Now();
                Save();
            }
        }
    }

    /// <summary>按给定 ID 顺序重排规则；未列出的规则保持原相对顺序排在末尾，随后保存。</summary>
    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        lock (_gate)
        {
            var hashSet = new HashSet<string>(orderedIds);
            var list = new List<BackupRule>(orderedIds.Count);
            foreach (string orderedId in orderedIds)
            {
                BackupRule backupRule = Get(orderedId)!;
                if (backupRule != null)
                {
                    list.Add(backupRule);
                }
            }
            foreach (BackupRule rule in _rules)
            {
                if (!hashSet.Contains(rule.RuleId))
                {
                    list.Add(rule);
                }
            }
            _rules.Clear();
            _rules.AddRange(list);
            Save();
        }
    }

    /// <summary>导出指定规则到 JSON 文件（容器格式，非 .json 后缀自动补全，原子写入）。</summary>
    /// <param name="ruleIds">要导出的规则 ID 集合；为空抛 <see cref="RuleException"/>。</param>
    /// <param name="targetFile">目标文件路径。</param>
    /// <returns>(实际导出规则数, 实际写入的目标路径)。</returns>
    public (int Count, string Target) Export(IEnumerable<string> ruleIds, string targetFile)
    {
        var ids = new HashSet<string>(ruleIds);
        var list = _rules.Where((BackupRule r) => ids.Contains(r.RuleId)).ToList();
        if (list.Count == 0)
        {
            throw new RuleException("没有可导出的规则。");
        }
        var value = new RuleExportContainer
        {
            ExportedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Rules = list
        };
        // 【核实报告 S2】EndsWith 会让 "rules.json.exe" 误判为已带扩展名——改用 GetExtension 严格判定
        string text = (System.IO.Path.GetExtension(targetFile).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? targetFile
            : (targetFile + ".json"));
        try
        {
            // 【核实报告 W1】导出是用户主动备份动作，写半截 = 备份价值丢失——走原子写入
            AtomicFile.WriteAllText(text, JsonSerializer.Serialize(value, JsonOpts));
        }
        catch (Exception ex)
        {
            throw new RuleException("导出规则失败：" + ex.Message);
        }
        return (Count: list.Count, Target: text);
    }

    /// <summary>从 JSON 文件导入规则（兼容带元数据的容器格式与裸规则数组）；至少成功一条时保存。</summary>
    /// <param name="sourceFile">导入文件路径。</param>
    /// <returns>(成功导入的规则数, 逐条错误消息列表)。</returns>
    public (int Ok, List<string> Errors) Import(string sourceFile)
    {
        if (!File.Exists(sourceFile))
        {
            throw new RuleException("导入文件不存在：" + sourceFile);
        }
        string json;
        try
        {
            json = File.ReadAllText(sourceFile);
        }
        catch (Exception ex)
        {
            throw new RuleException("读取导入文件失败：" + ex.Message);
        }
        List<BackupRule> imported;
        try
        {
            // 兼容两种格式：带元数据的 RuleExportContainer 容器，或裸规则数组（直接拷贝 share-rules 时常见）
            imported = (!json.TrimStart().StartsWith("["))
                ? (JsonSerializer.Deserialize<RuleExportContainer>(json, JsonOpts)?.Rules ?? new List<BackupRule>())
                : (JsonSerializer.Deserialize<List<BackupRule>>(json, JsonOpts) ?? new List<BackupRule>());
        }
        catch (Exception ex)
        {
            throw new RuleException("导入文件不是合法的 JSON：" + ex.Message);
        }
        var errors = new List<string>();
        int succeeded = 0;
        List<BackupRule> currentSnapshot;
        lock (_gate)
        {
            currentSnapshot = _rules.ToList();
        }
        var byId = currentSnapshot.ToDictionary((BackupRule r) => r.RuleId);
        var processedIds = new HashSet<string>();
        for (int i = 0; i < imported.Count; i++)
        {
            lock (_gate)
            {
                BackupRule rule = imported[i];
                if (string.IsNullOrWhiteSpace(rule.RuleName) || rule.Sources().Count == 0)
                {
                    errors.Add($"第 {i + 1} 条规则无效：名称或源路径为空");
                    continue;
                }
                if (byId.ContainsKey(rule.RuleId) && !processedIds.Contains(rule.RuleId))
                {
                    // 同 ID 仅首次命中视为「原地更新」，后续同 ID 另起新 ID 追加（避免本次导入互相覆盖）
                    byId[rule.RuleId] = rule;
                    int existingIndex = _rules.FindIndex((BackupRule r) => r.RuleId == rule.RuleId);
                    if (existingIndex >= 0)
                    {
                        _rules[existingIndex] = rule;
                    }
                    processedIds.Add(rule.RuleId);
                }
                else
                {
                    // 导入批次内出现重复 ID，或与既有规则重复 → 重新分配 ID，保证导入幂等
                    if (byId.ContainsKey(rule.RuleId) || processedIds.Contains(rule.RuleId))
                    {
                        rule.RuleId = IdGenerator.NewId();
                    }
                    // 与已有规则源集合完全相同 → 名称后追加「（导入）」区分，避免 UI 上两条看起来一模一样
                    var sourcesSet = new HashSet<string>(rule.Sources(), StringComparer.OrdinalIgnoreCase);
                    if (_rules.Any((BackupRule r) => r.RuleId != rule.RuleId && new HashSet<string>(r.Sources(), StringComparer.OrdinalIgnoreCase).SetEquals(sourcesSet)))
                    {
                        rule.RuleName += "（导入）";
                    }
                    if (string.IsNullOrEmpty(rule.CreatedAt))
                    {
                        rule.CreatedAt = Now();
                    }
                    rule.UpdatedAt = Now();
                    _rules.Add(rule);
                    byId[rule.RuleId] = rule;
                    processedIds.Add(rule.RuleId);
                }
                succeeded++;
            }
        }
        if (succeeded > 0)
        {
            Save();
        }
        return (Ok: succeeded, Errors: errors);
    }

    private static string Now()
    {
        return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    }
}
