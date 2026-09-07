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
    /// <summary>全部一致（无缺失、无不一致）为通过。</summary>
    public bool Success => Failed == 0 && Missing == 0;

    /// <summary>人类可读摘要。</summary>
    public string Message => Success
        ? $"校验通过（{Ok}/{Total} 个文件哈希一致）"
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
    /// </summary>
    public async Task<SnapshotVerifyReport> VerifyAsync(
        SnapshotInfo info,
        string snapshotDir,
        IProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(snapshotDir))
        {
            throw new ArgumentException("快照目录不能为空。", nameof(snapshotDir));
        }

        string filesDir = Path.Combine(snapshotDir, SnapshotManager.FilesDir);
        List<FileEntry> entries = info.Files ?? new List<FileEntry>();
        int total = entries.Count;
        int ok = 0, failed = 0, missing = 0;
        var failures = new List<string>();
        int done = 0;

        await Parallel.ForEachAsync(
            entries,
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

                if (reporter is not null && (index % ReportEvery == 0 || index == total))
                {
                    reporter.OnProgress(index, total, "校验");
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var report = new SnapshotVerifyReport(total, ok, failed, missing, failures);
        _logger.Info($"快照校验：{info.SnapshotId} {report.Message}");
        return report;
    }
}
