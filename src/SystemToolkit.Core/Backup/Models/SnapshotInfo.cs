using System.Text.Json.Serialization;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Models;

/// <summary>快照元数据（完整版写入 manifest.json，轻量版写入 meta.json）。</summary>
public sealed class SnapshotInfo
{
    /// <summary>快照唯一 ID（规则 ID + 时间戳）。</summary>
    public string SnapshotId { get; set; } = IdGenerator.NewId();

    /// <summary>快照所属规则的 ID。</summary>
    public string RuleId { get; set; } = "";

    /// <summary>快照所属规则的名称。</summary>
    public string RuleName { get; set; } = "";

    /// <summary>快照创建时间戳（同快照目录名：yyyyMMdd_HHmmss，可带 _N 同秒序号）。</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>主源路径（旧格式单源字段；多源时存第一个源，兼容保留）。</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>备份时的全部源路径列表（恢复范围白名单的兜底来源）。</summary>
    public List<string> SourcePaths { get; set; } = new List<string>();

    /// <summary>快照 files 数据目录的绝对路径。</summary>
    public string BackupPath { get; set; } = "";

    /// <summary>快照包含的文件总数。</summary>
    public int FileCount { get; set; }

    /// <summary>快照文件总字节数。</summary>
    public long TotalSize { get; set; }

    /// <summary>快照状态常量（取值见 Models.SnapshotStatuses：success/failed）。</summary>
    public string Status { get; set; } = "success";

    /// <summary>校验状态常量（取值见 Models.ChecksumStatuses：passed/failed/skipped）。</summary>
    public string ChecksumStatus { get; set; } = "passed";

    /// <summary>文件清单明细（manifest 专用；meta.json 轻量版不含此内容）。</summary>
    public List<FileEntry> Files { get; set; } = new List<FileEntry>();

    /// <summary>源中的空目录相对路径列表（恢复后按此重建空目录结构）。</summary>
    public List<string> EmptyDirs { get; set; } = new List<string>();

    /// <summary>格式化的创建时间（yyyy-MM-dd HH:mm:ss；目录名带同秒序号时剥离后解析，失败回退原始 CreatedAt）。</summary>
    [JsonIgnore]
    public string DisplayTime
    {
        get
        {
            string text = CreatedAt;
            if (text.Contains('_'))
            {
                int num = text.LastIndexOf('_');
                if (num > 8 && int.TryParse(text.Substring(num + 1), out int _) && text.Substring(0, num).Length == 15)
                {
                    text = text.Substring(0, num);
                }
            }
            if (text.Length == 15 && text[8] == '_')
            {
                return $"{text.Substring(0, 4)}-{text.Substring(4, 2)}-{text.Substring(6, 2)} {text.Substring(9, 2)}:{text.Substring(11, 2)}:{text.Substring(13, 2)}";
            }
            return CreatedAt;
        }
    }

    /// <summary>总大小的人类可读文本（经 FormatUtil 格式化）。</summary>
    [JsonIgnore]
    public string SizeText => FormatUtil.FormatSize(TotalSize);

    /// <summary>
    /// 路径列的友好显示文本：规则名 · 快照时间。原始 BackupPath 含 Rule_xxxxxxxx 哈希目录，
    /// 对用户无意义且与「快照时间」列 80% 前缀重复；规则名缺失（旧 manifest 未写入）时回退完整路径。
    /// 完整路径在 UI 侧经 ToolTip 保留（FileBackupView 路径列）。
    /// </summary>
    [JsonIgnore]
    public string DisplayPath =>
        string.IsNullOrWhiteSpace(RuleName) ? BackupPath : $"{RuleName} · {DisplayTime}";

    /// <summary>快照状态的中文显示文本（成功/失败）。</summary>
    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (!(Status == "success"))
            {
                return "失败";
            }
            return "成功";
        }
    }

    /// <summary>校验状态的中文显示文本（通过/失败/未校验）。</summary>
    [JsonIgnore]
    public string ChecksumText
    {
        get
        {
            string checksumStatus = ChecksumStatus;
            if (!(checksumStatus == "passed"))
            {
                if (checksumStatus == "failed")
                {
                    return "失败";
                }
                return "未校验";
            }
            return "通过";
        }
    }
}
