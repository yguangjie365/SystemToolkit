using System.Net;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>扫描进度：阶段（中文，UI 直显）+ 已完成/总数。</summary>
/// <param name="Phase">当前阶段。</param>
/// <param name="Done">已完成探测数。</param>
/// <param name="Total">本轮计划探测数。</param>
public sealed record LanScanProgress(string Phase, int Done, int Total);

/// <summary>
/// 局域网扫描编排（NET-6 的心脏，UI 只见这一个入口）：
/// 主动 ARP 扫段 → 邻居表收口 MAC → 主机名反查/OUI 富化 → 基线比对与冲突判定 → 基线落盘。
/// <para>
/// 免提权路线（调研定稿）：SendARP/GetIpNetTable2 普通权限可用；探针经
/// <see cref="ILanNeighborProbe"/> 注入，全链路可用假件脱网测试。
/// 「连续两轮绑定不一致 = IP 冲突」：监控轮间天然两轮；重启后首轮用即时双探复核替代。
/// </para>
/// </summary>
public sealed class LanScanService
{
    /// <summary>ARP 并发探测上限（家用/办公网 1024 地址 2 秒内扫完；再大易触发防火墙限速）。</summary>
    public const int MaxConcurrency = 32;

    /// <summary>主机名反查的规模闸门：超过则跳过反查（监控轮次里 MAC 比对不依赖主机名）。</summary>
    public const int HostnameResolveLimit = 256;

    /// <summary>操作边界日志动作名（06 册 §4 清单登记）。</summary>
    public const string ScanAction = "LanScan";

    private readonly ILanNeighborProbe _probe;
    private readonly LanBaselineStore _store;
    private readonly ILogger _log;
    private readonly Func<string, Task<string?>> _resolveHost;
    private readonly ILanPeerProbe? _peer;

    /// <summary>上一轮观测（网段标签 → IP↔MAC）：跨轮冲突判定的内存态，不落盘。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _previousRounds = new(StringComparer.Ordinal);

    /// <summary>探针/存储/日志均可注入；<paramref name="hostnameResolver"/> 缺省走真 DNS（400ms 超时降级）。</summary>
    public LanScanService(
        ILanNeighborProbe probe,
        LanBaselineStore store,
        ILogger? logger = null,
        Func<string, Task<string?>>? hostnameResolver = null,
        ILanPeerProbe? peerProbe = null)
    {
        _probe = probe;
        _store = store;
        _log = logger ?? NullLogger.Instance;
        _resolveHost = hostnameResolver ?? ResolveHostnameAsync;
        _peer = peerProbe;
    }

    /// <summary>
    /// 单次 ping 判定文本（局域网 Tab 行内「Ping」用——真实 ICMP，无状态自建探针；永不抛）。
    /// 刻意不走 <see cref="_peer"/>：扫描富化可被假件静音，但用户手点 Ping 必须真打网络。
    /// </summary>
    public Task<string> PingOnceAsync(string ipv4, CancellationToken ct = default) =>
        new LanPeerProbe().PingVerdictAsync(ipv4, ct);

    /// <summary>当前基线（UI 首屏「已知设备」计数用；不触发扫描）。</summary>
    public LanBaseline? PeekBaseline() => _store.Load().Data;

    /// <summary>
    /// 执行一轮扫描。取消时返回部分结果（<c>WasCancelled=true</c>，基线与跨轮态均不更新，
    /// 部分结果语义对齐曲库扫描）。进度回调可能来自线程池线程，UI 侧自行调度。
    /// </summary>
    public async Task<LanScanResult> ScanAsync(
        LanSubnetPlan plan,
        IProgress<LanScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        using LogTiming timing = _log.Time(ScanAction);
        DateTimeOffset now = DateTimeOffset.Now;
        List<string> alive = [];
        try
        {
            // ① 主动 ARP 扫段：SendARP 命中即在线，同时把条目打进邻居缓存
            int done = 0;
            using SemaphoreSlim gate = new(MaxConcurrency);
            // 子任务内部吞取消返回 null（永不 fault）：避免批量 UnobservedTaskException，
            // 取消语义统一由循环后的 ThrowIfCancellationRequested 收敛
            var sweep = plan.Hosts.Select(async ip =>
            {
                try
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                try
                {
                    bool hit = await Task.Run(() => _probe.TryPoke(ip), ct).ConfigureAwait(false);
                    int n = Interlocked.Increment(ref done);
                    progress?.Report(new LanScanProgress("ARP 扫段", n, plan.Hosts.Count));
                    return hit ? ip : null;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            foreach (Task<string?> task in sweep)
            {
                string? hit = await task.ConfigureAwait(false);
                if (hit is not null)
                {
                    alive.Add(hit);
                }
            }

            ct.ThrowIfCancellationRequested();

            // ② 邻居表收口 MAC；漏网者（表刷新窗口）补一轮「再探 + 重读表」
            Dictionary<string, string> macs = ReadMacMap();
            var blind = alive.Where(ip => !macs.ContainsKey(ip)).ToList();
            if (blind.Count > 0)
            {
                progress?.Report(new LanScanProgress("补齐 MAC", 0, blind.Count));
                foreach (string ip in blind)
                {
                    _probe.TryPoke(ip);
                }

                await Task.Delay(150, ct).ConfigureAwait(false);
                foreach (KeyValuePair<string, string> pair in ReadMacMap())
                {
                    macs.TryAdd(pair.Key, pair.Value);
                }
            }

            // ③ 主机名并发反查（限流 + 单次 400ms 超时降级 null）+ OUI 富化 → 设备清单
            var ordered = alive.Order(StringComparer.Ordinal).ToList();
            string?[] names = ordered.Count <= HostnameResolveLimit
                ? await ResolveNamesAsync(ordered, ct).ConfigureAwait(false)
                : new string?[ordered.Count];
            List<LanDevice> devices = [];
            for (int i = 0; i < ordered.Count; i++)
            {
                string mac = macs.TryGetValue(ordered[i], out string? m) ? m : "";
                devices.Add(new LanDevice(ordered[i], mac, names[i], OuiTable.Lookup(mac), now, now));
            }

            // ③a TTL/NetBIOS 富化（可选探针）：主机名补 nbtstat、OS 走 TTL 推断；规模闸门防 ICMP 风暴
            if (_peer is not null && devices.Count <= PeerEnrichLimit)
            {
                devices = await EnrichAsync(devices, ct).ConfigureAwait(false);
            }

            // ④ 基线比对 + 首轮候选即时双探复核 → 事件/冲突/新基线（空 MAC 行不参与绑定比对）
            LanBaselineLoad load = _store.Load();
            _previousRounds.TryGetValue(plan.NetworkLabel, out Dictionary<string, string>? previous);
            LanDiffOutcome diff = LanDiffEngine.Diff(
                load.State == LanBaselineLoadStatus.Ok ? load.Data : null,
                devices.Where(static d => d.Mac.Length > 0).ToList(),
                previous,
                now);
            List<LanEvent> events = [.. diff.Events];
            List<string> conflicted = [.. diff.ConflictedIps];
            List<LanBaselineEntry> entries = [.. diff.Entries];
            await RecheckCandidatesAsync(diff.NeedsRecheck, events, conflicted, entries, ct)
                .ConfigureAwait(false);

            // ⑤ 落盘（事件环 = 本轮新事件在前，历史事件在后）与跨轮态更新：仅完整轮次执行
            _store.Save(entries, [.. events, .. load.Data?.Events ?? []], now);
            _previousRounds[plan.NetworkLabel] = devices
                .Where(static d => d.Mac.Length > 0)
                .ToDictionary(static d => d.Ip, static d => d.Mac, StringComparer.Ordinal);

            timing.Complete(message:
                $"{ScanAction} 完成：{devices.Count} 台在线，事件 {events.Count} 条，冲突 {conflicted.Count} 处");
            return new LanScanResult(devices, events, conflicted, now, plan.Truncated, WasCancelled: false);
        }
        catch (OperationCanceledException)
        {
            timing.Complete(LogResult.Cancelled, LogLevel.Warn, $"{ScanAction} 取消：已发现 {alive.Count} 台（基线未更新）");
            return new LanScanResult(
                [], [], [], now, plan.Truncated, WasCancelled: true);
        }
        catch (Exception ex)
        {
            timing.Complete(LogResult.Failed, LogLevel.Error, $"{ScanAction} 异常", ex);
            throw;
        }
    }

    /// <summary>主机名反查并发闸（DNS 压力上限，单条超时 400ms 见 <see cref="ResolveHostnameAsync"/>）。</summary>
    private const int NameResolveConcurrency = 16;

    /// <summary>补充探测（TTL+nbtstat）规模闸门：超过则跳过（超大网段下 8 并发 × 1.5s 会拖垮单轮）。</summary>
    public const int PeerEnrichLimit = 128;

    private async Task<List<LanDevice>> EnrichAsync(List<LanDevice> devices, CancellationToken ct)
    {
        using SemaphoreSlim gate = new(8);
        var probes = devices.Select(async d =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return d;
            }

            try
            {
                LanPeerInfo info = await _peer!.QueryAsync(d.Ip, ct).ConfigureAwait(false);
                return d with { Hostname = d.Hostname ?? info.NetBiosName, Os = LanOs.Classify(info.Ttl) };
            }
            catch (OperationCanceledException)
            {
                return d;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        ct.ThrowIfCancellationRequested();
        return [.. await Task.WhenAll(probes).ConfigureAwait(false)];
    }

    private async Task<string?[]> ResolveNamesAsync(List<string> ips, CancellationToken ct)
    {
        using SemaphoreSlim gate = new(NameResolveConcurrency);
        // 与 ARP 扫描同法：取消由外层 ThrowIf 收敛，子任务吞取消返回 null、永不 fault
        var tasks = ips.Select(async ip =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            try
            {
                return await _resolveHost(ip).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        ct.ThrowIfCancellationRequested();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>邻居表 → IP↔MAC（仅保留完整以太网绑定；重复键先到先得）。</summary>
    private Dictionary<string, string> ReadMacMap() =>
        _probe.ReadNeighbors()
            .Where(static n => n.Mac.Length > 0)
            .GroupBy(static n => n.Ip, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First().Mac, StringComparer.Ordinal);

    /// <summary>
    /// 重启后首轮的绑定变化候选：即时双探（探→读→短等待→再探→再读）。
    /// 两次 MAC 不一致 → 升级 Conflict；一致 → 定论 BindingChanged 并把基线条目切到新绑定代际。
    /// </summary>
    private async Task RecheckCandidatesAsync(
        IReadOnlyList<LanDevice> candidates,
        List<LanEvent> events,
        List<string> conflicted,
        List<LanBaselineEntry> entries,
        CancellationToken ct)
    {
        foreach (LanDevice device in candidates)
        {
            ct.ThrowIfCancellationRequested();
            _probe.TryPoke(device.Ip);
            string? macA = PeekMac(device.Ip);
            await Task.Delay(250, ct).ConfigureAwait(false);
            _probe.TryPoke(device.Ip);
            string? macB = PeekMac(device.Ip);

            entries.RemoveAll(e => string.Equals(e.Ip, device.Ip, StringComparison.Ordinal));
            if (macA is not null && macB is not null && !string.Equals(macA, macB, StringComparison.Ordinal))
            {
                conflicted.Add(device.Ip);
                events.Insert(0, new LanEvent(LanEventType.Conflict, device.Ip, macA, macB,
                    $"IP 冲突（即时复核）：{device.Ip} 双探应答不同 MAC（{macA} / {macB}）", DateTimeOffset.Now));
                entries.Add(new LanBaselineEntry(device.Ip, macB, device.Hostname, device.Vendor, DateTimeOffset.Now, DateTimeOffset.Now));
            }
            else
            {
                events.Insert(0, new LanEvent(LanEventType.BindingChanged, device.Ip, null, device.Mac,
                    $"绑定变更：{device.Ip} → {device.Mac}（复核稳定）", DateTimeOffset.Now));
                entries.Add(LanDiffEngine.ConfirmRecheck(device, DateTimeOffset.Now));
            }
        }
    }

    private string? PeekMac(string ip) =>
        _probe.ReadNeighbors().FirstOrDefault(n => string.Equals(n.Ip, ip, StringComparison.Ordinal)).Mac;

    /// <summary>反向 DNS：单次 400ms 超时降级 null；悬挂任务显式吞异常，杜绝 UnobservedTaskException。</summary>
    private static async Task<string?> ResolveHostnameAsync(string ip)
    {
        if (!IPAddress.TryParse(ip, out IPAddress? addr))
        {
            return null;
        }

        Task<IPHostEntry> query = Dns.GetHostEntryAsync(addr);
        _ = query.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        Task winner = await Task.WhenAny(query, Task.Delay(400, CancellationToken.None)).ConfigureAwait(false);
        if (winner != query)
        {
            return null;
        }

        try
        {
            IPHostEntry entry = await query.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (Exception)
        {
            return null; // 无 PTR 记录是常态，不记日志防刷屏
        }
    }
}
