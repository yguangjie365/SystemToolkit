using System.Diagnostics;

namespace SystemToolkit.Core.Network.Connections;

/// <summary>按进程聚合的 TCP 端点概况——回答「谁在占用网络」。</summary>
/// <param name="Pid">进程 ID。</param>
/// <param name="ProcessName">进程名；读不到时为 <see cref="ProcessConnectionSummaryBuilder.UnknownProcessName"/>。</param>
/// <param name="Total">该进程的端点总数（含监听与各种中间态）。</param>
/// <param name="Established">其中处于已建立态的条数。</param>
/// <param name="Listening">其中处于监听态的条数。</param>
/// <param name="ListenPorts">监听中的本地端口（升序）。</param>
public sealed record ProcessConnectionSummary(
    int Pid,
    string ProcessName,
    int Total,
    int Established,
    int Listening,
    IReadOnlyList<int> ListenPorts);

/// <summary>
/// 把 <see cref="TcpConnectionTable"/> 的端点表按进程聚合。纯函数，不做 IO，便于离线断言。
/// </summary>
public static class ProcessConnectionSummaryBuilder
{
    /// <summary>默认榜单长度。</summary>
    public const int DefaultTopCount = 10;

    /// <summary>进程名读不到时的显示名（进程已退出 / 权限不足——这是常态，不是错误）。</summary>
    public const string UnknownProcessName = "（未知进程）";

    /// <summary>按进程聚合，并取端点总数最多的前 <paramref name="topCount"/> 个。</summary>
    /// <param name="rows">TCP 端点表（可为空集合）。</param>
    /// <param name="topCount">榜单长度（≤0 时返回空列表）。</param>
    public static IReadOnlyList<ProcessConnectionSummary> Build(
        IReadOnlyList<TcpConnectionRow> rows,
        int topCount = DefaultTopCount)
    {
        var grouped = new Dictionary<int, Accumulator>();
        foreach (TcpConnectionRow row in rows)
        {
            // 🔴 PID ≤ 0 不是进程：TCP 表里存在 PID 为 0 的保留项（系统占位），纳入会造出一个假"进程"
            if (row.Pid <= 0)
            {
                continue;
            }

            if (!grouped.TryGetValue(row.Pid, out Accumulator? acc))
            {
                acc = new Accumulator();
                grouped[row.Pid] = acc;
            }

            acc.Total++;
            if (row.IsEstablished)
            {
                acc.Established++;
            }

            if (row.IsListening)
            {
                acc.Listening++;
                acc.ListenPorts.Add(row.LocalPort);
            }
        }

        var all = new List<ProcessConnectionSummary>(grouped.Count);
        foreach (KeyValuePair<int, Accumulator> pair in grouped)
        {
            List<int> ports = pair.Value.ListenPorts;
            ports.Sort();
            all.Add(new ProcessConnectionSummary(
                pair.Key,
                ResolveProcessName(pair.Key),
                pair.Value.Total,
                pair.Value.Established,
                pair.Value.Listening,
                ports));
        }

        return SelectTop(all, topCount);
    }

    /// <summary>
    /// 取端点总数最多的前 N 个；同数按 PID 升序——**稳定序，便于断言**
    /// （不稳定排序会让用例变成"看运气"）。
    /// </summary>
    internal static IReadOnlyList<ProcessConnectionSummary> SelectTop(
        IReadOnlyList<ProcessConnectionSummary> all,
        int topCount)
    {
        if (topCount <= 0)
        {
            return Array.Empty<ProcessConnectionSummary>();
        }

        return all.OrderByDescending(s => s.Total)
            .ThenBy(s => s.Pid)
            .Take(topCount)
            .ToList();
    }

    /// <summary>
    /// 解析 PID 对应的进程名；读不到（进程已退出 / 权限不足）返回 <see cref="UnknownProcessName"/>，
    /// **绝不抛出**——聚合过程因一个进程读不到名就整体失败是不可接受的。
    /// </summary>
    internal static string ResolveProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.IsNullOrEmpty(process.ProcessName) ? UnknownProcessName : process.ProcessName;
        }
        catch
        {
            return UnknownProcessName;
        }
    }

    /// <summary>单进程的累加器（分组中间态）。</summary>
    private sealed class Accumulator
    {
        /// <summary>端点总数。</summary>
        public int Total { get; set; }

        /// <summary>已建立条数。</summary>
        public int Established { get; set; }

        /// <summary>监听条数。</summary>
        public int Listening { get; set; }

        /// <summary>监听端口集合。</summary>
        public List<int> ListenPorts { get; } = new();
    }
}
