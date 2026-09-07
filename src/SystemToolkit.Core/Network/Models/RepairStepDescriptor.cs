namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// 一项修复操作的描述元数据（不可变）。
/// <para>
/// <see cref="RequiresAdmin"/> 决定非提权时是否置灰；<see cref="RequiresReboot"/> 决定
/// 执行成功后是否提示重启，以及是否被排除在「一键安全修复」之外（设计文档 §4.3）。
/// </para>
/// </summary>
public sealed record RepairStepDescriptor(
    string Id,
    string Title,
    string Description,
    bool RequiresAdmin,
    bool RequiresReboot,
    string CommandPreview);
