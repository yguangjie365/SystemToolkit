namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// winget 命令执行抽象：按 <see cref="WingetService"/> 的公开实例成员提取。
/// 目的：让消费方（如 EnvManagerViewModel）依赖接口而非具体实现，
/// 单元测试可用 fake 替身（记录调用次数 / 可编程返回值），彻底摆脱对真实
/// winget 进程、网络与注册表的依赖。
/// 静态成员（ParseSearchResults / FindPackageRow 等纯解析函数）不进接口——
/// 它们无副作用，测试可直接调用真实实现。
/// </summary>
public interface IWingetClient
{
    /// <summary>winget 输出回调（构造后可替换，供 VM 挂接日志面板）。</summary>
    Action<string> OutputSink { get; set; }

    /// <summary>探测 winget 是否可用（winget --version）。</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>执行全量 winget list，返回原始输出。</summary>
    Task<string> ListInstalledAsync(CancellationToken ct = default);

    /// <summary>执行 winget upgrade（无参），返回可升级包列表原始输出。</summary>
    Task<string> ListUpgradesAsync(CancellationToken ct = default);

    /// <summary>按 Id 精确查询单包安装状态。</summary>
    Task<WingetQueryResult> QueryAsync(string id, string? source = null, CancellationToken ct = default);

    /// <summary>安装指定包。</summary>
    Task<WingetRunResult> InstallAsync(string id, string? source = null, CancellationToken ct = default);

    /// <summary>升级指定包。</summary>
    Task<WingetRunResult> UpgradeAsync(string id, string? source = null, CancellationToken ct = default);

    /// <summary>卸载指定包。</summary>
    Task<WingetRunResult> UninstallAsync(string id, string? source = null, CancellationToken ct = default);

    /// <summary>更新 winget 软件源。</summary>
    Task<WingetRunResult> UpdateSourceAsync(CancellationToken ct = default);

    /// <summary>
    /// 把指定源切换为镜像地址：先尝试移除同名源，再以 <c>trusted</c> 信任级别添加。
    /// 源原本不存在时移除会返回非零码，属预期（首次换源），不中断流程。
    /// </summary>
    Task<WingetRunResult> SetSourceAsync(string name, string url, CancellationToken ct = default);

    /// <summary>把指定源恢复为官方默认（<c>winget source reset --force</c>）。</summary>
    Task<WingetRunResult> ResetSourceAsync(string name, CancellationToken ct = default);

    /// <summary>搜索 winget 源，返回原始输出（JSON 或表格文本）。</summary>
    Task<string> SearchAsync(string query, CancellationToken ct = default);
}
