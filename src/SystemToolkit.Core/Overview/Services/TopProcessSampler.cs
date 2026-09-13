namespace SystemToolkit.Core.Overview.Services;

/// <summary>
/// 单个进程的一次占用读数。
/// <see cref="CpuPercent"/> 为 null = 本拍**没有 CPU 基线**（首拍，或该进程本拍才出现），
/// **不得当作 0% 展示**——没有证据的数字就是假数据（本仓「状态诚实」红线的同型）。
/// </summary>
/// <param name="Pid">进程 ID（同名进程靠它区分，实测本机存在 `WorkBuddy#1`/`WorkBuddy#3` 这类同名多实例）。</param>
/// <param name="Name">进程名（不含扩展名，与任务管理器口径一致）。</param>
/// <param name="CpuPercent">占**整机** CPU 的百分比（0-100，已按逻辑核数归一化）；null = 无基线。</param>
/// <param name="WorkingSetBytes">工作集（物理内存占用，字节）。</param>
public sealed record ProcessUsageRow(int Pid, string Name, double? CpuPercent, long WorkingSetBytes);

/// <summary>一次 Top 进程采样结果。</summary>
/// <param name="CpuTop">CPU 占用最高的若干进程（按占比降序）；无基线时为空列表。</param>
/// <param name="MemoryTop">内存占用最高的若干进程（按工作集降序）。</param>
/// <param name="HasCpuBaseline">本拍是否已有可用 CPU 基线（false = CPU 列无数据，UI 须如实说明）。</param>
/// <param name="SampledCount">本次成功读到读数的进程数（诊断用）。</param>
public sealed record TopProcessSnapshot(
    IReadOnlyList<ProcessUsageRow> CpuTop,
    IReadOnlyList<ProcessUsageRow> MemoryTop,
    bool HasCpuBaseline,
    int SampledCount);

/// <summary>
/// Top 进程占用采样（概览页「实时占用进程」卡的数据源）。
/// 与 <see cref="LiveUsageSampler"/> / <see cref="QuickPulseSampler"/> 同一设计约定：
/// **采样失败折叠为 null 分量，绝不抛出**。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **数据源选型是实测结论，不是偏好**（2026-09-13）：WMI 的
/// <c>Win32_PerfFormattedData_PerfProc_Process</c> 在本机 168 个进程下单次查询 **6291 ms**，
/// 塞进 2 秒节拍会把概览页彻底拖死；改走 <see cref="System.Diagnostics.Process.GetProcesses()"/>
/// + <c>TotalProcessorTime</c> 差分，实测首轮 102 ms、后续 7 ms（快约 230 倍）。
/// 两条路已交叉验证：WMI 报的单核口径 88%（20 逻辑核）÷ 20 = 4.4%，与本类差分算出的 4.4% 吻合。
/// </para>
/// <para>
/// CPU 归一化口径 = **占整机 CPU 的百分比**（与任务管理器一致）：
/// <c>ΔCPU 时间 ÷ Δ墙钟时间 ÷ 逻辑核数 × 100</c>。
/// </para>
/// <para>
/// 已知局限（如实声明，不掩盖）：本类以「该 PID 上拍存在 + Δ 非负」判定基线可续，**未校验 PID 复用**。
/// 若某 PID 在两次采样之间被新进程复用、且新进程的累计 CPU 时间恰好大于旧值，那一拍会报出偏高的
/// 百分比；2 秒窗口内该情形概率极低，且下一拍即自愈。校验复用要读 <c>Process.StartTime</c>
/// （对受保护进程会抛），异常面大于收益。
/// </para>
/// </remarks>
public sealed class TopProcessSampler
{
    /// <summary>默认榜单长度（CPU / 内存各取前 N）。</summary>
    public const int DefaultTopCount = 5;

    /// <summary>
    /// 低于该采样间隔（秒）不做差分：分母过小会把 CPU 百分比放大成噪声。
    /// </summary>
    private const double MinElapsedSeconds = 0.25;

    private readonly int _topCount;

    /// <summary>上一拍的各 PID 累计 CPU 时间（秒）。每拍整体替换，已退出进程自然淘汰。</summary>
    private readonly Dictionary<int, double> _lastCpuSeconds = new();

    /// <summary>上一拍的采样时刻（<see cref="System.Diagnostics.Stopwatch"/> 时基的秒）。</summary>
    private double _lastSampleSeconds;

    /// <summary>初始化采样器。</summary>
    /// <param name="topCount">每个榜单保留的条数（默认 <see cref="DefaultTopCount"/>）。</param>
    public TopProcessSampler(int topCount = DefaultTopCount)
    {
        _topCount = Math.Max(1, topCount);
    }

    /// <summary>
    /// 该 PID 是否计入采样。排除 <b>Idle（PID 0）</b>：它的「CPU 时间」表示的是 CPU 空闲时间
    /// （本机 20 逻辑核下空闲占比常年在 90% 以上），纳入会直接霸榜——它不是一个真正的进程。
    /// </summary>
    /// <param name="pid">进程 ID。</param>
    internal static bool IsCountedProcess(int pid) => pid != 0;

    /// <summary>采样一次 Top 进程（应在后台线程调用）；整体失败返回 null。</summary>
    public Task<TopProcessSnapshot?> SampleAsync() => Task.Run(Sample);

    /// <summary>同步采样一次；任何异常折叠为 null（不抛出）。</summary>
    public TopProcessSnapshot? Sample()
    {
        try
        {
            System.Diagnostics.Process[] all = System.Diagnostics.Process.GetProcesses();
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            bool hasBaseline = _lastCpuSeconds.Count > 0;
            double elapsed = now - _lastSampleSeconds;
            // 🔴 窗口未满（调用方以 < 0.25s 的高频调用）时**不推进基准**，让窗口自然累积；
            // 若每拍都推进，elapsed 会恒小于阈值 → 这个功能对高频调用者**永久静默失效**
            // （"界面在骗人"的同型：功能看着在跑，数据永远没有）。生产节拍是 2 秒，远大于阈值。
            bool canDiff = hasBaseline && elapsed >= MinElapsedSeconds;
            int cores = Math.Max(1, Environment.ProcessorCount);

            var rows = new List<ProcessUsageRow>(all.Length);
            var current = new Dictionary<int, double>(all.Length);

            foreach (System.Diagnostics.Process p in all)
            {
                try
                {
                    int id = p.Id;
                    if (!IsCountedProcess(id))
                    {
                        continue;
                    }

                    string name = p.ProcessName;
                    double cpuSeconds = p.TotalProcessorTime.TotalSeconds;
                    long workingSet = p.WorkingSet64;
                    current[id] = cpuSeconds;

                    double? percent = null;
                    if (canDiff && _lastCpuSeconds.TryGetValue(id, out double previous))
                    {
                        double delta = cpuSeconds - previous;
                        // Δ 为负 = 计数器回退 / PID 被复用：宁可本拍不给数字，也不报一个错的
                        if (delta >= 0)
                        {
                            percent = Math.Clamp(100.0 * delta / elapsed / cores, 0, 100);
                        }
                    }

                    rows.Add(new ProcessUsageRow(id, name, percent, workingSet));
                }
                catch
                {
                    // 受保护 / 已退出的进程读不到读数：跳过这一个，不打断整轮（不是错误）
                }
                finally
                {
                    p.Dispose();
                }
            }

            // 换一份新表：让已退出进程自然淘汰，字典不会随时间无限增长。
            // 只在「首拍」或「窗口已满」时推进基准（理由见上方 canDiff 处的说明）。
            if (!hasBaseline || canDiff)
            {
                _lastCpuSeconds.Clear();
                foreach (KeyValuePair<int, double> pair in current)
                {
                    _lastCpuSeconds[pair.Key] = pair.Value;
                }

                _lastSampleSeconds = now;
            }

            return new TopProcessSnapshot(
                SelectTopByCpu(rows, _topCount),
                SelectTopByMemory(rows, _topCount),
                canDiff,
                rows.Count);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按 CPU 占比取前 N：无占比的（null）不入榜（不拿 0% 冒充）；
    /// 同值按 PID 升序——**稳定序，便于断言**（随机序会让用例变成"看运气"）。
    /// </summary>
    /// <param name="rows">待筛的读数集合。</param>
    /// <param name="count">保留条数。</param>
    internal static IReadOnlyList<ProcessUsageRow> SelectTopByCpu(IReadOnlyList<ProcessUsageRow> rows, int count) =>
        rows.Where(r => r.CpuPercent is not null)
            .OrderByDescending(r => r.CpuPercent!.Value)
            .ThenBy(r => r.Pid)
            .Take(count)
            .ToList();

    /// <summary>按工作集取前 N；同值按 PID 升序（稳定序，便于断言）。</summary>
    /// <param name="rows">待筛的读数集合。</param>
    /// <param name="count">保留条数。</param>
    internal static IReadOnlyList<ProcessUsageRow> SelectTopByMemory(IReadOnlyList<ProcessUsageRow> rows, int count) =>
        rows.OrderByDescending(r => r.WorkingSetBytes)
            .ThenBy(r => r.Pid)
            .Take(count)
            .ToList();
}
