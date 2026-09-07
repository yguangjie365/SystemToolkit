namespace SystemToolkit.Core.Network.Models;

/// <summary>诊断步骤状态。</summary>
public enum DiagStatus
{
    /// <summary>排队中，本轮尚未执行。</summary>
    Pending,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>执行完成且通过。</summary>
    Success,
    /// <summary>执行完成但未通过（原因见 Detail）。</summary>
    Failed,

    /// <summary>前置条件不满足（如无网关可测），本轮未执行。</summary>
    Skipped,
}

/// <summary>一步诊断的结果快照。</summary>
public sealed record DiagStepResult(
    string Step,
    DiagStatus Status,
    string Detail,
    long ElapsedMs);
