namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// 量化探测结果（M6a P0-1）：丢包率与延迟分布。
/// <para>
/// 语义约定：延迟三值仅在 <see cref="Received"/> &gt; 0 时有意义——全丢包时为 null
/// （「延迟无从谈起」≠「延迟为 0」，与「读不到值一律降级绝不猜」原则同源）。
/// </para>
/// </summary>
public sealed record PingQuantifyResult(
    int Sent,
    int Received,
    int? MinMs,
    int? AvgMs,
    int? MaxMs)
{
    /// <summary>丢包率百分比（四舍五入取整）；全部成功为 0。</summary>
    public int LossPercent => Sent == 0 ? 0 : (int)Math.Round((Sent - Received) * 100.0 / Sent);
}
