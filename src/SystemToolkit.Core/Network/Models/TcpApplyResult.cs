namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// TCP 调优应用结果摘要（【核实报告 N12】）：服务实际写入了哪些项、跳过了哪些项
/// 及原因——与 VM 确认摘要对齐，消除「确认 4 项、实际写 2 项」的不一致。
/// 审查 O4/O10：新增 Failed——netsh 写入失败项（退出码非 0）不再被吞，还原流程据此补 Verify 判定。
/// </summary>
public sealed record TcpApplyResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Failed);
