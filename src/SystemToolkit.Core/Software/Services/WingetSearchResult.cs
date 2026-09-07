namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// winget search 的单个候选包。
/// </summary>
/// <param name="Id">winget 包 ID。</param>
/// <param name="Name">软件显示名。</param>
/// <param name="Version">可用版本。</param>
public sealed record WingetSearchResult(string Id, string Name, string Version);
