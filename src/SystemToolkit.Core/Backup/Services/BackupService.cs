using System.Collections.Concurrent;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>备份引擎：扫描、并发复制+SHA256、磁盘检查、快照写入、上限清理、取消。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BackupService : IBackupService
{
    private readonly BackupConfigService _config;
    private readonly int? _maxWorkersOverride;
    /// <summary>磁盘空间安全余量倍数（20%：manifest/meta + 目录开销 + .old 归档）。</summary>
    private const double SpaceSafetyMultiplier = 1.2;

    private readonly ILogger _logger;
    private readonly ElevatedVssClient? _vss;

    /// <summary>创建备份引擎；<paramref name="maxWorkers"/> 为并行度覆盖（测试注入用），null 时读配置 MaxWorkers；<paramref name="vssClient"/> 缺省未注入（UseVss 规则回退普通复制）。</summary>
    public BackupService(BackupConfigService config, int? maxWorkers = null, ILogger? logger = null, ElevatedVssClient? vssClient = null)
    {
        _config = config;
        _maxWorkersOverride = maxWorkers;
        _logger = logger ?? NullLogger.Instance;
        _vss = vssClient;
    }

    /// <summary>
    /// 执行一条备份规则：源校验 → 重叠/空间检查 → 扫描 → 并发复制 + SHA-256 → 写快照元数据 → 上限清理。
    /// 失败不抛异常，统一经 <see cref="BackupResult"/> 的 Success/Failures/Canceled 表达。
    /// 开启 <c>UseVss</c> 且 VSS 通道可用时，先创建卷影快照租约（外层 finally 统一释放），
    /// 复制阶段从快照设备路径读取——可备份被占用的文件；创建失败自动回退普通复制并留痕。
    /// </summary>
    public async Task<BackupResult> BackupRuleAsync(
        BackupRule rule,
        IProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        var vssLeases = new List<VssLease>();
        // LOG-2：操作边界三字段留痕（Action/Result/Duration）；GUI 与 --backup-worker 同走本入口
        LogTiming timing = _logger.Time("BackupRule");
        try
        {
            BackupResult result = await BackupRuleCoreAsync(rule, reporter, ct, vssLeases).ConfigureAwait(false);
            timing.Complete(
                result.Success ? LogResult.Success : result.Canceled ? LogResult.Cancelled : LogResult.Failed,
                result.Success ? LogLevel.Info : LogLevel.Warn,
                $"规则「{rule.RuleName}」备份结束：{result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            timing.Complete(LogResult.Failed, LogLevel.Error, $"规则「{rule.RuleName}」备份中止", ex);
            throw;
        }
        finally
        {
            foreach (VssLease lease in vssLeases)
            {
                try
                { await lease.DisposeAsync().ConfigureAwait(false); }
                catch { /* 快照删除失败不改变备份结果，留痕由删除侧负责 */ }
            }
        }
    }

    private async Task<BackupResult> BackupRuleCoreAsync(
        BackupRule rule,
        IProgressReporter? reporter,
        CancellationToken ct,
        List<VssLease> vssLeases)
    {
        Action<string>? log = reporter is null ? null : (Action<string>)reporter.OnLog;
        string sourceDesc = string.Join("；", rule.Sources());
        log?.Invoke($"【备份】规则「{rule.RuleName}」开始，源路径：{sourceDesc}");
        string? snapDir = null;
        bool filesCopied = false;
        bool metaWritten = false;

        try
        {
            // 校验源路径存在
            var missing = rule.Sources().Where(s => !Directory.Exists(s) && !File.Exists(s)).ToList();
            if (missing.Count > 0)
                throw new IOException("以下源路径不存在：" + string.Join("；", missing));

            // 备份根先就位：重叠检查与空间检查都依赖它，且两者都不该等到扫描完才做
            string backupRoot = rule.EffectiveBackupRoot(_config.Settings.BackupRoot);
            try
            { Directory.CreateDirectory(backupRoot); }
            catch (Exception ex) { throw new IOException($"备份根路径不可用（{backupRoot}）：{ex.Message}"); }

            // 安全校验：拒绝备份根目录与源路径重叠（源包含目标或目标包含源），
            // 避免递归自我备份——扫描时将备份产物再次扫入，导致无限膨胀。
            // 必须放在全量扫描之前：否则“备份根位于源目录内”这种配置要先白白扫完
            // 整棵目录树（大目录可达数分钟）才报错。
            string? overlap = CheckPathOverlap(rule.Sources(), backupRoot);
            if (overlap is not null)
                throw new IOException($"备份目标路径与源路径重叠（{overlap}），可能导致递归自我备份。请将备份目录设置到源路径之外。");

            // VSS 卷影（批次二）：UseVss 且提权通道可用时，对各源卷创建快照，
            // 复制阶段改从快照设备路径读取（可备份被占用的文件，内容时间点一致）。
            // 任一卷创建失败 → 释放已创建租约、留痕并整体回退普通复制（部分成功语义不变）。
            var shadowMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (rule.UseVss && _vss is not null)
            {
                reporter?.OnPhase("正在创建 VSS 卷影快照…");
                foreach (string root in rule.Sources()
                    .Select(s => Path.GetPathRoot(Path.GetFullPath(s)) ?? "")
                    .Where(r => r.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    VssLease? lease = await _vss.CreateAsync(root, log, ct).ConfigureAwait(false);
                    if (lease is null)
                    {
                        log?.Invoke($"⚠️ 卷 {root} 的 VSS 快照创建失败，本次备份回退普通复制（被占用文件可能失败）");
                        foreach (VssLease created in vssLeases)
                            await created.DisposeAsync().ConfigureAwait(false);
                        vssLeases.Clear();
                        shadowMap.Clear();
                        break;
                    }

                    vssLeases.Add(lease);
                    shadowMap[root] = lease.DevicePath.TrimEnd('\\');
                }

                if (shadowMap.Count > 0)
                {
                    log?.Invoke($"✅ VSS 快照就绪（{shadowMap.Count} 个卷），本次将从卷影读取文件");
                }
            }

            // 扫描（同步递归，大目录可能耗时 → 移入线程池避免阻塞调用线程）
            reporter?.OnPhase($"正在扫描源文件：{sourceDesc}");
            ScanResult scan = await Task.Run(() => DirectoryScanner.ScanMultiSources(rule, _logger), ct);
            if (scan.Files.Count == 0)
                throw new IOException("未发现任何可备份的文件。");

            long totalSize = scan.Files.Sum(f => f.Length);

            // 磁盘空间检查：无法确认（网络路径 / 驱动器未就绪）时不阻断，但必须留痕，
            // 不能像过去那样被静默当成"空间充足"而继续复制到一半失败。
            // 含 20% 安全余量（manifest/meta + 目录开销 + .old 归档），
            // 避免"刚好够"却在写元数据时耗尽空间导致半截失败。VSS 卷影在源卷，不计入。
            long required = (long)(totalSize * SpaceSafetyMultiplier);
            DiskSpaceCheck space = DiskSpaceUtil.Check(backupRoot, required);
            if (space == DiskSpaceCheck.Unknown)
            {
                _logger.Warn($"无法确认备份目标「{backupRoot}」的剩余空间（网络路径或驱动器未就绪），继续备份但存在空间耗尽风险");
                log?.Invoke("⚠️ 无法确认目标磁盘剩余空间（网络路径或未就绪驱动器），将继续备份");
            }
            else if (space == DiskSpaceCheck.Insufficient)
            {
                throw new IOException($"目标磁盘剩余空间不足：需要约 {FormatUtil.FormatSize(required)}（含 20% 余量），请清理磁盘或更换备份路径。");
            }

            // 创建快照目录
            var snapMgr = SnapshotManager.FromRule(rule, _config.Settings.BackupRoot, _logger);
            snapDir = snapMgr.CreateSnapshotDir();
            string filesDir = Path.Combine(snapDir, SnapshotManager.FilesDir);
            string snapTs = Path.GetFileName(snapDir);

            // 并发复制 + 校验（MaxWorkers 调用时读取，设置变更后下次备份即生效）
            int maxWorkers = _maxWorkersOverride ?? _config.Settings.MaxWorkers;

            // 🔴 B5b-③：未变文件复用（**默认关**）的复用源 = 上一份**成功**快照。
            //    部分成功的快照清单本身不完整（缺的文件不在里面），不适合当基准。
            IReadOnlyDictionary<string, FileEntry>? reuseEntries = null;
            string? reuseFilesDir = null;
            if (_config.Settings.SkipUnchangedFiles)
            {
                (string Dir, SnapshotInfo Info)? baseline = snapMgr.LatestSnapshotWithDir();
                if (baseline is { } b && b.Info.Status == SnapshotStatuses.Success && b.Info.Files.Count > 0)
                {
                    // 按相对路径索引；多源扫描已保证相对路径唯一（带源前缀），此处仅做防御性去重
                    reuseEntries = b.Info.Files
                        .Where(f => !string.IsNullOrEmpty(f.RelativePath))
                        .GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    reuseFilesDir = Path.Combine(b.Dir, SnapshotManager.FilesDir);
                    log?.Invoke($"未变文件跳过已开启：以「{Path.GetFileName(b.Dir)}」为复用源"
                        + "（仅当字节数与修改时间都一致时才复用；不一致一律重新复制）");
                }
                else
                {
                    log?.Invoke("未变文件跳过已开启，但没有可作为基准的成功快照 → 本次全部重新复制");
                }
            }

            reporter?.OnPhase("正在备份文件（复制 + SHA-256 校验）…");
            CopyOutcome copy = await CopyAndVerifyAsync(
                scan.Files, filesDir, reporter, maxWorkers, shadowMap, reuseEntries, reuseFilesDir, ct);
            List<FileEntry> entries = copy.Entries;
            List<string> failures = copy.Failures;
            if (copy.LinkFallbackCount > 0)
            {
                // 让"硬链接不可用"**可见**：否则现象是"开了未变跳过却还是那么慢"= 静默失效
                log?.Invoke($"⚠️ 有 {copy.LinkFallbackCount} 个文件判定为未变，但硬链接失败"
                    + "（目标盘可能不支持硬链接，或上一份快照的副本已不在）→ 已回退为重新复制");
            }
            filesCopied = true;

            // 部分成功模式：有失败文件时仍写入快照，标记为 failed
            bool isPartial = failures.Count > 0;

            // 🔴 B5b-③：只要本次存在"复用上一份快照"的条目，本次就**没有读过那些文件的源**。
            //    这既决定校验状态不得写 passed（见下方），也必须在文案里说清。
            bool reusedAny = copy.ReusedCount > 0;
            string reuseText = reusedAny
                ? $"（其中 {copy.ReusedCount} 个与上一份快照一致、未重读源文件；校验状态记为未完整校验）"
                : "";

            // 写快照元数据；失败时保留已复制数据供人工处理（不删目录）
            var info = new SnapshotInfo
            {
                SnapshotId = $"{rule.RuleId}_{snapTs}",
                RuleId = rule.RuleId,
                CreatedAt = snapTs,
                SourcePath = rule.Sources().FirstOrDefault() ?? "",
                SourcePaths = rule.Sources().ToList(),
                RuleName = rule.RuleName,
                BackupPath = filesDir,
                FileCount = entries.Count,
                TotalSize = totalSize,
                Status = isPartial ? SnapshotStatuses.Failed : SnapshotStatuses.Success,
                ReusedFileCount = copy.ReusedCount,
                Files = entries,
                EmptyDirs = scan.EmptyDirs,
            };

            // 🔴 B5a：备份完成后的**读回校验**——复制阶段算的是**源**哈希，目标是否真的写对
            //    从未被验证（见 CopyAndVerifyAsync 的"写入字节即源字节"注释）；
            //    这里读回目标逐条比对，复用 SnapshotVerifier 同一判据（不另写一份校验实现）。
            SnapshotVerifyReport? verify = await RunPostBackupVerifyAsync(info, snapDir, maxWorkers, ct, log).ConfigureAwait(false);

            // 状态诚实化：**只有全量通过才写 passed**；抽样通过 / 未校验写 skipped（不谎报）；
            // 复制有失败或校验未通过一律 failed。
            // 🔴 B5b-③：本次有"复用上一份快照"的条目时**一律不得写 passed** —— 那些文件本次
            //    根本没读源；即便全量读回校验通过，也只证明"快照内的字节与继承来的哈希一致"，
            //    证明不了"源文件当时就是这些字节"。把这种情况报成通过就是状态欺骗。
            string checksumStatus = isPartial || verify is { Success: false }
                ? ChecksumStatuses.Failed
                : reusedAny
                    ? ChecksumStatuses.Skipped
                    : verify is { Success: true, IsSampled: false }
                        ? ChecksumStatuses.Passed
                        : ChecksumStatuses.Skipped;
            info.ChecksumStatus = checksumStatus;
            try
            {
                snapMgr.WriteSnapshot(snapDir, info);
                snapMgr.WriteMeta(snapDir, info);
                metaWritten = true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"⚠️ 快照数据已复制但元数据写入失败，已保留数据目录：{snapDir}（{ex.Message}）");
                return new BackupResult
                {
                    RuleId = rule.RuleId,
                    RuleName = rule.RuleName,
                    Success = false,
                    SnapshotDir = snapDir,
                    FileCount = entries.Count,
                    TotalSize = totalSize,
                    ChecksumStatus = checksumStatus,
                    Message = $"备份数据已复制但元数据写入失败（快照已保留待人工处理）：{ex.Message}",
                    VerifyReport = verify,
                    Failures = [ex.Message],
                };
            }

            // 保留策略：规则显式配了 GFS 就走时间纵深，否则维持固定条数。
            // 旧配置读进来 Gfs 为 null → 行为与升级前完全一致（计划要求的"回退读法"）。
            GfsRetention? gfsPolicy = rule.Gfs ?? _config.Settings.Gfs;
            List<string> removed;
            if (gfsPolicy is { } gfs)
            {
                removed = snapMgr.EnforceGfsLimit(gfs);
            }
            else
            {
                // 上限清理：规则级 MaxSnapshots 钳制到合理范围，防止被设为极大值导致无限累积
                int rawLimit = rule.MaxSnapshots > 0 ? rule.MaxSnapshots : _config.Settings.MaxSnapshots;
                int limit = Math.Clamp(rawLimit, 1, BackupRule.MaxSnapshotsCap);
                removed = snapMgr.EnforceLimit(limit);
            }
            foreach (string r in removed)
                log?.Invoke($"已自动清理最旧快照：{Path.GetFileName(r)}");

            if (isPartial)
            {
                log?.Invoke($"【部分成功】规则「{rule.RuleName}」备份完成：成功 {entries.Count} 个，失败 {failures.Count} 个");
                foreach (string? f in failures.Take(10))
                    log?.Invoke($"  失败：{f}");
                return new BackupResult
                {
                    RuleId = rule.RuleId,
                    RuleName = rule.RuleName,
                    Success = false,
                    SnapshotDir = snapDir,
                    FileCount = entries.Count,
                    TotalSize = totalSize,
                    ChecksumStatus = checksumStatus,
                    Message = $"备份部分成功：成功 {entries.Count} 个，失败 {failures.Count} 个{reuseText}",
                    VerifyReport = verify,
                    ReusedFileCount = copy.ReusedCount,
                    Failures = failures,
                };
            }

            log?.Invoke($"【备份完成】规则「{rule.RuleName}」备份成功：{entries.Count} 个文件{reuseText}");
            return new BackupResult
            {
                RuleId = rule.RuleId,
                RuleName = rule.RuleName,
                Success = true,
                SnapshotDir = snapDir,
                FileCount = entries.Count,
                TotalSize = totalSize,
                ChecksumStatus = checksumStatus,
                VerifyReport = verify,
                Message = $"备份成功：{entries.Count} 个文件{reuseText}",
                ReusedFileCount = copy.ReusedCount,
            };
        }
        catch (OperationCanceledException)
        {
            // 取消：若元数据已写入则保留快照（备份其实已完成），否则清理半成品目录
            if (!metaWritten)
                await Task.Run(() => CleanupSnapshotDir(snapDir));
            log?.Invoke($"【取消】规则「{rule.RuleName}」备份已取消");
            return new BackupResult
            {
                RuleId = rule.RuleId,
                RuleName = rule.RuleName,
                Success = false,
                Canceled = true,
                SnapshotDir = snapDir,
                Message = "备份已取消",
            };
        }
        catch (Exception ex)
        {
            // 数据已复制但元数据未写入：保留目录供人工检查，不静默删除用户数据
            if (filesCopied && !metaWritten)
            {
                log?.Invoke($"⚠️ 备份数据已复制但未写入元数据，保留目录：{snapDir}（{ex.Message}）");
                return new BackupResult
                {
                    RuleId = rule.RuleId,
                    RuleName = rule.RuleName,
                    Success = false,
                    SnapshotDir = snapDir,
                    Message = $"备份数据已保留但未完成元数据写入（{ex.Message}）",
                    Failures = [ex.Message],
                };
            }
            if (!metaWritten)
                await Task.Run(() => CleanupSnapshotDir(snapDir));
            log?.Invoke($"【失败】规则「{rule.RuleName}」{ex.Message}");
            return new BackupResult
            {
                RuleId = rule.RuleId,
                RuleName = rule.RuleName,
                Success = false,
                Message = $"备份失败：{ex.Message}",
                Failures = [ex.Message],
            };
        }
    }

    /// <summary>
    /// 备份完成后的读回校验（B5a）。按设置决定抽样 / 全量 / 关闭。
    /// 🔴 **任何取消与异常都不阻断备份结果**（备份数据已经落盘，不该因为"没能校验"而报失败），
    /// 但一律**留痕**（日志 + 回传 null = 未校验），绝不静默当"通过"。
    /// </summary>
    private async Task<SnapshotVerifyReport?> RunPostBackupVerifyAsync(
        SnapshotInfo info,
        string snapDir,
        int maxWorkers,
        CancellationToken ct,
        Action<string>? log)
    {
        int sample = _config.Settings.VerifyAfterBackupSampleSize;
        if (sample < 0)
        {
            log?.Invoke("⚠️ 已按设置跳过备份后完整性校验：无法证明目标文件写对，校验状态记为 skipped");
            return null;
        }

        try
        {
            int files = info.Files?.Count ?? 0;
            log?.Invoke(sample == 0
                ? $"开始读回校验全部 {files} 个文件（完整性证据）…"
                : $"开始读回抽样校验（最多 {sample}/{files} 个文件）…");

            var verifier = new SnapshotVerifier(maxWorkers, _logger);
            SnapshotVerifyReport report = await verifier
                .VerifyAsync(info, snapDir, null, ct, sample)
                .ConfigureAwait(false);

            log?.Invoke(report.Success
                ? "完整性校验：" + report.Message
                : "⚠️ 完整性校验未通过：" + report.Message);
            if (!report.Success)
            {
                _logger.Warn($"备份后完整性校验未通过：{info.SnapshotId} {report.Message}");
            }

            return report;
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("⚠️ 备份后完整性校验已取消：本次备份未取得完整校验证据（状态记为 skipped）");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"备份后完整性校验未能完成：{ex.Message}");
            log?.Invoke("⚠️ 备份后完整性校验未能完成（状态记为 skipped）：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 并发复制文件并边算 SHA-256；B5b-③ 起支持「未变文件复用」（命中则硬链接上一份快照的副本）。
    /// </summary>
    /// <param name="files">待备份文件。</param>
    /// <param name="filesDir">本次快照的 files 目录。</param>
    /// <param name="reporter">进度回调。</param>
    /// <param name="maxWorkers">并行度。</param>
    /// <param name="shadowMap">VSS：活动路径 → 卷影设备路径。</param>
    /// <param name="reuseEntries">上一份快照的清单（按相对路径索引）；null = 不做复用。</param>
    /// <param name="reuseFilesDir">上一份快照的 files 目录；null = 不做复用。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<CopyOutcome> CopyAndVerifyAsync(
        List<ScannedFile> files,
        string filesDir,
        IProgressReporter? reporter,
        int maxWorkers,
        IReadOnlyDictionary<string, string> shadowMap,
        IReadOnlyDictionary<string, FileEntry>? reuseEntries,
        string? reuseFilesDir,
        CancellationToken ct)
    {
        int total = files.Count;
        int done = 0;
        var failures = new List<string>();
        var entries = new List<FileEntry>();
        int reusedCount = 0;
        int linkFallbackCount = 0;
        object lockObj = new object();
        // 目录创建缓存：同一目录下大量文件并发复制时，避免重复 CreateDirectory 系统调用。
        // 关键实现：用 ConcurrentDictionary.GetOrAdd，其 valueFactory 在 key 真正加入字典前
        // 同步执行，且同一 key 的 factory 只执行一次。这样目录一定在 key 对外可见前创建完成，
        // 彻底避免之前"先加 key 再创建"导致的"Could not find a part of the path"竞态。
        var createdDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(maxWorkers, total)),
            CancellationToken = ct,
        };

        try
        {
            await Parallel.ForEachAsync(files, options, (item, token) =>
            {
                try
                {
                    // VSS 场景：从快照设备路径读取（内容与时间点一致）；manifest 记录的仍是原路径
                    string src = MapToShadow(item.SourcePath, shadowMap);
                    string rel = item.RelativePath;
                    string dst = Path.Combine(filesDir, rel);
                    string dir = Path.GetDirectoryName(dst)!;

                    createdDirs.GetOrAdd(dir, static d =>
                    {
                        Directory.CreateDirectory(d);
                        return 0;
                    });

                    // 🔴 B5b-③ 未变文件复用：先**只看元数据**（不读内容）判断能否复用。
                    //    命中 → 不读源，直接把上一份的副本硬链接过来（同卷零拷贝）；
                    //    硬链接失败（跨卷 / 文件系统不支持 / 旧副本已被删）→ 落到下面的真实复制。
                    //    无论如何都**不留下"清单里有、快照里没有"的条目**。
                    long size = 0;
                    string hSrc = "";
                    bool reused = false;
                    if (reuseEntries is not null && reuseFilesDir is not null
                        && reuseEntries.TryGetValue(rel, out FileEntry? prev) && prev is not null)
                    {
                        var srcInfo = new FileInfo(src);
                        if (srcInfo.Exists
                            && UnchangedFileReuse.IsReusable(prev, item.SourcePath, srcInfo.Length, srcInfo.LastWriteTime))
                        {
                            if (FileLinker.TryCreateHardLink(dst, Path.Combine(reuseFilesDir, rel)))
                            {
                                // 复用：硬链接与上一份共享同一份数据，故清单沿用它记录的哈希——
                                // 这正是"未变"的含义（该文件本次**没有**被读取）。
                                size = prev.Size;
                                hSrc = prev.Sha256;
                                reused = true;
                            }
                            else
                            {
                                Interlocked.Increment(ref linkFallbackCount);
                            }
                        }
                    }

                    if (!reused)
                    {
                        // 流式复制时已对写入的数据计算源哈希；写入字节即源字节，
                        // 故不再整文件重读目标做二次校验（去掉双倍 I/O）。记录该哈希作为文件指纹。
                        (size, hSrc) = Sha256Hasher.CopyAndHash(src, dst);
                    }

                    int currentDone;
                    lock (lockObj)
                    {
                        entries.Add(new FileEntry
                        {
                            // manifest 记活动路径（VSS 场景 src 是设备路径，映射回原卷路径供「恢复到原位置」使用）
                            SourcePath = MapToLive(src, shadowMap),
                            RelativePath = rel,
                            Size = size,
                            // 🔴 刻意从 src（真实复制源）取 mtime，**不要**改成 item.SourcePath。
                            //    VSS 卷影是卷的时点快照，文件元数据（含 mtime）与快照时刻一致——
                            //    非 VSS 场景 shadowMap 为空，src 本就等于原路径，两者无差别。
                            //    差别只在「快照后又改了文件」这一窗口：此时我们复制的是快照字节，
                            //    取 src 的 mtime 才与内容自洽；改取活动路径会把更新后的 mtime
                            //    安到旧内容上，恢复时 File.SetLastWriteTime 写出错误时间。
                            //    （2026-09-06 审查报告 S-8 建议改用活动路径，经核验为误报，特此留注。）
                            Mtime = File.GetLastWriteTime(src).ToString("o"),
                            Sha256 = hSrc,
                            BackupTime = DateTime.Now.ToString("o"),
                        });
                        done++;
                        currentDone = done;
                        if (reused)
                        {
                            reusedCount++;
                        }
                    }
                    // 回调移出锁外：UiProgressReporter 经 Dispatcher 封送，避免锁内跨线程
                    reporter?.OnProgress(currentDone, total, "备份中");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        failures.Add($"{item.RelativePath}：{ex.Message}");
                    }
                }
                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            // 取消：等待在途任务完成（Parallel.ForEachAsync 已处理）
            throw;
        }

        return new CopyOutcome(entries, failures, reusedCount, linkFallbackCount);
    }

    /// <summary>VSS 设备路径 → 活动路径反查（manifest 语义）；非设备路径原样返回。</summary>
    // internal：对测试程序集开放直测（Core 已有 InternalsVisibleTo），覆盖混合分隔符场景
    internal static string MapToLive(string shadowPath, IReadOnlyDictionary<string, string> shadowMap)
    {
        // 2026-09-08（审查 A-1）：两端都做规范化——Windows 下 / 与 \ 等价，
        // 未规范化的 StartsWith 在混合分隔符场景会漏匹配，导致 manifest 记错活动路径。
        string normalized = NormalizePath(shadowPath);
        foreach (KeyValuePair<string, string> kv in shadowMap)
        {
            string prefix = NormalizePath(kv.Value);
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string tail = normalized[prefix.Length..].TrimStart('\\');
                return tail.Length == 0 ? kv.Key : kv.Key + "\\" + tail;
            }
        }

        return shadowPath;
    }

    /// <summary>把活动源路径映射为 VSS 快照设备路径（无映射 = 原样返回）。设备路径无尾分隔符，需补一个。</summary>
    // internal：对测试程序集开放直测（Core 已有 InternalsVisibleTo），覆盖混合分隔符场景
    internal static string MapToShadow(string livePath, IReadOnlyDictionary<string, string> shadowMap)
    {
        if (shadowMap.Count == 0)
        {
            return livePath;
        }

        string full;
        try
        { full = NormalizePath(livePath); }
        catch
        { return livePath; }

        foreach (KeyValuePair<string, string> kv in shadowMap)
        {
            string prefix = NormalizePath(kv.Key);
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string tail = full[prefix.Length..].TrimStart('\\');
                return tail.Length == 0 ? kv.Value : kv.Value + "\\" + tail;
            }
        }

        return livePath;
    }

    /// <summary>路径规范化：转绝对路径、分隔符统一反斜杠、去尾分隔符（根路径保留）。</summary>
    private static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        full = full.Replace('/', '\\');
        return full.Length > 3 ? full.TrimEnd('\\') : full; // C:\ 这类根保留尾分隔符
    }

    /// <summary>
    /// 检查备份根目录是否与任一源路径存在包含关系（重叠）。
    /// 返回冲突描述字符串（如 "源 D:\Data 包含备份根 D:\Data\backup"）；
    /// 无冲突返回 null。路径以规范化后的绝对目录形式比较，追加尾部分隔符
    /// 以确保匹配目录边界（避免 "b" 误匹配 "backup_root"）。
    /// </summary>
    private string? CheckPathOverlap(IReadOnlyList<string> sources, string backupRoot)
    {
        string normRoot;
        try
        { normRoot = PathUtil.NormalizeDirectory(backupRoot) + Path.DirectorySeparatorChar; }
        catch (Exception ex)
        {
            // 备份根无法规范化 = 无法判定它是否落在某个源的目录树内。
            // 过去这里 catch 后 return null（放行），等于恰好在"自我备份风险未知"时
            // 关闭了这道防护；改为阻断并说明原因。
            _logger.Error($"备份根路径无法规范化，重叠检查无法进行：{backupRoot}（{ex.Message}）");
            return $"备份根 {backupRoot} 无法规范化（{ex.Message}）";
        }

        foreach (string src in sources)
        {
            string normSrc;
            try
            { normSrc = PathUtil.NormalizeDirectory(src) + Path.DirectorySeparatorChar; }
            catch (Exception ex)
            {
                // 单个源无法规范化：跳过它，但必须留痕（其余源仍参与检查）
                _logger.Warn($"源路径无法规范化，已跳过其重叠检查：{src}（{ex.Message}）");
                continue;
            }

            // 源路径是文件时，取其所在目录做比较（文件不可能包含目录，但目录可能包含该文件）
            if (File.Exists(src))
            {
                try
                { normSrc = PathUtil.NormalizeDirectory(Path.GetDirectoryName(src)!) + Path.DirectorySeparatorChar; }
                catch { continue; }
            }

            // 源包含备份根（备份根在源目录树内）→ 扫描时会扫到备份产物
            if (normSrc.Length <= normRoot.Length &&
                normRoot.StartsWith(normSrc, StringComparison.OrdinalIgnoreCase))
                return $"源 {src} 包含备份根 {backupRoot}";

            // 备份根包含源（源在备份根目录树内）→ 同样不安全
            if (normRoot.Length <= normSrc.Length &&
                normSrc.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                return $"备份根 {backupRoot} 包含源 {src}";
        }
        return null;
    }

    /// <summary>清理不完整的快照目录（失败/取消时），避免孤儿目录累积。</summary>
    private void CleanupSnapshotDir(string? snapDir)
    {
        if (string.IsNullOrEmpty(snapDir))
            return;
        try
        {
            if (Directory.Exists(snapDir))
                Directory.Delete(snapDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"清理不完整快照目录失败：{snapDir}（{ex.Message}）");
        }
    }
}
