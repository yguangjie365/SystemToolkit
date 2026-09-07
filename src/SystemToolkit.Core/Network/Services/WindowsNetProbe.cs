using System.Net.NetworkInformation;
using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="INetProbe"/> 的真实实现：BCL <c>Ping</c> + <c>Dns.GetHostAddressesAsync</c>。
/// 系统边界不做单测（真实网络不可控）；结论规则由诊断服务持 fake probe 覆盖。
/// M6a 增量化探测（延迟统计）与 DF 位探测（<c>PingOptions.DontFragment</c>，
/// MTU 路径二分的探针——不走 ping.exe，规避本地化输出解析坑）。
/// </summary>
public sealed class WindowsNetProbe : INetProbe
{
    /// <inheritdoc cref="INetProbe.PingAsync"/>
    public async Task<bool> PingAsync(string address, int timeoutMs, CancellationToken ct = default)
    {
        PingReply? reply = await TryPingAsync(address, timeoutMs, payloadSize: 0, dontFragment: false, ct).ConfigureAwait(false);
        return reply is not null;
    }

    /// <inheritdoc cref="INetProbe.PingQuantifyAsync"/>
    public async Task<PingQuantifyResult> PingQuantifyAsync(string host, int count, int timeoutMs, int intervalMs, CancellationToken ct = default)
    {
        var latencies = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            PingReply? reply = await TryPingAsync(host, timeoutMs, payloadSize: 0, dontFragment: false, ct).ConfigureAwait(false);
            if (reply is not null)
            {
                latencies.Add((int)reply.RoundtripTime);
            }

            // 包间间隔不计最后一轮（避免无谓的收尾等待）
            if (i < count - 1 && intervalMs > 0)
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }

        if (latencies.Count == 0)
        {
            return new PingQuantifyResult(count, 0, null, null, null);
        }

        return new PingQuantifyResult(
            count,
            latencies.Count,
            latencies.Min(),
            (int)Math.Round(latencies.Average()),
            latencies.Max());
    }

    /// <inheritdoc cref="INetProbe.PingDontFragmentAsync"/>
    public async Task<bool> PingDontFragmentAsync(string host, int payloadSize, int timeoutMs, CancellationToken ct = default)
    {
        PingReply? reply = await TryPingAsync(host, timeoutMs, payloadSize, dontFragment: true, ct).ConfigureAwait(false);
        return reply is not null;
    }

    /// <summary>
    /// 统一 Ping 入口：返回成功回复或 null（任何失败——超时 / DF 拆分需求 / 解析失败）。
    /// DF 探测把「需要拆分」视作失败（该尺寸不可通过），由二分逻辑收敛边界。
    /// </summary>
    private static async Task<PingReply?> TryPingAsync(string host, int timeoutMs, int payloadSize, bool dontFragment, CancellationToken ct)
    {
        try
        {
            using Ping ping = new();
            var options = new PingOptions(ttl: 64, dontFragment);
            byte[] buffer = payloadSize > 0 ? new byte[payloadSize] : [];
            PingReply reply = await ping.SendPingAsync(host, timeoutMs, buffer, options).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    /// <inheritdoc cref="INetProbe.ConnectTcpAsync"/>
    public async Task<Models.TcpProbeResult> ConnectTcpAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            sw.Stop();
            return new Models.TcpProbeResult(true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            return new Models.TcpProbeResult(false, null, "连接被拒绝（端口未开放或服务未运行）");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Models.TcpProbeResult(false, null, $"连接超时（{timeoutMs} ms）——目标不可达或被防火墙拦截");
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound)
        {
            return new Models.TcpProbeResult(false, null, "域名无法解析——顺带说明 DNS 有问题");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new Models.TcpProbeResult(false, null, $"连接失败：{ex.Message}");
        }
    }

    /// <inheritdoc cref="INetProbe.ResolveAsync"/>
    public async Task<bool> ResolveAsync(string host, CancellationToken ct = default)
    {
        try
        {
            System.Net.IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
