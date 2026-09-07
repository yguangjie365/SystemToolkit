namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// hosts 异常检查（M6b P1-4，<b>纯只读</b>——只呈现绝不修改）。
/// </summary>
public interface IHostsCheckService
{
    /// <summary>
    /// 读取并解析 hosts 文件，返回全部非注释条目（含「需留意」标记）。
    /// 文件不可读时抛 <see cref="IOException"/>，由调用方降级为日志提示。
    /// </summary>
    Task<HostsCheckResult> CheckAsync(CancellationToken ct = default);
}

/// <summary>hosts 检查结果：全部非注释条目 + 文件路径（供日志展示来源）。</summary>
public sealed record HostsCheckResult(string FilePath, IReadOnlyList<HostsEntry> Entries);

/// <summary>真实实现：读 <c>%WINDIR%\System32\drivers\etc\hosts</c>。</summary>
public sealed class HostsCheckService : IHostsCheckService
{
    private readonly string _hostsPath;

    /// <summary>构造；hosts 路径缺省为 <c>%WINDIR%\System32\drivers\etc\hosts</c>（测试可注入临时文件）。</summary>
    public HostsCheckService(string? hostsPath = null)
    {
        _hostsPath = hostsPath
            ?? System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
                "drivers", "etc", "hosts");
    }

    /// <inheritdoc cref="IHostsCheckService.CheckAsync"/>
    public async Task<HostsCheckResult> CheckAsync(CancellationToken ct = default)
    {
        string text = await File.ReadAllTextAsync(_hostsPath, ct).ConfigureAwait(false);
        return new HostsCheckResult(_hostsPath, HostsParser.Parse(text));
    }
}
