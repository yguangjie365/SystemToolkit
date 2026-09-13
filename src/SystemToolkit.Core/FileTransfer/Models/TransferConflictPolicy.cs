namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 目标目录已存在同名文件时的处理策略（协议 §4.4 第 3 条：**接收端 UI 预选**，默认「自动改名」）。
/// <para>
/// 🔴 四个策略里只有 <see cref="Overwrite"/> 会**破坏已有文件**，因此默认值必须且只能是
/// <see cref="Rename"/>——本项目对已有文件的一贯口径是「绝不覆盖」（备份恢复域同理）。
/// </para>
/// <para>
/// ⚠️ 本枚举是本域专有的，**不要**与备份域的 <c>SystemToolkit.Core.Backup.Contracts.ConflictPolicy</c>
/// 混用：两者语义相近但取值与判据不同（备份域针对目录树合并，本域针对单文件落定）。
/// </para>
/// </summary>
public enum TransferConflictPolicy
{
    /// <summary>自动改名：<c>name (1).ext</c>（默认；绝不覆盖）。</summary>
    Rename = 0,

    /// <summary>询问：由接收确认门弹窗让用户逐次选择。**无法询问时降级为 <see cref="Rename"/>**。</summary>
    Ask = 1,

    /// <summary>跳过：不接收这一份，已存在的文件原样保留。</summary>
    Skip = 2,

    /// <summary>覆盖：用收到的内容替换已存在的同名文件（破坏性，需用户显式选择）。</summary>
    Overwrite = 3,
}

/// <summary>单次落定的实际动作（已把策略解析成确定结果）。</summary>
public enum ConflictResolution
{
    /// <summary>目标不存在，直接落定。</summary>
    Fresh = 0,

    /// <summary>目标已存在 → 改名落定（<c>name (n).ext</c>）。</summary>
    Renamed = 1,

    /// <summary>目标已存在 → 覆盖它。</summary>
    Overwrite = 2,

    /// <summary>目标已存在 → 不写入（保留原文件）。</summary>
    Skip = 3,
}

/// <summary>一次落定计划：动作 + 最终目标路径。</summary>
/// <param name="Kind">动作。</param>
/// <param name="TargetPath">最终目标路径（<see cref="ConflictResolution.Skip"/> 时为被跳过的那个已存在文件）。</param>
public readonly record struct ConflictPlan(ConflictResolution Kind, string TargetPath)
{
    /// <summary>本次是否会真正写入文件（跳过为 false）。</summary>
    public bool WritesFile => Kind != ConflictResolution.Skip;
}
