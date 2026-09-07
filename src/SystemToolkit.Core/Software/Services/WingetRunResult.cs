namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// 一次 winget 命令执行的结果。
/// </summary>
/// <param name="ExitCode">进程退出码（0 = 成功）。</param>
/// <param name="Success">是否成功（退出码为 0）。</param>
public sealed record WingetRunResult(int ExitCode, bool Success);
