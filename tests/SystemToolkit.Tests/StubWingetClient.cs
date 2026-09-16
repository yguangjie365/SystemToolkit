using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 全测试工程共享的 winget 假件（零进程、零网络）。
/// <para>
/// 2026-09-15 从两处私有副本（<c>AppManagerIgnoreRowTests</c> / <c>ThemeSwitchViewRebuildGuardTests</c>）
/// 合并而来——同一结构出现两次即抽取（01 分册 §三「一文件一主类型」的相邻纪律）；
/// 行为口径沿用原实现：不可用（<c>IsAvailableAsync=false</c>，状态检测走"未知"分支）、
/// 查询未知、写操作失败（exit 1），调用方需要别的取值时**在自己的用例里**包装或另建假件。
/// </para>
/// </summary>
public sealed class StubWingetClient : IWingetClient
{
    public Action<string> OutputSink { get; set; } = _ => { };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<string> ListInstalledAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);

    public Task<string> ListUpgradesAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);

    public Task<WingetQueryResult> QueryAsync(string id, string? source = null, CancellationToken ct = default)
        => Task.FromResult(new WingetQueryResult(Installed: false, Version: null, AvailableVersion: null, Unknown: true));

    public Task<WingetRunResult> InstallAsync(string id, string? source = null, CancellationToken ct = default)
        => Task.FromResult(new WingetRunResult(1, false));

    public Task<WingetRunResult> UpgradeAsync(string id, string? source = null, CancellationToken ct = default)
        => Task.FromResult(new WingetRunResult(1, false));

    public Task<WingetRunResult> UninstallAsync(string id, string? source = null, CancellationToken ct = default)
        => Task.FromResult(new WingetRunResult(1, false));

    public Task<WingetRunResult> UpdateSourceAsync(CancellationToken ct = default)
        => Task.FromResult(new WingetRunResult(1, false));

    public Task<WingetRunResult> SetSourceAsync(string name, string url, CancellationToken ct = default)
        => Task.FromResult(new WingetRunResult(1, false));

    public Task<WingetRunResult> ResetSourceAsync(string name, CancellationToken ct = default)
        => Task.FromResult(new WingetRunResult(1, false));

    public Task<string> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult(string.Empty);

    public Task<WingetRunResult> ExportAsync(string path, CancellationToken ct = default)
        => Task.FromResult(new WingetRunResult(1, false));
}
