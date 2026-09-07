namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// TCP 调优应用结果摘要（【核实报告 N12】）：服务实际写入了哪些项、跳过了哪些项
/// 及原因——与 VM 确认摘要对齐，消除「确认 4 项、实际写 2 项」的不一致。
/// </summary>
public sealed record TcpApplyResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped);
