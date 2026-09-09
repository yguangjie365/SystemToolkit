using System.Runtime.Versioning;
using System.Text.Json;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Network.Services;

/// <summary>快照内容体（对应 DB 设计 §4.4.1 的 ContentJson：Adapter / IPv4 / DNS / TCP / Proxy 全量）。</summary>
public sealed record NetworkSnapshotContent(
    string SchemaVersion,
    IReadOnlyList<NetAdapterInfo> Adapters,
    TcpGlobalSettings? Tcp,
    IReadOnlyList<InterfaceMetricInfo> InterfaceMetrics,
    ProxyInfo Proxy);

/// <summary>
/// 一份网络配置快照（字段与 DB 设计 §4.4.1 NetworkSnapshots 表对齐）。
/// ⚠️ 快照内容不含代理凭据（设计 §4.4.1 加密需求：Proxy 用户名/密码不进快照）。
/// </summary>
public sealed record NetworkSnapshotRecord(
    string Id,
    string Name,
    string Reason,
    string? RelatedAction,
    string? CorrelationId,
    DateTime CreatedAt,
    NetworkSnapshotContent Content);

/// <summary>网络配置快照服务契约（保存 / 列表 / 恢复）。</summary>
public interface INetworkSnapshotService
{
    /// <summary>采集当前全量网络配置并落盘（原子写）。</summary>
    Task<NetworkSnapshotRecord> CaptureAsync(
        string reason, string? relatedAction = null, string? correlationId = null,
        Action<string>? onLine = null, CancellationToken ct = default);

    /// <summary>列出全部快照（按创建时间倒序）。损坏文件跳过并在日志可观测处忽略。</summary>
    Task<IReadOnlyList<NetworkSnapshotRecord>> ListAsync(CancellationToken ct = default);

    /// <summary>按 Id 取单份快照；不存在返回 null。</summary>
    Task<NetworkSnapshotRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>删除快照文件。</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// 一键恢复：恢复前<b>自动采集一份 BeforeChange 快照</b>（设计 §5 修改类操作 100% 快照纪律），
    /// 然后按 适配器 IPv4/DNS → TCP 调优 → 接口跃点数 → 系统代理 顺序回放。
    /// 只回放快照里仍存在的适配器；绝不删除/修改快照之外的配置。
    /// </summary>
    Task RestoreAsync(string id, Action<string> onLine, CancellationToken ct = default);
}

/// <summary>
/// <see cref="INetworkSnapshotService"/> 实现。
/// <para>
/// 【存储】暂以 <c>%LOCALAPPDATA%\SystemToolkit\net\snapshots\</c> 下一文件一快照（原子写，
/// 复用 <see cref="AtomicFile"/>）；SQLite NetworkSnapshots 表（DB 设计 §4.4.1）随数据库基建
/// 落地后迁移，字段一一对应。仅保留最近 <see cref="MaxKeep"/> 份。
/// 【协议栈例外】ipreset / winsockreset 属修复类破坏性动作（需重启），不纳入快照回放。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkSnapshotService : INetworkSnapshotService
{
    /// <summary>Reason：手动创建。</summary>
    public const string ReasonManual = "Manual";
    /// <summary>Reason：修改前自动。</summary>
    public const string ReasonBeforeChange = "BeforeChange";

    /// <summary>快照 JSON 结构版本（DB 设计：SchemaVersion，跨版本恢复兼容判断）。</summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>最多保留的快照份数（超出按创建时间淘汰最旧）。</summary>
    public const int MaxKeep = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly INetworkInfoService _info;
    private readonly ITcpTuningService _tuning;
    private readonly INetConfigService _config;
    private readonly string _directory;

    /// <summary>构造；目录缺省 <c>%LOCALAPPDATA%\SystemToolkit\net\snapshots</c>（测试注入临时目录）。</summary>
    public NetworkSnapshotService(
        INetworkInfoService info,
        ITcpTuningService tuning,
        INetConfigService config,
        string? directory = null)
    {
        _info = info;
        _tuning = tuning;
        _config = config;
        _directory = directory ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "net", "snapshots");
    }

    /// <inheritdoc cref="INetworkSnapshotService.CaptureAsync"/>
    public async Task<NetworkSnapshotRecord> CaptureAsync(
        string reason, string? relatedAction = null, string? correlationId = null,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (reason is not (ReasonManual or ReasonBeforeChange))
        {
            throw new ArgumentException($"reason 必须为 {ReasonManual} 或 {ReasonBeforeChange}", nameof(reason));
        }

        IReadOnlyList<NetAdapterInfo> adapters = await _info.GetAdaptersAsync().ConfigureAwait(false);
        TcpGlobalSettings tcp = await _tuning.ReadAsync(ct).ConfigureAwait(false);
        IReadOnlyList<InterfaceMetricInfo> metrics = await _tuning.ListInterfaceMetricsAsync(ct).ConfigureAwait(false);
        ProxyInfo proxy = _info.GetSystemProxy();

        var record = new NetworkSnapshotRecord(
            Id: Guid.NewGuid().ToString("N"),
            Name: $"{relatedAction ?? reason} {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            Reason: reason,
            RelatedAction: relatedAction,
            CorrelationId: correlationId,
            CreatedAt: DateTime.Now,
            Content: new NetworkSnapshotContent(CurrentSchemaVersion, adapters, tcp, metrics, proxy));

        Directory.CreateDirectory(_directory);
        string path = PathFor(record);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
        onLine?.Invoke($"[快照] 已保存改前快照：{record.Name}（适配器 {adapters.Count} 个）");
        PruneOldSnapshots(onLine);
        return record;
    }

    /// <inheritdoc cref="INetworkSnapshotService.ListAsync"/>
    public async Task<IReadOnlyList<NetworkSnapshotRecord>> ListAsync(CancellationToken ct = default)
    {
        var records = new List<NetworkSnapshotRecord>();
        if (!Directory.Exists(_directory))
        {
            return records;
        }

        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                NetworkSnapshotRecord? r = JsonSerializer.Deserialize<NetworkSnapshotRecord>(
                    await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), JsonOptions);
                if (r is not null)
                {
                    records.Add(r);
                }
            }
            catch (Exception)
            {
                // 损坏文件不阻断列表（半截文件被原子写机制挡住，理论到不了这里）
            }
        }

        return records.OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <inheritdoc cref="INetworkSnapshotService.GetAsync"/>
    public async Task<NetworkSnapshotRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        string? path = FindFileById(id);
        if (path is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NetworkSnapshotRecord>(
                await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc cref="INetworkSnapshotService.DeleteAsync"/>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        // 审查 O16（2026-09-10）：快照目录扫描 + 文件删除移出调用线程（原非 async 方法被 await 不切线程）
        await Task.Run(() =>
        {
            string? path = FindFileById(id);
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc cref="INetworkSnapshotService.RestoreAsync"/>
    public async Task RestoreAsync(string id, Action<string> onLine, CancellationToken ct = default)
    {
        NetworkSnapshotRecord? record = await GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("快照不存在或已损坏，拒绝还原");

        onLine($"[快照] 开始还原 {record.CreatedAt:yyyy-MM-dd HH:mm:ss} 的快照「{record.Name}」");

        // 修改类操作 100% 快照纪律：还原本身也是修改，先留一份当前状态的后悔药
        await CaptureAsync(ReasonBeforeChange, "RestoreSnapshot", onLine: onLine, ct: ct).ConfigureAwait(false);

        // ① 适配器 IPv4 / DNS（只回放快照里当前仍存在的适配器）
        IReadOnlyList<NetAdapterInfo> current = await _info.GetAdaptersAsync().ConfigureAwait(false);
        var failedExits = new List<int>(); // 审查 O4（2026-09-10）：聚合各步非零退出码，补 Verify 红线
        int tcpRestoreFailed = 0; // 审查 O4/O10：TCP 还原 ApplyAsync 失败项计数（跨 if 块累加）
        foreach (NetAdapterInfo adapter in record.Content.Adapters)
        {
            if (current.FirstOrDefault(a => a.Name == adapter.Name) is null)
            {
                onLine($"[快照] 适配器「{adapter.Name}」已不存在，跳过其配置还原");
                continue;
            }

            int addressExit = await RestoreAdapterAddressAsync(adapter, onLine, ct).ConfigureAwait(false);
            if (addressExit != 0)
            {
                failedExits.Add(addressExit);
            }

            int dnsExit = await RestoreAdapterDnsAsync(adapter, onLine, ct).ConfigureAwait(false);
            if (dnsExit != 0)
            {
                failedExits.Add(dnsExit);
            }
        }

        // ② TCP 调优（ApplyAsync 只对变化项发命令；可空字段跳过——与「降级不猜值」纪律一致）
        if (record.Content.Tcp is TcpGlobalSettings tcp)
        {
            onLine("[快照] 还原 TCP 调优参数…");
            var target = new TcpGlobalSettings(
                tcp.AutoTuningLevel,
                tcp.RssEnabled,
                tcp.EcnEnabled,
                tcp.NetworkThrottlingIndex,
                InitialRto: null,
                CongestionProvider: null,
                RscState: null,
                Rfc1323Timestamps: null);
            TcpApplyResult result = await _tuning.ApplyAsync(target, onLine, ct).ConfigureAwait(false);
            tcpRestoreFailed += result.Failed.Count; // 审查 O4/O10：TCP 写失败并入还原结局判定
            onLine($"[快照] TCP 还原完成：应用 {result.Applied.Count} 项、跳过 {result.Skipped.Count} 项"
                + (result.Failed.Count > 0 ? $"、失败 {result.Failed.Count} 项（{string.Join("、", result.Failed)}）" : ""));
        }

        // ③ 接口跃点数（ApplyInterfaceMetricAsync 自带「接口不存在跳过 / 同值不写」）
        foreach (InterfaceMetricInfo metric in record.Content.InterfaceMetrics)
        {
            await _tuning.ApplyInterfaceMetricAsync(metric.Name, metric.Metric, onLine, ct).ConfigureAwait(false);
        }

        // ④ 系统代理
        _info.SetSystemProxy(record.Content.Proxy.Enabled, record.Content.Proxy.Server, onLine);

        // 审查 O4/O10：按各步退出码 + TCP 失败项给出真实结局，不再无条件 ✅
        int failedTotal = failedExits.Count + tcpRestoreFailed;
        if (failedTotal == 0)
        {
            onLine("[快照] ✅ 还原完成。若结果不符合预期，可再还原本次还原前自动保存的快照（RestoreSnapshot）");
        }
        else
        {
            onLine($"[快照] ⚠️ 还原完成，但有 {failedTotal} 步失败" +
                (failedExits.Count > 0 ? $"（退出码 {string.Join("、", failedExits)}）" : "") +
                "——可用 RestoreSnapshot 回滚本次还原前自动保存的快照");
        }
    }

    // 审查 O4（2026-09-10）：还原子方法改为回传退出码（-1=前置校验未执行）——
    // 主流程聚合失败步，不再无条件报"✅ 还原完成"（Snapshot→Modify→Verify→Rollback 红线补 Verify）
    private async Task<int> RestoreAdapterAddressAsync(NetAdapterInfo adapter, Action<string> onLine, CancellationToken ct)
    {
        if (adapter.IsDhcp)
        {
            onLine($"[快照] 「{adapter.Name}」恢复 DHCP 自动获取");
            return await _config.SetDhcpAsync(adapter.Name, onLine).ConfigureAwait(false);
        }

        if (adapter.IPv4WithMask.Count == 0)
        {
            return 0; // 静态模式但无 IPv4（罕见）：无从还原，静默跳过
        }

        string primary = adapter.IPv4WithMask[0];
        int slash = primary.IndexOf('/');
        if (slash <= 0 || !IpValidation.IsIPv4(primary[..slash])
            || !int.TryParse(primary[(slash + 1)..], out int prefix) || prefix is < 0 or > 32)
        {
            onLine($"[快照] ⚠️ 「{adapter.Name}」的 IP 记录「{primary}」无法解析，跳过其 IPv4 还原");
            return 0;
        }

        string ip = primary[..slash];
        string mask = PrefixToMask(prefix);
        string? gateway = adapter.Gateways.Count > 0 ? adapter.Gateways[0] : null;
        if (adapter.Gateways.Count > 1)
        {
            onLine($"[快照] ⚠️ 「{adapter.Name}」有 {adapter.Gateways.Count} 个网关，快照还原只回放主网关 {gateway}");
        }

        onLine($"[快照] 「{adapter.Name}」恢复静态 IP {ip}/{mask}（网关 {gateway ?? "无"}）");
        return await _config.SetStaticIpAsync(adapter.Name, ip, mask, gateway, onLine).ConfigureAwait(false);
    }

    private async Task<int> RestoreAdapterDnsAsync(NetAdapterInfo adapter, Action<string> onLine, CancellationToken ct)
    {
        string? primary = adapter.DnsServers.Count > 0 ? adapter.DnsServers[0] : null;
        string? secondary = adapter.DnsServers.Count > 1 ? adapter.DnsServers[1] : null;
        if (primary is null && adapter.DnsServers.Count > 0)
        {
            return 0; // 首条为空但列表非空：记录异常，不动作
        }

        onLine($"[快照] 「{adapter.Name}」恢复 DNS：{primary ?? "自动获取"}{(secondary is null ? "" : $" / {secondary}")}");
        return await _config.SetDnsAsync(adapter.Name, primary, secondary, onLine).ConfigureAwait(false);
    }

    /// <summary>前缀长度 → 子网掩码（/24 → 255.255.255.0）。internal 供单测钉死位运算。</summary>
    internal static string PrefixToMask(int prefix)
    {
        if (prefix is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefix), prefix, "前缀长度必须为 0..32");
        }

        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return $"{mask >> 24 & 0xFF}.{mask >> 16 & 0xFF}.{mask >> 8 & 0xFF}.{mask & 0xFF}";
    }

    /// <summary>快照文件名：时间前缀（淘汰排序依据）+ Reason + Id。</summary>
    private string PathFor(NetworkSnapshotRecord record)
        => Path.Combine(_directory, $"{record.CreatedAt:yyyyMMdd-HHmmss}_{record.Reason}_{record.Id}.json");

    /// <summary>按 Id 定位快照文件（数量 ≤ MaxKeep，扫描可接受）。不存在返回 null。</summary>
    private string? FindFileById(string id)
    {
        if (!Directory.Exists(_directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(_directory, $"*_{id}.json").FirstOrDefault();
    }

    /// <summary>保留最近 <see cref="MaxKeep"/> 份（文件名带时间前缀，按名排序即按时间排序）。</summary>
    private void PruneOldSnapshots(Action<string>? onLine)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        List<string> files = [.. Directory.EnumerateFiles(_directory, "*.json").OrderByDescending(f => f)];
        foreach (string stale in files.Skip(MaxKeep))
        {
            try
            {
                File.Delete(stale);
                onLine?.Invoke($"[快照] 淘汰最旧快照（超出保留 {MaxKeep} 份）：{Path.GetFileName(stale)}");
            }
            catch (Exception)
            {
                // 淘汰失败不阻断主流程
            }
        }
    }
}
