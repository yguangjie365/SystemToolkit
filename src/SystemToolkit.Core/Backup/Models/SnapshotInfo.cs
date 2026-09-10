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

    /// <summary>
    /// 格式化的创建时间（<c>yyyy-MM-dd HH:mm:ss</c>）。
    /// <para>
    /// 目录名前 15 位恒为 <c>yyyyMMdd_HHmmss</c>；2026-09-08 起为消除创建竞态追加了
    /// <c>_随机6位</c>（再撞名时再加 <c>_序号</c>）后缀——**后缀不是时间的一部分**，
    /// 显示层一律剥离；磁盘目录名保持不变（后缀是防同名竞态的安全机制，不可删）。
    /// </para>
    /// <para>
    /// 🔴 2026-09-11 用户反馈修复：原实现只剥离「下划线后为**纯数字**」的序号，而随机 6 位
    /// 取自 <c>Path.GetRandomFileName()</c>（基 32 字符集，可能含**字母**），走不进该分支
    /// → 解析失败 → 回退原样，列表里显示成 <c>20260911_143022_a3f9c1</c> 这种带后缀的目录名。
    /// 改为**按前 15 位解析**后，与后缀形态无关。
    /// </para>
    /// <para>无法解析（旧格式 / 异常值）时回退原始 <see cref="CreatedAt"/>。</para>
    /// </summary>
    [JsonIgnore]
    public string DisplayTime
    {
        get
        {
            if (CreatedAt.Length >= 15 && CreatedAt[8] == '_'
                && CreatedAt[..15].Count(char.IsAsciiDigit) == 14)
            {
                return $"{CreatedAt[..4]}-{CreatedAt[4..6]}-{CreatedAt[6..8]} "
                     + $"{CreatedAt[9..11]}:{CreatedAt[11..13]}:{CreatedAt[13..15]}";
            }

            return CreatedAt;
        }
    }

    /// <summary>总大小的人类可读文本（经 FormatUtil 格式化）。</summary>
    [JsonIgnore]
    public string SizeText => FormatUtil.FormatSize(TotalSize);

    /// <summary>
    /// 路径列的友好显示文本。原始 <see cref="BackupPath"/> 含 <c>Rule_xxxxxxxx</c> 哈希目录，
    /// 对用户无意义。
    /// <para>
    /// 2026-09-11 用户反馈：本列只显示**规则名**——时间已在「快照时间」列完整呈现，
    /// 再拼一遍属重复且会挤压列宽。规则名缺失（旧 manifest 未写入）时回退完整路径；
    /// 完整磁盘路径在 UI 侧经 ToolTip 保留（FileBackupView 路径列）。
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string DisplayPath =>
        string.IsNullOrWhiteSpace(RuleName) ? BackupPath : RuleName;

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
