using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace SystemToolkit.Core.Network.Services;

/// <summary>持续 ping 的单次采样（Success=false 时 Error 说明失败原因；延迟单位 ms）。</summary>
public sealed record PingSample(int Seq, bool Success, int? LatencyMs, string? Error, DateTime At);

/// <summary>
/// 持续 ping（2026-09-06 新增，参考 bp2008/pingtracer（MIT）的连续探测思路）：按固定间隔
/// 对目标连续探测并逐包上报——定位「时通时断」「延迟抖动」类问题的标准手段，弥补
/// 诊断链丢包量化（固定 10 包）覆盖不了的长时间观察场景。
/// <para>取消即停止（调用方持有 CancellationToken）；逐包经 <see cref="IProgress{T}"/> 上报，
/// 不批量攒结果。BCL Ping 不需要管理员权限。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ContinuousPingService
{
    /// <summary>单包超时（毫秒；与诊断链 PingTimeoutMs 同口径）。</summary>
    public const int PingTimeoutMs = 1500;

    /// <summary>
    /// 持续探测：每 <paramref name="intervalMs"/> 毫秒一发，逐包上报，取消即停。
    /// host 支持 IP 与域名；域名解析失败按失败包上报（Error 说明），不终止循环。
    /// </summary>
    public async Task RunAsync(string host, int intervalMs, IProgress<PingSample> progress, CancellationToken ct)
    {
        if (intervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMs), intervalMs, "间隔必须为正数");
        }

        int seq = 0;
        while (!ct.IsCancellationRequested)
        {
            seq++;
            try
            {
                using var ping = new Ping();
                PingReply reply = await ping.SendPingAsync(host, PingTimeoutMs).ConfigureAwait(false);
                bool ok = reply.Status == IPStatus.Success;
                progress.Report(new PingSample(
                    seq, ok,
                    ok ? Convert.ToInt32(reply.RoundtripTime) : null,
                    ok ? null : reply.Status.ToString(),
                    DateTime.Now));
            }
            catch (Exception ex)
            {
                // 域名无法解析 / 网络栈异常：按失败包上报，持续探测不因此中断
                progress.Report(new PingSample(seq, Success: false, null, ex.Message, DateTime.Now));
            }

            try
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
