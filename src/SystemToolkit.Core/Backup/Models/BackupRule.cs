using System.Text.Json.Serialization;

namespace SystemToolkit.Core.Backup.Models;

/// <summary>备份规则配置：源路径集合、备份根、启用状态与快照保留数，持久化于 rules.json。</summary>
public sealed class BackupRule
{
    /// <summary>规则快照保留数的建议上限（UI 输入约束；引擎硬钳制见 <see cref="MaxSnapshotsCap"/>）。</summary>
    public const int MaxSnapshotsLimit = 7;

    /// <summary>快照保留数的引擎硬上限（超出配置一律钳制到该值，防止快照无限累积）。</summary>
    public const int MaxSnapshotsCap = 100;

    /// <summary>规则唯一 ID（32 位无连字符 GUID，创建时生成，导入冲突时会重分配）。</summary>
    public string RuleId { get; set; } = IdGenerator.NewId();

    /// <summary>规则显示名称。</summary>
    public string RuleName { get; set; } = "未命名规则";

    /// <summary>旧版单源路径字段，仅为旧 rules.json 反序列化兼容保留；新代码一律使用 <see cref="SourcePaths"/>。</summary>
    [Obsolete("仅用于旧版 JSON 兼容，请使用 SourcePaths")]
    public string SourcePath { get; set; } = "";

    /// <summary>备份源路径列表（支持多个文件/文件夹，多值不做单源假设）。</summary>
    public List<string> SourcePaths { get; set; } = new List<string>();

    /// <summary>源类型常量（取值见 Models.SourceTypes：folder/file），用于展示与旧格式兼容。</summary>
    public string SourceType { get; set; } = "folder";

    /// <summary>规则专属备份根目录（<see cref="UseGlobalBackupRoot"/> 为 false 时生效）。</summary>
    public string BackupRoot { get; set; } = "";

    /// <summary>是否使用全局备份根（false 时改用规则专属的 <see cref="BackupRoot"/>）。</summary>
    public bool UseGlobalBackupRoot { get; set; } = true;

    /// <summary>规则是否启用（未启用的规则不参与备份执行）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>该规则保留的快照上限（0 = 沿用全局默认），备份后超出即清理最旧快照。</summary>
    public int MaxSnapshots { get; set; } = 7;

    /// <summary>规则创建时间（本地时间 ISO 文本，导入时缺省补写）。</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>规则最后修改时间（本地时间 ISO 文本，每次保存时刷新）。</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>规则备注说明。</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// 是否启用每日定时备份（批次二；需 Task Scheduler 任务注册为前提，且应用至少启动过一次）。
    /// </summary>
    public bool EnableSchedule { get; set; }

    /// <summary>每日定时执行时间（本地时间 HH:mm 文本；空或非法视为未配置）。</summary>
    public string DailyTime { get; set; } = "";

    /// <summary>最近一次定时备份的执行日期（yyyy-MM-dd；空 = 从未执行）。「同一天不重复」与补做判断依据。</summary>
    public string LastRunDate { get; set; } = "";

    /// <summary>
    /// 备份时是否优先使用 VSS 卷影快照（批次二；需提权辅助进程支持）。
    /// 开启后可备份被占用的文件；创建快照失败时自动回退普通复制并留痕。
    /// </summary>
    public bool UseVss { get; set; }

    /// <summary>
    /// 排除模式列表（2026-09-07 新增，旧版无此能力）：每行一条，支持 <c>*</c> / <c>?</c> 通配；
    /// 匹配相对路径的任一段（目录名/文件名）或多段前缀（如 <c>bin/Debug</c>）。
    /// 典型取值：<c>node_modules</c>、<c>*.tmp</c>、<c>.git</c>、<c>bin/Debug</c>。
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = new List<string>();

    /// <summary>源路径摘要文本（「文件/文件夹 + 源路径」），仅供 UI 列表展示，不持久化。</summary>
    [JsonIgnore]
#pragma warning disable CS0618 // 本类内部消费旧版兼容属性
    public string DisplaySummary => ((SourceType == "file") ? "文件" : "文件夹") + " " + SourcePath;
#pragma warning restore CS0618

    /// <summary>规则卡片副标题：有备注时显示备注（去首尾空白），否则为空。</summary>
    [JsonIgnore]
    public string CardSubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Description))
            {
                return Description.Trim();
            }
            return "";
        }
    }

    /// <summary>
    /// 合并旧单路径字段 <see cref="SourcePath"/> 与新多路径列表 <see cref="SourcePaths"/>；
    /// 去重（按 OrdinalIgnoreCase）、清空白、修剪空白字符，保证遍历和持久化时没有「同一路径两份」的问题。
    /// </summary>
    public IReadOnlyList<string> Sources()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
#pragma warning disable CS0618
        foreach (string item in new string[1] { SourcePath }.Concat(SourcePaths))
#pragma warning restore CS0618
        {
            if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
            {
                result.Add(item.Trim());
            }
        }
        return result;
    }

    /// <summary>计算本规则的备份基目录：有效备份根 + Rule_&lt;ID 前 8 位&gt;。</summary>
    public string BackupBaseDir(string globalRoot)
    {
        return Path.Combine(EffectiveBackupRoot(globalRoot), "Rule_" + RuleId.Substring(0, Math.Min(8, RuleId.Length)));
    }

    /// <summary>解析实际生效的备份根：规则独立根优先（非空时），否则返回全局备份根。</summary>
    public string EffectiveBackupRoot(string globalRoot)
    {
        if (!UseGlobalBackupRoot && !string.IsNullOrWhiteSpace(BackupRoot))
        {
            return BackupRoot.Trim();
        }
        return globalRoot;
    }

    /// <summary>
    /// 回溯兼容：历史版本对同一条规则可能写出「Rule_xxxx_SUFFIX」形式的备份目录（后缀为 8 位规则 ID），
    /// 主 Restore 入口找不到新规范的 Rule_ 目录时，会拿本方法枚举所有兼容的旧目录用于还原。
    /// 返回值不包含当前规范的主目录（调用方自行处理）。
    /// </summary>
    public List<string> LegacyBaseDirs(string globalRoot)
    {
        string root = EffectiveBackupRoot(globalRoot);
        string canonical = "Rule_" + RuleId.Substring(0, Math.Min(8, RuleId.Length));
        string legacySuffix = "_" + RuleId.Substring(0, Math.Min(8, RuleId.Length));
        var legacyDirs = new List<string>();
        if (!Directory.Exists(root))
        {
            return legacyDirs;
        }
        try
        {
            string[] directories = Directory.GetDirectories(root);
            foreach (string dir in directories)
            {
                string fileName = Path.GetFileName(dir);
                if (fileName != canonical && fileName.EndsWith(legacySuffix, StringComparison.Ordinal))
                {
                    legacyDirs.Add(dir);
                }
            }
        }
        catch
        {
        }
        return legacyDirs;
    }
}
