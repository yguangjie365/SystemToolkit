using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 网络模块测试共用的 fake 替身（自旧工程 NetManagerTestFakes 移植）：不触碰真实系统
/// ——进程 / 注册表 / 提权检测 / 真实 ICMP 全部隔离。
/// </summary>
internal static class NetworkTestFakes
{
    public sealed class FakeInfoService : INetworkInfoService
    {
        public List<NetAdapterInfo> Adapters { get; } = new();

        public ProxyInfo Proxy { get; set; } = new(false, null);

        public List<(bool Enabled, string? Server)> SetProxyCalls { get; } = new();

        public Task<IReadOnlyList<NetAdapterInfo>> GetAdaptersAsync()
            => Task.FromResult<IReadOnlyList<NetAdapterInfo>>(Adapters.ToArray());

        public ProxyInfo GetSystemProxy() => Proxy;

        public void SetSystemProxy(bool enabled, string? server, Action<string>? log = null)
        {
            SetProxyCalls.Add((enabled, server));
            // 行为对齐真实实现：写入后读回能看到（读回校验测试依赖这一点）
            Proxy = new ProxyInfo(enabled, server);
        }
    }

    public sealed class FakeConfigService : INetConfigService
    {
        public List<string> Calls { get; } = new();

        public int ExitCode { get; set; }

        public Task<int> SetDhcpAsync(string adapter, Action<string> onLine)
        {
            Calls.Add($"dhcp:{adapter}");
            return Task.FromResult(ExitCode);
        }

        /// <summary>测试用的执行门闩：置位后 SetStaticIpAsync 挂起，用于验证写命令防重入门闩。</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<int> SetStaticIpAsync(string adapter, string ip, string mask, string? gateway, Action<string> onLine)
        {
            Calls.Add($"static:{adapter}:{ip}/{mask}/{gateway ?? "-"}");
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return ExitCode;
        }

        public Task<int> SetDnsAsync(string adapter, string? primary, string? secondary, Action<string> onLine)
        {
            Calls.Add($"dns:{adapter}:{primary ?? "dhcp"}/{secondary ?? "-"}");
            return Task.FromResult(ExitCode);
        }

        public Task<int> SetAdapterEnabledAsync(string adapter, bool enabled, Action<string> onLine)
        {
            Calls.Add($"adapter:{adapter}:{(enabled ? "enable" : "disable")}");
            return Task.FromResult(ExitCode);
        }
    }

    public sealed class FakeElevation : IElevationProvider
    {
        public bool IsElevated { get; set; }
    }

    public sealed class FakeTuningService : ITcpTuningService
    {
        public TcpGlobalSettings Current { get; set; } =
            new("normal", true, false, TcpGlobalSettings.ThrottlingDefault);

        public bool HasSnapshotValue { get; set; }

        public List<TcpGlobalSettings> Applied { get; } = new();

        public bool ThrowOnRestore { get; set; }

        public Task<TcpGlobalSettings> ReadAsync(CancellationToken ct = default) => Task.FromResult(Current);

        bool ITcpTuningService.HasSnapshot => HasSnapshotValue;

        public Task<TcpApplyResult> ApplyAsync(TcpGlobalSettings target, Action<string> onLine, CancellationToken ct = default)
        {
            Applied.Add(target);
            Current = target;
            HasSnapshotValue = true;
            return Task.FromResult(new TcpApplyResult(
                Array.Empty<string>(), Array.Empty<string>()));
        }

        public Task RestoreAsync(Action<string> onLine, CancellationToken ct = default)
        {
            if (ThrowOnRestore)
            {
                throw new InvalidOperationException("没有可还原的优化快照——从未应用过更改，或快照文件已被清理");
            }

            Current = new TcpGlobalSettings("normal", true, false, TcpGlobalSettings.ThrottlingDefault);
            return Task.CompletedTask;
        }

        public List<InterfaceMetricInfo> Metrics { get; } =
            new() { new("LAN", 25), new("WLAN", 40) };

        public bool MetricReadThrows { get; set; }

        public List<(string Adapter, int Metric)> AppliedMetrics { get; } = new();

        public Task<IReadOnlyList<InterfaceMetricInfo>> ListInterfaceMetricsAsync(CancellationToken ct = default)
        {
            if (MetricReadThrows)
            {
                throw new InvalidOperationException("show interfaces 解析失败");
            }

            return Task.FromResult<IReadOnlyList<InterfaceMetricInfo>>(Metrics);
        }

        public Task ApplyInterfaceMetricAsync(string adapter, int metric, Action<string> onLine, CancellationToken ct = default)
        {
            AppliedMetrics.Add((adapter, metric));
            onLine($"[调优] ✅ 「{adapter}」跃点数已设为 {metric}");
            return Task.CompletedTask;
        }
    }

    public sealed class FakeHostsService : IHostsCheckService
    {
        public HostsCheckResult Result { get; set; } =
            new("hosts", Array.Empty<HostsEntry>());

        public Task<HostsCheckResult> CheckAsync(CancellationToken ct = default)
            => Task.FromResult(Result);
    }

    /// <summary>网络探测 fake：可编程「网关 / 公网 / DNS」三处连通性，覆盖诊断结论规则的全部分支。</summary>
    public sealed class FakeProbe : INetProbe
    {
        public bool GatewayReachable { get; set; } = true;

        public bool PublicReachable { get; set; } = true;

        public bool DnsResolvable { get; set; } = true;

        public List<string> PingTargets { get; } = new();

        public Task<bool> PingAsync(string address, int timeoutMs, CancellationToken ct = default)
        {
            PingTargets.Add(address);
            // 公网探测地址是固定常量，其余一律视作网关
            return Task.FromResult(address == NetDiagnosticService.PublicProbeAddress
                ? PublicReachable
                : GatewayReachable);
        }

        public Task<bool> ResolveAsync(string host, CancellationToken ct = default) => Task.FromResult(DnsResolvable);

        // ── 量化 / DF 探测 ──
        public int QuantifyLost { get; set; }

        public int QuantifyLatencyMs { get; set; } = 5;

        public List<int> DfProbeSizes { get; } = new();

        /// <summary>DF 探测：载荷 ≤ 此值通过（模拟路径 MTU 上限）；null = 全部拒绝。</summary>
        public int? MaxDfPayloadPass { get; set; } = 1472;

        public Task<PingQuantifyResult> PingQuantifyAsync(string host, int count, int timeoutMs, int intervalMs, CancellationToken ct = default)
        {
            int received = count - QuantifyLost;
            return Task.FromResult(new PingQuantifyResult(
                count,
                received,
                received > 0 ? QuantifyLatencyMs : null,
                received > 0 ? QuantifyLatencyMs : null,
                received > 0 ? QuantifyLatencyMs : null));
        }

        public Task<bool> PingDontFragmentAsync(string host, int payloadSize, int timeoutMs, CancellationToken ct = default)
        {
            DfProbeSizes.Add(payloadSize);
            return Task.FromResult(MaxDfPayloadPass is int max && payloadSize <= max);
        }

        // ── 端口测试 ──
        public TcpProbeResult TcpResult { get; set; } =
            new(true, 12, null);

        public Task<TcpProbeResult> ConnectTcpAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
            => Task.FromResult(TcpResult);
    }

    public sealed class FakeDiagnosticService : INetDiagnosticService
    {
        public IReadOnlyList<DiagStepResult> Steps { get; set; } = Array.Empty<DiagStepResult>();

        public string Conclusion { get; set; } = "（fake 结论）";

        public int RunCount { get; private set; }

        public bool WithProgressCalled { get; private set; }

        /// <summary>测试用的执行门闩：置位后 RunAsync 会挂起，用于验证「操作期间防重入」。</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<IReadOnlyList<DiagStepResult>> RunAsync(CancellationToken ct = default)
        {
            RunCount++;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return Steps;
        }

        public async Task<IReadOnlyList<DiagStepResult>> RunWithProgressAsync(IProgress<DiagStepResult>? progress, CancellationToken ct = default)
        {
            WithProgressCalled = true;
            IReadOnlyList<DiagStepResult> steps = await RunAsync(ct);
            foreach (DiagStepResult step in steps)
            {
                progress?.Report(step);
            }

            return steps;
        }

        public string BuildConclusion(IReadOnlyList<DiagStepResult> steps) => Conclusion;

        public MtuProbeResult? MtuResult { get; set; }

        public Task<MtuProbeResult?> ProbePathMtuAsync(CancellationToken ct = default)
            => Task.FromResult(MtuResult);

        public HostsCheckResult HostsResult { get; set; } =
            new("hosts", Array.Empty<HostsEntry>());

        public Task<HostsCheckResult> CheckHostsAsync(CancellationToken ct = default)
            => Task.FromResult(HostsResult);

        public TcpProbeResult PortResult { get; set; } =
            new(true, 15, null);

        public Task<TcpProbeResult> TestPortAsync(string host, int port, CancellationToken ct = default)
            => Task.FromResult(PortResult);
    }

    public sealed class FakeRepairService : INetRepairService
    {
        public List<string> Executed { get; } = new();

        public int ExitCode { get; set; }

        /// <summary>与真实服务同形的目录（管理员 / 重启标记一致），便于测置灰矩阵。</summary>
        public IReadOnlyList<RepairStepDescriptor> Steps { get; } = new[]
        {
            new RepairStepDescriptor("flushdns", "刷新 DNS 缓存", "清除缓存", false, false, "ipconfig /flushdns"),
            new RepairStepDescriptor("renew", "重新获取 IP", "续订租约", false, false, "ipconfig /renew \"<n>\""),
            new RepairStepDescriptor("bounce", "重启网卡", "软重连", true, false, "netsh … disable → enable"),
            new RepairStepDescriptor("arpclear", "清除 ARP 缓存", "删除 ARP 表", true, false, "arp -d *"),
            new RepairStepDescriptor("winsockreset", "重置 Winsock", "重置套接字", true, true, "netsh winsock reset"),
            new RepairStepDescriptor("ipreset", "重置 TCP/IP", "重置协议栈", true, true, "netsh int ip reset"),
        };

        public Task<int> ExecuteAsync(string stepId, Action<string> onLine, string? adapter = null, CancellationToken ct = default)
        {
            Executed.Add(stepId);
            return Task.FromResult(ExitCode);
        }

        public Task<IReadOnlyList<string>> RunSafeSequenceAsync(Action<string> onLine, CancellationToken ct = default)
        {
            Executed.Add("__safe__");
            return Task.FromResult<IReadOnlyList<string>>(new[] { "flushdns", "renew" });
        }
    }

    /// <summary>全量快照 fake：内存列表 + 计数，供 VM 与恢复流程测试。</summary>
    public sealed class FakeSnapshotService : INetworkSnapshotService
    {
        public List<NetworkSnapshotRecord> Snapshots { get; } = new();

        public int CaptureCount { get; private set; }

        public string? LastCaptureReason { get; private set; }

        public List<string> RestoredIds { get; } = new();

        public string? ThrowOnRestoreId { get; set; }

        public Task<NetworkSnapshotRecord> CaptureAsync(
            string reason, string? relatedAction = null, string? correlationId = null,
            Action<string>? onLine = null, CancellationToken ct = default)
        {
            CaptureCount++;
            LastCaptureReason = reason;
            var record = new NetworkSnapshotRecord(
                Guid.NewGuid().ToString("N"), $"{relatedAction ?? reason}", reason, relatedAction,
                correlationId, DateTime.Now,
                new NetworkSnapshotContent("1.0", Array.Empty<NetAdapterInfo>(), null,
                    Array.Empty<InterfaceMetricInfo>(), new ProxyInfo(false, null)));
            Snapshots.Insert(0, record);
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<NetworkSnapshotRecord>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NetworkSnapshotRecord>>(Snapshots.ToArray());

        public Task<NetworkSnapshotRecord?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult<NetworkSnapshotRecord?>(Snapshots.FirstOrDefault(s => s.Id == id));

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            Snapshots.RemoveAll(s => s.Id == id);
            return Task.CompletedTask;
        }

        public Task RestoreAsync(string id, Action<string> onLine, CancellationToken ct = default)
        {
            if (ThrowOnRestoreId == id || Snapshots.All(s => s.Id != id))
            {
                throw new InvalidOperationException("快照不存在或已损坏，拒绝还原");
            }

            RestoredIds.Add(id);
            return Task.CompletedTask;
        }
    }
}
