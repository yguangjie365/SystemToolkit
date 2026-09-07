using System.Collections.Concurrent;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>备份引擎：扫描、并发复制+SHA256、磁盘检查、快照写入、上限清理、取消。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BackupService : IBackupService
{
    private readonly BackupConfigService _config;
    private readonly int? _maxWorkersOverride;
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
        try
        {
            return await BackupRuleCoreAsync(rule, reporter, ct, vssLeases).ConfigureAwait(false);
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
            long required = (long)(totalSize * 1.2);
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
            reporter?.OnPhase("正在备份文件（复制 + SHA-256 校验）…");
            (List<FileEntry>? entries, List<string>? failures) = await CopyAndVerifyAsync(
                scan.Files, filesDir, reporter, maxWorkers, shadowMap, ct);
            filesCopied = true;

            // 部分成功模式：有失败文件时仍写入快照，标记为 failed
            bool isPartial = failures.Count > 0;
            string checksumStatus = isPartial ? ChecksumStatuses.Failed : ChecksumStatuses.Passed;

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
                ChecksumStatus = checksumStatus,
                Files = entries,
                EmptyDirs = scan.EmptyDirs,
            };
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
                    Failures = [ex.Message],
                };
            }

            // 上限清理：规则级 MaxSnapshots 钳制到合理范围，防止被设为极大值导致无限累积
            int rawLimit = rule.MaxSnapshots > 0 ? rule.MaxSnapshots : _config.Settings.MaxSnapshots;
            int limit = Math.Clamp(rawLimit, 1, BackupRule.MaxSnapshotsCap);
            List<string> removed = snapMgr.EnforceLimit(limit);
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
                    Message = $"备份部分成功：成功 {entries.Count} 个，失败 {failures.Count} 个",
                    Failures = failures,
                };
            }

            log?.Invoke($"【备份完成】规则「{rule.RuleName}」备份成功：{entries.Count} 个文件");
            return new BackupResult
            {
                RuleId = rule.RuleId,
                RuleName = rule.RuleName,
                Success = true,
                SnapshotDir = snapDir,
                FileCount = entries.Count,
                TotalSize = totalSize,
                ChecksumStatus = ChecksumStatuses.Passed,
                Message = $"备份成功：{entries.Count} 个文件",
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

    /// <summary>并发复制文件并边算 SHA-256。返回 (manifest 条目, 失败列表)。</summary>
    private async Task<(List<FileEntry> Entries, List<string> Failures)> CopyAndVerifyAsync(
        List<ScannedFile> files,
        string filesDir,
        IProgressReporter? reporter,
        int maxWorkers,
        IReadOnlyDictionary<string, string> shadowMap,
        CancellationToken ct)
    {
        int total = files.Count;
        int done = 0;
        var failures = new List<string>();
        var entries = new List<FileEntry>();
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

                    // 流式复制时已对写入的数据计算源哈希；写入字节即源字节，
                    // 故不再整文件重读目标做二次校验（去掉双倍 I/O）。记录该哈希作为文件指纹。
                    (long size, string? hSrc) = Sha256Hasher.CopyAndHash(src, dst);

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

        return (entries, failures);
    }

    /// <summary>VSS 设备路径 → 活动路径反查（manifest 语义）；非设备路径原样返回。</summary>
    private static string MapToLive(string shadowPath, IReadOnlyDictionary<string, string> shadowMap)
    {
        foreach (KeyValuePair<string, string> kv in shadowMap)
        {
            string prefix = kv.Value + "\\";
            if (shadowPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Key + shadowPath[prefix.Length..];
            }
        }

        return shadowPath;
    }

    /// <summary>把活动源路径映射为 VSS 快照设备路径（无映射 = 原样返回）。设备路径无尾分隔符，需补一个。</summary>
    private static string MapToShadow(string livePath, IReadOnlyDictionary<string, string> shadowMap)
    {
        if (shadowMap.Count == 0)
        {
            return livePath;
        }

        string full;
        try
        { full = Path.GetFullPath(livePath); }
        catch
        { return livePath; }

        foreach (KeyValuePair<string, string> kv in shadowMap)
        {
            if (full.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value + "\\" + full[kv.Key.Length..];
            }
        }

        return livePath;
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
