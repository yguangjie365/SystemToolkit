using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>快照校验结果。</summary>
/// <param name="Total">清单登记的文件总数。</param>
/// <param name="Ok">哈希一致数。</param>
/// <param name="Failed">哈希不一致数。</param>
/// <param name="Missing">文件缺失数。</param>
/// <param name="Failures">失败明细（文件相对路径 + 原因），已截断。</param>
public sealed record SnapshotVerifyReport(
    int Total,
    int Ok,
    int Failed,
    int Missing,
    IReadOnlyList<string> Failures)
{
    /// <summary>本次**实际校验**的文件数（抽样时小于 <see cref="Total"/>）。</summary>
    public int Checked { get; init; }

    /// <summary>是否为抽样校验（只查了部分文件）。</summary>
    public bool IsSampled => Checked > 0 && Checked < Total;

    /// <summary>
    /// 本次校验的样本是否全部一致。
    /// 🔴 **抽样时它只代表"样本通过"**，不代表快照整体完整——文案必须写清（见 <see cref="Message"/>）。
    /// 把抽样结果当"校验通过"报出去属于**状态欺骗**，本仓明令禁止。
    /// </summary>
    public bool Success => Failed == 0 && Missing == 0;

    /// <summary>人类可读摘要（抽样必须显式声明"未做完整校验"）。</summary>
    public string Message => Success
        ? IsSampled
            ? $"抽样校验通过（已查 {Checked}/{Total}，未做完整校验）"
            : $"校验通过（{Ok}/{Total} 个文件哈希一致）"
        : IsSampled
            ? $"抽样校验未通过（一致 {Ok}、不一致 {Failed}、缺失 {Missing}，已查 {Checked}/{Total}）"
            : $"校验未通过（一致 {Ok}、不一致 {Failed}、缺失 {Missing}，共 {Total}）";
}

/// <summary>
/// 快照完整性校验：按 manifest 登记的 SHA-256 重算快照目录内文件哈希并比对。
/// 2026-09-07 补齐旧版「校验」命令能力（旧版在 MainViewModel.Operations 内实现，
/// 本项目下沉为 Core 服务以便单测与未来「批量校验」复用）。
/// </summary>
public sealed class SnapshotVerifier
{
    private const int ReportEvery = 50;

    private readonly ILogger _logger;
    private readonly int _maxWorkers;

    /// <summary>创建校验器。</summary>
    /// <param name="maxWorkers">并行度（钳 1..8）。</param>
    /// <param name="logger">日志（缺省空实现）。</param>
    public SnapshotVerifier(int maxWorkers = 4, ILogger? logger = null)
    {
        _maxWorkers = Math.Clamp(maxWorkers, 1, 8);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 校验快照：<paramref name="snapshotDir"/> 下 <c>files</c> 子目录内文件与
    /// <paramref name="info"/> 清单逐条比对（哈希 + 存在性）。
    /// <para>
    /// <c>sampleSize</c>：抽样条数——<c>0</c> 或 ≥ 清单总数表示**全量**，<c>&gt;0</c> 表示只校验
    /// <see cref="SelectSample"/> 确定性抽取的子集（抽样结果的语义与文案见
    /// <see cref="SnapshotVerifyReport.IsSampled"/>：抽样通过**不等于**完整校验）。
    /// </para>
    /// </summary>
    public async Task<SnapshotVerifyReport> VerifyAsync(
        SnapshotInfo info,
        string snapshotDir,
        IProgressReporter? reporter = null,
        CancellationToken ct = default,
        int sampleSize = 0)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(snapshotDir))
        {
            throw new ArgumentException("快照目录不能为空。", nameof(snapshotDir));
        }

        string filesDir = Path.Combine(snapshotDir, SnapshotManager.FilesDir);
        List<FileEntry> entries = info.Files ?? new List<FileEntry>();
        int total = entries.Count;
        List<FileEntry> toCheck = SelectSample(entries, sampleSize);
        int planned = toCheck.Count;
        int ok = 0, failed = 0, missing = 0;
        var failures = new List<string>();
        int done = 0;

        await Parallel.ForEachAsync(
            toCheck,
            new ParallelOptions { MaxDegreeOfParallelism = _maxWorkers, CancellationToken = ct },
            (entry, token) =>
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(filesDir, entry.RelativePath ?? "");
                int index = Interlocked.Increment(ref done);

                if (!File.Exists(path))
                {
                    Interlocked.Increment(ref missing);
                    lock (failures)
                    {
                        if (failures.Count < 20)
                        {
                            failures.Add($"{entry.RelativePath}：文件缺失");
                        }
                    }
                }
                else
                {
                    try
                    {
                        string actual = Sha256Hasher.HashFile(path);
                        if (string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Increment(ref ok);
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                            lock (failures)
                            {
                                if (failures.Count < 20)
                                {
                                    failures.Add($"{entry.RelativePath}：哈希不一致");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        lock (failures)
                        {
                            if (failures.Count < 20)
                            {
                                failures.Add($"{entry.RelativePath}：读取失败（{ex.Message}）");
                            }
                        }
                    }
                }

                if (reporter is not null && (index % ReportEvery == 0 || index == planned))
                {
                    reporter.OnProgress(index, total, "校验");
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var report = new SnapshotVerifyReport(total, ok, failed, missing, failures) { Checked = planned };
        _logger.Info($"快照校验：{info.SnapshotId} {report.Message}");
        return report;
    }

    /// <summary>
    /// 确定性抽样：按固定步长从清单里取至多 <paramref name="sampleSize"/> 条
    /// （<paramref name="sampleSize"/> ≤ 0 或 ≥ 总数时返回全量）。
    /// 刻意**不用随机**：同一份清单每次取到同一批样本——问题可复现、单测可钉实现。
    /// </summary>
    internal static List<FileEntry> SelectSample(List<FileEntry> entries, int sampleSize)
    {
        if (sampleSize <= 0 || sampleSize >= entries.Count)
        {
            return entries;
        }

        var picked = new List<FileEntry>(sampleSize);
        double step = (double)entries.Count / sampleSize;
        for (int i = 0; i < sampleSize; i++)
        {
            picked.Add(entries[(int)(i * step)]);
        }

        return picked;
    }
}
