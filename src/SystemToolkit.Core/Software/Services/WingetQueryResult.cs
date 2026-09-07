namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// winget 查询结果。Unknown=true 表示查询过程出错（winget 不可用/网络异常/源损坏），
/// 调用方应据此显示"状态未知"而非"未安装"。
/// </summary>
public sealed record WingetQueryResult(bool Installed, string? Version, string? AvailableVersion, bool Unknown = false);
