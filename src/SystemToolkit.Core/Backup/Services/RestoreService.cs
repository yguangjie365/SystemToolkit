using System.Collections.Concurrent;
using System.Globalization;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>恢复引擎：冲突策略、rename 唯一名、原子替换、恢复校验、原路径/指定路径。</summary>
public sealed class RestoreService : IRestoreService, IRestorePreviewProvider
{
    private readonly BackupConfigService? _config;
    private readonly int? _maxWorkersOverride;
    private readonly ILogger _logger;

    /// <summary>应用自身受保护目录（配置目录等），恢复操作拒绝写入，防止被篡改快照破坏。</summary>
    private readonly IReadOnlyList<string> _protectedRoots;

    /// <summary>
    /// 磁盘空间探测注入点（测试用 internal seam）：真实探测依赖驱动器状态，
    /// 无法稳定构造 Unknown 结果；实例级注入避免静态全局在并行测试间互相污染。
    /// </summary>
    internal Func<string, long, DiskSpaceCheck> SpaceProbe { get; set; } = (path, needed) => DiskSpaceUtil.Check(path, needed);

    /// <summary>创建恢复引擎；<paramref name="config"/> 提供配置目录作为受保护根（拒绝写入），可缺省供测试。</summary>
    public RestoreService(BackupConfigService? config = null, int? maxWorkers = null, ILogger? logger = null)
    {
        _config = config;
        _maxWorkersOverride = maxWorkers;
        _logger = logger ?? NullLogger.Instance;
        _protectedRoots = config != null
            ? new[] { config.ConfigDir }
            : Array.Empty<string>();
    }

    /// <summary>
    /// 恢复一份快照：清单安全校验 → 磁盘空间预检 → 冲突策略裁决 → 并发写入 → 恢复后校验 → 汇总报告。
    /// </summary>
    /// <param name="info">要恢复的快照元数据（含文件清单与数据目录路径）。</param>
    /// <param name="targetRoot">指定恢复目标根目录；null 表示恢复到原始路径。</param>
    /// <param name="policy">同名文件冲突处理策略。</param>
    /// <param name="userChoice">policy 为 Ask 时的逐条裁决回调：入参为冲突相对路径列表，返回用户选择的策略。</param>
    /// <param name="reporter">可选的进度/日志上报器。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="trustedRoots">可信源路径白名单（优先于 manifest 自带来源，防单点篡改）；null 时回退 manifest 白名单（弱信任，留痕）。</param>
    /// <returns>恢复结果报告（计数汇总与失败/跳过明细）。</returns>
    public async Task<RestoreReport> RestoreSnapshotAsync(
        SnapshotInfo info,
        string? targetRoot,
        ConflictPolicy policy,
        Func<IReadOnlyList<string>, ConflictPolicy>? userChoice = null,
        IProgressReporter? reporter = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? trustedRoots = null)
    {
        Action<string>? log = reporter is null ? null : (Action<string>)reporter.OnLog;
        var report = new RestoreReport { SnapshotId = info.SnapshotId, RuleName = info.RuleName };
        // MaxWorkers 调用时读取，设置变更后下次恢复即生效
        int maxWorkers = _maxWorkersOverride ?? _config?.Settings.MaxWorkers ?? 4;

        try
        {
            ct.ThrowIfCancellationRequested();
            string filesDir = info.BackupPath;
            if (!Directory.Exists(filesDir))
                throw new IOException($"快照文件目录不存在：{filesDir}");

            report.Total = info.Files.Count;
            if (report.Total == 0)
            {
                report.Success = true;
                report.Message = "快照中没有可恢复的文件。";
                return report;
            }

            // 安全校验：拒绝不安全的相对路径
            var unsafePaths = info.Files.Where(f => !IsSafeRelativePath(f.RelativePath)).ToList();
            if (unsafePaths.Count > 0)
                throw new IOException("快照清单包含不安全路径，已中止恢复：" +
                    string.Join("；", unsafePaths.Take(10).Select(f => f.RelativePath)));

            // 磁盘空间预检（含 20% 余量），避免复制到一半空间耗尽。
            // 无法确认（网络路径/驱动器未就绪）时不阻断，但必须留痕并提示用户，
            // 不能像过去那样被静默当成"空间充足"。
            long totalSize = info.Files.Sum(f => f.Size);
            long needed = (long)(totalSize * 1.2);
            DiskSpaceCheck space = SpaceProbe(targetRoot ?? info.SourcePath, needed);
            if (space == DiskSpaceCheck.Unknown)
            {
                _logger.Warn($"无法确认恢复目标「{targetRoot ?? info.SourcePath}」的剩余空间（网络路径或驱动器未就绪），继续恢复但存在空间耗尽风险");
                log?.Invoke("⚠️ 无法确认目标磁盘剩余空间（网络路径或未就绪驱动器），将继续恢复");
            }
            else if (space == DiskSpaceCheck.Insufficient)
            {
                throw new IOException($"目标磁盘剩余空间不足：需要约 {FormatUtil.FormatSize(needed)}（含 20% 余量），请清理磁盘或改用其它恢复路径。");
            }

            // 安全边界：源路径范围白名单 + 应用受保护目录黑名单。
            // 白名单来源与写入目标来源分离（S2，REVIEW-2026-08-30）：manifest 的 SourcePaths
            // 与被恢复文件的 SourcePath 同文件同源，单独篡改 manifest 即可让「白名单校验」
            // 形同虚设。优先采用调用方传入的 trustedRoots（规则当前配置，rules.json——
            // 攻击者需同时篡改两处数据才能绕过）；无则回退 manifest 自带（弱信任，必须留痕）。
            // 旧版快照没有 SourcePaths → 白名单为空会让"源路径范围"这道校验被整体跳过
            // （见 RestoreDestination 的 `allowedRoots is { Count: > 0 }` 条件），
            // 回退用单一源路径作为白名单，尽量不丢失这层防护。
            IReadOnlyList<string> allowedRoots;
            if (trustedRoots is { Count: > 0 })
            {
                allowedRoots = trustedRoots;
            }
            else
            {
                List<string> manifestRoots = info.SourcePaths;
                if (manifestRoots.Count == 0 && !string.IsNullOrWhiteSpace(info.SourcePath))
                {
                    allowedRoots = new List<string> { info.SourcePath };
                    _logger.Warn($"快照「{info.SnapshotId}」未声明 SourcePaths（旧格式），已回退使用单一源路径作为恢复范围白名单：{info.SourcePath}");
                }
                else if (manifestRoots.Count == 0)
                {
                    allowedRoots = manifestRoots;
                    _logger.Warn($"快照「{info.SnapshotId}」未提供任何源路径，无法校验恢复目标范围，仅依赖系统关键路径与受保护目录两道防护");
                }
                else
                {
                    allowedRoots = manifestRoots;
                    _logger.Warn($"快照「{info.SnapshotId}」未获得规则配置的可信源路径，恢复范围白名单回退为 manifest 自带来源（弱信任）：{string.Join("；", manifestRoots)}");
                }
            }
            IReadOnlyList<string> protectedRoots = _protectedRoots;

            // 目标目录
            if (targetRoot is not null)
            {
                Directory.CreateDirectory(targetRoot);
            }

            // 冲突策略解析（探测在后台线程执行，避免万级文件串行 File.Exists 卡 UI）
            ConflictPolicy finalPolicy = await ResolvePolicyAsync(info, targetRoot, policy, userChoice, allowedRoots, protectedRoots, ct);
            log?.Invoke($"冲突处理策略：{PolicyName(finalPolicy)}");

            // rename 预留集合：每次恢复独立分配，不跨调用残留
            object renameLock = new object();
            var allocatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createdDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            int restored = 0;
            int skipped = 0;
            int failed = 0;
            int verifyFailed = 0;
            var failures = new List<string>();
            // 被跳过条目的原因明细（多源快照的空目录、不安全路径等），与 skipped 计数配套
            var skippedItems = new List<string>();
            int done = 0;
            int total = info.Files.Count;
            object lockObj = new object();

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(maxWorkers, total)),
                CancellationToken = ct,
            };

            try
            {
                await Parallel.ForEachAsync(info.Files, options, (f, token) =>
                {
                    try
                    {
                        (string? status, string? msg, string? _) = ProcessOne(f, filesDir, targetRoot, finalPolicy, token, renameLock, allocatedPaths, createdDirs, allowedRoots, protectedRoots, log);
                        int currentDone;
                        lock (lockObj)
                        {
                            done++;
                            currentDone = done;
                            switch (status)
                            {
                                case "restored":
                                    restored++;
                                    break;
                                case "skipped":
                                    skipped++;
                                    break;
                                case "verify_failed":
                                    verifyFailed++;
                                    failures.Add(msg);
                                    break;
                                default:
                                    failed++;
                                    failures.Add(msg);
                                    break;
                            }
                        }
                        // 回调移出锁外：UiProgressReporter 经 Dispatcher 封送，避免锁内跨线程
                        reporter?.OnProgress(currentDone, total, "恢复中");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lock (lockObj)
                        {
                            failed++;
                            failures.Add($"{f.RelativePath}：{ex.Message}");
                        }
                    }
                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                report.Canceled = true;
                report.Message = "恢复已取消";
                report.Failures.Add("恢复已取消");
                log?.Invoke("【取消】恢复已取消");
                return report;
            }

            // 恢复空目录
            foreach (string rel in info.EmptyDirs)
            {
                if (ct.IsCancellationRequested)
                    break;
                if (!IsSafeRelativePath(rel))
                {
                    // 不安全路径过去直接 continue，用户无从得知有目录被跳过
                    skipped++;
                    skippedItems.Add($"{rel}：相对路径不安全，已跳过");
                    continue;
                }
                string? d = RestoreDirDestination(rel, info, targetRoot, allowedRoots, protectedRoots);
                if (d is null)
                {
                    // 多源快照无法精确还原空目录归属（RestoreDirDestination 返回 null）：
                    // 过去静默丢弃、不计入任何计数，用户以为目录都恢复了
                    skipped++;
                    skippedItems.Add($"{rel}：多源快照无法精确还原该目录的位置，已跳过");
                    continue;
                }
                try
                { Directory.CreateDirectory(d); }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{rel}：无法创建目录（{ex.Message}）");
                }
            }

            report.Restored = restored;
            report.Skipped = skipped;
            report.Failed = failed;
            report.VerifyFailed = verifyFailed;
            report.Failures = failures;
            report.SkippedItems = skippedItems;
            report.Success = failed == 0 && verifyFailed == 0;
            report.Message = $"恢复完成：成功 {restored} 个，跳过 {skipped} 个，失败 {failed} 个，校验失败 {verifyFailed} 个";
            log?.Invoke($"【恢复】{report.Message}");
            return report;
        }
        catch (OperationCanceledException)
        {
            report.Canceled = true;
            report.Message = "恢复已取消";
            report.Failures.Add("恢复已取消");
            log?.Invoke("【取消】恢复已取消");
            return report;
        }
        catch (Exception ex)
        {
            report.Message = $"恢复失败：{ex.Message}";
            report.Failures.Add(ex.Message);
            log?.Invoke($"【失败】{ex.Message}");
            return report;
        }
    }

    // ------------------------------------------------------------------
    // 单文件处理
    // ------------------------------------------------------------------
    private (string Status, string Msg, string? Dst) ProcessOne(
        FileEntry f,
        string filesDir,
        string? targetRoot,
        ConflictPolicy policy,
        CancellationToken ct,
        object renameLock,
        HashSet<string> allocatedPaths,
        ConcurrentDictionary<string, byte> createdDirs,
        IReadOnlyList<string>? allowedRoots,
        IReadOnlyList<string>? protectedRoots,
        Action<string>? log)
    {
        if (ct.IsCancellationRequested)
            return ("skipped", $"{f.RelativePath}：任务已取消", null);

        string src = FindBackupFile(filesDir, f.RelativePath);
        string dst = RestoreDestination(f, targetRoot, allowedRoots, protectedRoots);
        try
        {
            if (File.Exists(dst))
            {
                switch (policy)
                {
                    case ConflictPolicy.Skip:
                        return ("skipped", $"{f.RelativePath}：目标已存在，按策略跳过", null);
                    case ConflictPolicy.Rename:
                        lock (renameLock)
                        {
                            dst = UniquePath(dst, allocatedPaths);
                            allocatedPaths.Add(dst);
                        }
                        break;
                        // overwrite：写临时文件再原子替换（ReplaceWithRetry 内含属性清理/重试/回退）
                }
            }

            string dir = Path.GetDirectoryName(dst)!;
            if (createdDirs.TryAdd(dir, 0))
            {
                try
                { Directory.CreateDirectory(dir); }
                catch { createdDirs.TryRemove(dir, out _); throw; } // 回滚，避免同目录后续文件连锁失败
            }
            string tmp = Path.Combine(Path.GetDirectoryName(dst)!,
                $".{Path.GetFileName(dst)}.{Path.GetRandomFileName()}.fbktmp");
            try
            {
                // 复制到临时文件
                using (var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var d = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    s.CopyTo(d);
                }
                // 校验临时文件
                if (f.HasChecksum)
                {
                    string actual = Sha256Hasher.HashFile(tmp);
                    if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                        return ("verify_failed", $"{f.RelativePath}：恢复后 SHA-256 校验失败", dst);
                }

                // 无校验快照覆盖前备份原文件：校验缺失时无法确认临时文件完整性，
                // 覆盖会销毁用户当前文件。先复制一份 .fbkorig 兜底，成功后删除、失败后保留。
                string? origBackup = null;
                if (!f.HasChecksum && File.Exists(dst))
                {
                    origBackup = dst + ".fbkorig";
                    try
                    { File.Copy(dst, origBackup, overwrite: true); }
                    catch (Exception ex) { log?.Invoke($"警告：无法备份原文件（覆盖将直接替换）：{ex.Message}"); origBackup = null; }
                }

                // 覆盖式替换：清除只读等阻止属性 + 重试 + 回退，最大化恢复成功率
                if (!ReplaceWithRetry(tmp, dst, ct, log))
                {
                    // 替换失败：恢复原文件备份（如有）
                    if (origBackup is not null && File.Exists(origBackup))
                    {
                        try
                        { File.Move(origBackup, dst, overwrite: true); }
                        catch { /* 尽力而为，原文件备份保留在 .fbkorig */ }
                    }
                    return ("failed", $"{f.RelativePath}：覆盖写入失败（目标被占用或无权限）", null);
                }

                // 替换成功：清理原文件备份
                if (origBackup is not null)
                {
                    try
                    { if (File.Exists(origBackup)) File.Delete(origBackup); }
                    catch { /* 清理失败不影响恢复结果，留 .fbkorig 残留 */ }
                }

                // 还原原始修改时间，提升恢复保真度（备份时已记录 Mtime）
                if (!string.IsNullOrEmpty(f.Mtime) &&
                    DateTime.TryParse(f.Mtime, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime mtime))
                {
                    try
                    { File.SetLastWriteTime(dst, mtime); }
                    catch { /* 忽略时间戳设置失败，不影响文件内容恢复 */ }
                }

                return ("restored", $"{f.RelativePath} → {dst}", dst);
            }
            finally
            {
                try
                { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { /* 忽略清理失败 */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ("failed", $"{f.RelativePath}：{ex.Message}", null);
        }
    }

    // ------------------------------------------------------------------
    // 覆盖式替换：清除阻止属性 + 重试 + 回退，最大化恢复成功率
    // ------------------------------------------------------------------
    private const int MaxReplaceRetries = 3;

    /// <summary>
    /// 将已校验的临时文件覆盖替换到目标。覆盖失败最常见的两类原因：
    ///   1) 目标只读/系统/隐藏 → Windows MOVEFILE_REPLACE_EXISTING 返回 ACCESS_DENIED；
    ///   2) 目标被其它进程占用（杀软、索引器、用户打开）→ IOException 共享冲突。
    /// 策略：先清除只读等阻止属性，再带指数退避重试；仍失败则改名挪开旧文件后顶替
    /// （某些安全软件单独拦截覆盖式 rename 但放行普通 rename），并把原文件尽力还原以免丢数据。
    /// </summary>
    // 由 private 改 internal：属性清理/重试/改名兜底的分支组合无法仅经公开 Restore API 稳定触达，
    // 对测试程序集开放直测（Core 已有 InternalsVisibleTo）
    internal static bool ReplaceWithRetry(string tmp, string dst, CancellationToken ct, Action<string>? log)
    {
        TryClearBlockingAttributes(dst);

        for (int attempt = 0; attempt <= MaxReplaceRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Move(tmp, dst, overwrite: true);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            // 不再用 when(attempt < MaxReplaceRetries) 过滤：最后一次尝试（attempt==MaxReplaceRetries）
            // 抛出的 IOException/UnauthorizedAccessException 若无 catch 匹配会直接传播出方法，
            // 导致循环后的 .fbkold 改名兜底块永远不可达（死代码）。改为在 catch 体内区分重试与耗尽。
            catch (IOException ex)
            {
                if (attempt < MaxReplaceRetries)
                {
                    // 目标被占用：清一次属性后退避重试
                    TryClearBlockingAttributes(dst);
                    Thread.Sleep(BackoffMs(attempt));
                }
                else
                {
                    // 重试耗尽：不再退避等待，落到循环后的改名兜底流程
                    log?.Invoke($"覆盖重试 {MaxReplaceRetries + 1} 次仍失败（{ex.Message}），改用改名兜底");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                if (attempt < MaxReplaceRetries)
                {
                    TryClearBlockingAttributes(dst);
                    Thread.Sleep(BackoffMs(attempt));
                }
                else
                {
                    log?.Invoke($"覆盖重试 {MaxReplaceRetries + 1} 次仍失败（{ex.Message}），改用改名兜底");
                }
            }
        }

        // 最后手段：现有目标改名到一旁 → 新内容顶上 → 删除旧副本。
        // 注意：目标可能是同名目录而非文件，File.* 系列 API 对目录恒 false / 抛异常，
        // 必须区分处理，否则改名失败并残留 .fbkold。
        string old = dst + ".fbkold";
        bool targetIsDir = Directory.Exists(dst);
        try
        {
            if (targetIsDir)
            {
                if (Directory.Exists(old))
                    Directory.Delete(old, recursive: true);
                Directory.Move(dst, old);
                File.Move(tmp, dst, overwrite: true);
                Directory.Delete(old, recursive: true);
                return true;
            }

            if (File.Exists(old))
                File.Delete(old);
            File.Move(dst, old, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
            File.Delete(old);
            return true;
        }
        catch (Exception ex)
        {
            // 兜底还原：只在"原件确实已被挪走"时才回搬。
            // 原实现无条件 `File.Delete(dst)`——若 dst 仍是原文件（改名这步就失败了），
            // 会把用户的原文件直接删掉且无法还原，这是真实的数据丢失路径。
            try
            {
                if (targetIsDir)
                {
                    // dst 变成文件 = 新内容曾顶替成功：删掉它，把原目录搬回
                    if (File.Exists(dst))
                    {
                        File.Delete(dst);
                        if (Directory.Exists(old))
                            Directory.Move(old, dst);
                    }
                    // dst 仍是目录 = 原件未被挪动：保持原样，绝不删除
                }
                else if (File.Exists(old))
                {
                    // dst 已被挪到 old：移除可能顶替上来的新文件，还原原件
                    if (File.Exists(dst))
                        File.Delete(dst);
                    File.Move(old, dst, overwrite: true);
                }
                // old 不存在 = 原件未被挪动：保持原样，绝不删除 dst
            }
            catch { /* 尽力而为 */ }

            TryDeleteStale(old, targetIsDir, log);
            log?.Invoke($"覆盖替换失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 尽力清理兜底改名产生的 .fbkold 残留（文件或目录）；清理失败时明确提示用户手动删除，
    /// 避免残留物被静默留在用户目录下。
    /// </summary>
    private static void TryDeleteStale(string path, bool isDir, Action<string>? log)
    {
        try
        {
            if (isDir ? Directory.Exists(path) : File.Exists(path))
            {
                if (isDir)
                    Directory.Delete(path, recursive: true);
                else
                    File.Delete(path);
                log?.Invoke($"已清理兜底残留：{path}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"兜底残留清理失败，请手动删除：{path}（{ex.Message}）");
        }
    }

    /// <summary>清除会阻止 Windows 覆盖替换的只读/系统/隐藏属性。</summary>
    internal static void TryClearBlockingAttributes(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            FileAttributes attrs = File.GetAttributes(path);
            FileAttributes blocking = attrs & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);
            if (blocking != 0)
                File.SetAttributes(path, attrs & ~blocking);
        }
        catch { /* 拿不到属性就交给后续 Move 报错 */ }
    }

    private static int BackoffMs(int attempt) => 150 * (int)Math.Pow(2, attempt); // 150,300,600

    // ------------------------------------------------------------------
    // 路径计算与安全校验
    // ------------------------------------------------------------------
    private static bool IsSafeRelativePath(string rel) => PathUtil.IsSafeRelativePath(rel);

    /// <summary>
    /// 判断路径是否指向系统关键目录（Windows/Program Files/系统盘根）。
    /// 用于恢复原路径时拦截被篡改 manifest 指向系统目录覆盖文件。
    /// </summary>
    private static bool IsSystemCriticalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        try
        {
            string full = PathUtil.NormalizeDirectory(path) + Path.DirectorySeparatorChar;
            // 系统盘根（如 C:\）
            string? root = Path.GetPathRoot(full);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
            // Windows 系统目录
            string sysDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            if (full.StartsWith(
                sysDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                return true;
            // Program Files
            foreach (string? pf in new[]
            {
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (!string.IsNullOrEmpty(pf) &&
                    full.StartsWith(
                        pf.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return true; // 路径解析失败视为危险
        }
    }

    private static string FindBackupFile(string filesDir, string rel)
    {
        if (!IsSafeRelativePath(rel))
            throw new FileNotFoundException($"快照中包含不安全的相对路径：{rel}");
        string cand = Path.Combine(filesDir, rel);
        if (File.Exists(cand))
            return cand;
        throw new FileNotFoundException($"快照中未找到备份文件：{rel}");
    }

    private static string RestoreDestination(FileEntry f, string? targetRoot, IReadOnlyList<string>? allowedRoots, IReadOnlyList<string>? protectedRoots)
    {
        if (!IsSafeRelativePath(f.RelativePath))
            throw new IOException($"快照中包含不安全的相对路径：{f.RelativePath}");
        if (targetRoot is not null)
            return Path.Combine(targetRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        // 恢复到原路径：三重安全校验（系统关键路径 / 应用受保护目录 / 快照源路径范围）
        string dst = f.SourcePath;
        if (IsSystemCriticalPath(dst))
            throw new IOException($"拒绝向系统关键路径恢复文件：{dst}（疑似快照数据被篡改）");
        if (protectedRoots is { Count: > 0 } && PathUtil.IsUnderAny(dst, protectedRoots))
            throw new IOException($"拒绝向应用受保护目录恢复文件：{dst}（疑似快照数据被篡改）");
        if (allowedRoots is { Count: > 0 } && !PathUtil.IsUnderAny(dst, allowedRoots))
            throw new IOException($"拒绝恢复：{dst} 不在允许的源路径范围内（规则配置或快照声明，疑似快照数据被篡改）");
        return dst;
    }

    private static string? RestoreDirDestination(string rel, SnapshotInfo info, string? targetRoot, IReadOnlyList<string>? allowedRoots, IReadOnlyList<string>? protectedRoots)
    {
        if (targetRoot is not null)
            return Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        if (info.SourcePaths.Count > 1)
            return null; // 多源无法精确还原目录
        string basePath = info.SourcePath;
        string d = File.Exists(basePath)
            ? Path.Combine(Path.GetDirectoryName(basePath)!, rel.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(basePath, rel.Replace('/', Path.DirectorySeparatorChar));
        // 与文件恢复一致的三重安全校验：越界目录直接跳过（返回 null）
        if (IsSystemCriticalPath(d))
            return null;
        if (protectedRoots is { Count: > 0 } && PathUtil.IsUnderAny(d, protectedRoots))
            return null;
        if (allowedRoots is { Count: > 0 } && !PathUtil.IsUnderAny(d, allowedRoots))
            return null;
        return d;
    }

    private static string UniquePath(string dst, HashSet<string> allocated)
    {
        string dir = Path.GetDirectoryName(dst)!;
        string stem = Path.GetFileNameWithoutExtension(dst);
        string ext = Path.GetExtension(dst);
        int i = 1;
        while (true)
        {
            string cand = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(cand) && !allocated.Contains(cand))
                return cand;
            i++;
        }
    }

    // ------------------------------------------------------------------
    // 冲突策略
    // ------------------------------------------------------------------
    // 由 private 改 internal：冲突策略解析为纯决策逻辑（探测目标存在性 + 用户回调），
    // 对测试程序集开放直测（Core 已有 InternalsVisibleTo）
    internal async Task<ConflictPolicy> ResolvePolicyAsync(
        SnapshotInfo info, string? targetRoot, ConflictPolicy policy,
        Func<IReadOnlyList<string>, ConflictPolicy>? userChoice,
        IReadOnlyList<string>? allowedRoots, IReadOnlyList<string>? protectedRoots,
        CancellationToken ct)
    {
        if (policy != ConflictPolicy.Ask)
            return policy;

        // 冲突探测放线程池：万级文件串行 File.Exists 不应阻塞调用线程（UI 冻结源）
        // 探测时的安全校验异常（越界/受保护路径）视为无冲突，交由正式恢复阶段报错
        List<string> conflicts = await Task.Run(() => info.Files
            .Where(f =>
            {
                try
                { return File.Exists(RestoreDestination(f, targetRoot, allowedRoots, protectedRoots)); }
                catch { return false; }
            })
            .Select(f => f.RelativePath)
            .ToList(), ct);

        if (conflicts.Count == 0)
            return ConflictPolicy.Overwrite; // 无冲突直接写入
        if (userChoice is null)
            return ConflictPolicy.Skip; // 无法询问安全降级
        return userChoice(conflicts); // 回到调用线程（UI）执行用户询问
    }

    private static string PolicyName(ConflictPolicy p) => p switch
    {
        ConflictPolicy.Ask => "询问用户",
        ConflictPolicy.Overwrite => "覆盖",
        ConflictPolicy.Rename => "重命名保留两者",
        ConflictPolicy.Skip => "跳过",
        _ => p.ToString(),
    };

    /// <inheritdoc cref="IRestorePreviewProvider.PreviewConflictsAsync"/>
    public Task<RestorePreviewReport> PreviewConflictsAsync(
        SnapshotInfo info, string? targetRoot,
        IReadOnlyList<string>? trustedRoots = null, CancellationToken ct = default)
    {
        // 白名单解析与真实恢复同源（trustedRoots 优先，回退 manifest，语义一致）
        IReadOnlyList<string> allowedRoots;
        if (trustedRoots is { Count: > 0 })
        {
            allowedRoots = trustedRoots;
        }
        else if (info.SourcePaths.Count > 0)
        {
            allowedRoots = info.SourcePaths;
        }
        else if (!string.IsNullOrWhiteSpace(info.SourcePath))
        {
            allowedRoots = new List<string> { info.SourcePath };
        }
        else
        {
            allowedRoots = Array.Empty<string>();
        }

        IReadOnlyList<string> protectedRoots = _protectedRoots;
        var entries = new ConcurrentBag<RestorePreviewEntry>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(_maxWorkersOverride ?? _config?.Settings.MaxWorkers ?? 4, 1, 8),
            CancellationToken = ct,
        };

        Parallel.ForEach(info.Files, options, f =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string dst = RestoreDestination(f, targetRoot, allowedRoots, protectedRoots);
                entries.Add(new RestorePreviewEntry(f.RelativePath, dst, File.Exists(dst), false, null));
            }
            catch (IOException ex)
            {
                // 不安全相对路径 / 系统关键路径 / 越界：真实恢复会拒绝，预演按 Blocked 呈现
                entries.Add(new RestorePreviewEntry(f.RelativePath, "-", false, true, ex.Message));
            }
        });

        var list = entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToList();
        var report = new RestorePreviewReport
        {
            Total = info.Files.Count,
            ExistsCount = list.Count(e => e.ExistsInTarget),
            BlockedCount = list.Count(e => e.Blocked),
            Entries = list,
        };
        return Task.FromResult(report);
    }
}
