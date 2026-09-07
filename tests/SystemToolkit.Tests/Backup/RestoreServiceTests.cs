using System.Globalization;
using System.Text;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Tests;

/// <summary>恢复引擎测试：冲突策略、恢复校验、原路径恢复、取消（自旧工程移植）。</summary>
public class RestoreServiceTests
{
    /// <summary>测试用 logger：捕获 Warn 文本，供断言"无法确认空间"等告警确实产生。</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
        public void Info(string message) { }
    }

    /// <summary>
    /// 回归守卫：旧版快照没有 SourcePaths 时，"源路径范围"这道白名单校验会被整体跳过
    /// （RestoreDestination 中的 <c>allowedRoots is { Count: &gt; 0 }</c> 条件）。
    /// 现回退使用单一 SourcePath 作为白名单，越界目标必须仍被拒绝。
    /// </summary>
    [Fact]
    public async Task Restore_LegacySnapshotWithoutSourcePaths_OutOfRangeTargetStillRejected()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_legacy");
        try
        {
            (string _, SnapshotInfo? info, string _) = await CreateBackup(cfg);

            // 模拟旧格式快照：SourcePaths 为空，仅保留单一 SourcePath
            info.SourcePaths.Clear();
            Assert.False(string.IsNullOrWhiteSpace(info.SourcePath));

            // 篡改 manifest 中的目标路径，令其落在允许的源范围之外
            string outside = Path.Combine(temp, "outside_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            string hijacked = Path.Combine(outside, "hijacked.txt");
            info.Files[0].SourcePath = hijacked;

            var svc = new RestoreService();
            // 单个文件的校验异常由恢复循环内部捕获并计入 report（不向外抛）
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Overwrite);

            Assert.Equal(1, report.Failed);
            Assert.False(report.Success);
            // 失败原因必须是"不在允许的源路径范围内"，证明白名单回退确实生效
            Assert.Contains(report.Failures, f => f.Contains("不在允许的源路径范围内"));
            // 越界目标不应被写入
            Assert.False(File.Exists(hijacked));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // S2（REVIEW-2026-08-30）：白名单与写入目标来源分离
    // ==================================================================

    /// <summary>
    /// S2 回归守卫：manifest 的 SourcePaths（白名单）与 Files[].SourcePath（写入目标）
    /// 同在快照元数据一个文件里，修复前攻击者篡改 manifest 即可同时放宽白名单并改写
    /// 目标，"源路径范围校验"形同虚设。现在调用方传入的 trustedRoots（规则当前配置）
    /// 优先于 manifest 自带来源——篡改 manifest 不能扩大写入范围。
    /// </summary>
    [Fact]
    public async Task Restore_TrustedRootsTakePrecedence_TamperedManifestCannotExpandScope()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_trusted");
        try
        {
            (string _, SnapshotInfo? info, string? original) = await CreateBackup(cfg);

            // 模拟 S2 攻击：篡改 manifest——把越界目录塞进 SourcePaths，并把写入目标改到越界位置
            string outside = Path.Combine(temp, "outside_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            string hijacked = Path.Combine(outside, "hijacked.txt");
            info.Files[0].SourcePath = hijacked;
            info.SourcePaths.Add(Path.Combine(temp, "attacker_dir"));

            // 规则当前配置（rules.json）的可信白名单：不含越界目录
            var trusted = new List<string> { original };

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Overwrite,
                trustedRoots: trusted);

            Assert.Equal(1, report.Failed);
            Assert.False(report.Success);
            Assert.Contains(report.Failures, f => f.Contains("不在允许的源路径范围内"));
            Assert.False(File.Exists(hijacked), "越界目标不应被写入");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_TrustedRootsMatchingTarget_RestoresNormally()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_trusted_ok");
        try
        {
            (string _, SnapshotInfo? info, string? original) = await CreateBackup(cfg);
            // 篡改原文件，验证恢复确实写回
            File.WriteAllText(original, "已被篡改", Encoding.UTF8);

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Overwrite,
                trustedRoots: new List<string> { original });

            Assert.True(report.Success, report.Message);
            Assert.Equal("hello backup content", File.ReadAllText(original));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_WithoutTrustedRoots_FallsBackToManifestWhitelistAndLogs()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_trusted_fallback");
        try
        {
            (string _, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            Assert.True(info.SourcePaths.Count > 0, "前提：备份引擎会写入 manifest SourcePaths");
            var logger = new CapturingLogger();

            var svc = new RestoreService(logger: logger);
            // trustedRoots 缺省 → 回退 manifest 自带来源（弱信任），必须留痕
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Overwrite);

            Assert.True(report.Success, report.Message);
            Assert.Contains(logger.Warnings, w => w.Contains("弱信任"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static async Task<(string TempDir, SnapshotInfo Info, string SourceFile)> CreateBackup(
        BackupConfigService cfg, string content = "hello backup content")
    {
        string temp = Path.GetTempPath();
        string srcDir = Path.Combine(temp, "fb_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        string f1 = Path.Combine(srcDir, "hello.txt");
        File.WriteAllText(f1, content, Encoding.UTF8);

        var rule = new BackupRule { RuleName = "恢复测试", SourcePath = f1, SourceType = SourceTypes.File };
        var svc = new BackupService(cfg);
        BackupResult result = await svc.BackupRuleAsync(rule);
        Assert.True(result.Success, result.Message);

        var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
        SnapshotInfo? info = mgr.LatestSnapshot();
        Assert.NotNull(info);
        return (srcDir, info!, f1);
    }

    [Fact]
    public async Task Restore_ConflictSkip_KeepsExisting()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "hello.txt"), "已有文件", Encoding.UTF8);

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Skip);
            Assert.Equal(1, report.Skipped);
            Assert.Equal("已有文件", File.ReadAllText(Path.Combine(target, "hello.txt")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_ConflictOverwrite_Replaces()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string? original) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target2");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "hello.txt"), "旧内容", Encoding.UTF8);

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);
            Assert.Equal(1, report.Restored);
            Assert.Equal(File.ReadAllText(original), File.ReadAllText(Path.Combine(target, "hello.txt")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_OverReadonlyTarget_Succeeds()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string? original) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target_ro");
            Directory.CreateDirectory(target);
            string dstFile = Path.Combine(target, "hello.txt");
            File.WriteAllText(dstFile, "旧内容", Encoding.UTF8);
            File.SetAttributes(dstFile, FileAttributes.ReadOnly); // 模拟只读目标

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);
            Assert.Equal(1, report.Restored);
            Assert.Equal(File.ReadAllText(original), File.ReadAllText(dstFile));
            // 覆盖成功后应清掉只读，文件可正常读写
            Assert.False(File.GetAttributes(dstFile).HasFlag(FileAttributes.ReadOnly));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_PreservesOriginalMtime()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target_mtime");
            Directory.CreateDirectory(target);

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);
            Assert.True(report.Success, report.Message);
            string restored = Path.Combine(target, "hello.txt");
            // 还原后的修改时间应与备份时记录的一致（精确到秒；亚秒精度受文件系统粒度影响不强制）
            string storedSec = DateTime.Parse(info.Files[0].Mtime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToString("yyyy-MM-ddTHH:mm:ss");
            Assert.Equal(storedSec, File.GetLastWriteTime(restored).ToString("yyyy-MM-ddTHH:mm:ss"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_ConflictRename_CreatesUnique()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target3");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "hello.txt"), "原始", Encoding.UTF8);

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Rename);
            Assert.Equal(1, report.Restored);
            Assert.True(File.Exists(Path.Combine(target, "hello (1).txt")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_VerifyChecksum_Passes()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target4");
            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);
            Assert.True(report.Success, report.Message);
            Assert.Equal(0, report.VerifyFailed);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_ToOriginalPath_Restores()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string? original) = await CreateBackup(cfg, "将被篡改的内容");
            // 篡改原文件
            File.WriteAllText(original, "已被篡改", Encoding.UTF8);

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Overwrite);
            Assert.True(report.Success, report.Message);
            Assert.Equal("将被篡改的内容", File.ReadAllText(original));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_Canceled_NoPartialTmp()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target5");
            Directory.CreateDirectory(target);

            var svc = new RestoreService();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite, ct: cts.Token);
            Assert.True(report.Canceled);
            // 无 .fbktmp 残留
            Assert.Empty(Directory.GetFiles(target, "*.fbktmp"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_RejectsUnsafeRelativePath_PreventsTraversal()
    {
        (BackupConfigService _, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            // 构造含不安全相对路径的快照清单：路径穿越（../）应被拒绝，防止写到备份目录之外
            string filesDir = Path.Combine(temp, "backupfiles");
            Directory.CreateDirectory(filesDir);
            var info = new SnapshotInfo
            {
                BackupPath = filesDir,
                Files =
                [
                    new FileEntry { RelativePath = "../escape.txt" },
                ],
            };

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Skip);
            Assert.False(report.Success, "含不安全路径的快照不应恢复成功");
            Assert.Contains("不安全", report.Message);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("C:/windows/system32/evil.txt")]
    [InlineData("/abs/path/evil.txt")]
    public async Task Restore_RejectsVariousUnsafePaths(string unsafeRel)
    {
        (BackupConfigService _, string? temp) = TestHelpers.MakeConfig("fb_rest");
        try
        {
            string filesDir = Path.Combine(temp, "backupfiles");
            Directory.CreateDirectory(filesDir);
            var info = new SnapshotInfo
            {
                BackupPath = filesDir,
                Files = [new FileEntry { RelativePath = unsafeRel }],
            };

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Skip);
            Assert.False(report.Success);
            Assert.Contains("不安全", report.Message);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // 磁盘空间预检（修复 12）
    // ==================================================================
    [Fact]
    public async Task Restore_InsufficientDiskSpace_Aborts()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_space");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            // Size 置为极大值：needed = Size * 1.2 ≈ 2.7 EB，任何真实磁盘都不可能充足
            // （无需注入探测函数——Insufficient 可由真实 DiskSpaceUtil 稳定得出）
            info.Files[0].Size = long.MaxValue / 4;
            string target = Path.Combine(srcDir, "target_insufficient");

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);

            Assert.False(report.Success);
            Assert.Contains("剩余空间不足", report.Message);
            // 预检在创建目标目录之前中止：目标不应有任何写入
            Assert.False(Directory.Exists(target), "空间不足必须在写入前中止");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_UnknownDiskSpace_ContinuesAndLogsWarning()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_unknown");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target_unknown");

            // 真实驱动器状态无法稳定构造 Unknown（未就绪盘符会导致 CreateDirectory 先失败），
            // 故采用 RestoreService 的实例级探测注入点（SpaceProbe）而非静态全局，
            // 避免并行测试互相污染（与 spec 的"DiskSpaceUtil 注入重载"二选一，取后者替代）
            var logger = new CapturingLogger();
            var svc = new RestoreService(logger: logger)
            {
                SpaceProbe = (_, _) => DiskSpaceCheck.Unknown,
            };
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);

            // Unknown 不阻断：恢复应成功
            Assert.True(report.Success, report.Message);
            Assert.Equal(1, report.Restored);
            // 必须留痕（写日志告警），不能被静默当成"空间充足"
            Assert.Contains(logger.Warnings, w => w.Contains("无法确认"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // 无校验条目覆盖前备份 .fbkorig（修复 12）
    // ==================================================================
    [Fact]
    public async Task Restore_OverwriteWithoutChecksum_CleansFbkorigAfterSuccess()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_orig");
        try
        {
            (string? srcDir, SnapshotInfo? info, string? original) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target_orig_ok");
            Directory.CreateDirectory(target);
            string dst = Path.Combine(target, "hello.txt");
            File.WriteAllText(dst, "旧内容", Encoding.UTF8);

            // Sha256 置空 → HasChecksum=false → 覆盖前先复制 .fbkorig 兜底
            info.Files[0].Sha256 = null!;

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);

            Assert.True(report.Success, report.Message);
            Assert.Equal(File.ReadAllText(original), File.ReadAllText(dst));
            // 成功后 .fbkorig 必须清理干净，不残留
            Assert.Empty(Directory.GetFiles(target, "*.fbkorig"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Restore_OverwriteWithoutChecksum_KeepsFbkorigOnReplaceFailure()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_orig_fail");
        try
        {
            (string? srcDir, SnapshotInfo? info, string? original) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target_orig_fail");
            Directory.CreateDirectory(target);
            string dst = Path.Combine(target, "hello.txt");
            File.WriteAllText(dst, "旧内容", Encoding.UTF8);
            info.Files[0].Sha256 = null!;

            // 以 FileShare.Read 独占写语义锁定目标：读备份 .fbkorig 可行（允许读），
            // 但覆盖替换/改名（需写/删访问）必然失败 → 走"替换失败 → 尽力还原原文件"分支
            using (File.Open(dst, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var svc = new RestoreService();
                RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);

                Assert.Equal(1, report.Failed);
                Assert.False(report.Success);
                // 原文件未被破坏
                Assert.Equal("旧内容", File.ReadAllText(dst));
                // 还原失败时 .fbkorig 必须保留（数据兜底，用户可手动恢复）
                Assert.True(File.Exists(dst + ".fbkorig"), "替换失败且还原失败时 .fbkorig 应保留");
            }
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // 空目录 SkippedItems 明细（修复 12）
    // ==================================================================
    [Fact]
    public async Task Restore_SkippedItems_UnsafeEmptyDirRecorded()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_dirs");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            info.EmptyDirs = ["../escape", "sub"];
            string target = Path.Combine(srcDir, "target_dirs");

            var svc = new RestoreService();
            RestoreReport report = await svc.RestoreSnapshotAsync(info, target, ConflictPolicy.Overwrite);

            // 不安全空目录被跳过且计入 Skipped，明细与计数一致
            Assert.Equal(1, report.Skipped);
            Assert.Contains(report.SkippedItems, s => s.Contains("../escape") && s.Contains("不安全"));
            // 合法空目录正常还原
            Assert.True(Directory.Exists(Path.Combine(target, "sub")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // 冲突策略解析直测（修复 12，ResolvePolicyAsync 已改 internal）
    // ==================================================================
    [Fact]
    public async Task ResolvePolicy_Ask_ConflictDetectionAndDegradation()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_policy");
        try
        {
            (string? srcDir, SnapshotInfo? info, string _) = await CreateBackup(cfg);
            string target = Path.Combine(srcDir, "target_policy");
            Directory.CreateDirectory(target);

            var svc = new RestoreService();

            // 无冲突 + Ask → 直接 Overwrite（不打扰用户）
            ConflictPolicy noConflict = await svc.ResolvePolicyAsync(
                info, target, ConflictPolicy.Ask, null, info.SourcePaths, null, CancellationToken.None);
            Assert.Equal(ConflictPolicy.Overwrite, noConflict);

            // 目标已有同名文件 → 冲突；无 userChoice → 安全降级 Skip
            File.WriteAllText(Path.Combine(target, "hello.txt"), "已有", Encoding.UTF8);
            ConflictPolicy degraded = await svc.ResolvePolicyAsync(
                info, target, ConflictPolicy.Ask, null, info.SourcePaths, null, CancellationToken.None);
            Assert.Equal(ConflictPolicy.Skip, degraded);

            // 有 userChoice → 用户决策生效
            ConflictPolicy userDecided = await svc.ResolvePolicyAsync(
                info, target, ConflictPolicy.Ask,
                _ => ConflictPolicy.Rename, info.SourcePaths, null, CancellationToken.None);
            Assert.Equal(ConflictPolicy.Rename, userDecided);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // 受保护目录拒绝恢复（修复 12）
    // ==================================================================
    [Fact]
    public async Task Restore_TargetInsideProtectedDirectory_Rejected()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_rest_prot");
        try
        {
            // 源文件位于 ConfigDir 的子目录内：仍处于受保护根（ConfigDir）之下，
            // 但其所在目录不包含 backup_root（TestHelpers 把备份根设在 ConfigDir 下），
            // 避免建备份 fixture 时被「源/备份根重叠」前置检查拦截，让测试真正走到受保护目录拒绝分支
            string srcSub = Path.Combine(cfg.ConfigDir, "protected_src");
            Directory.CreateDirectory(srcSub);
            string srcFile = Path.Combine(srcSub, "protected.txt");
            File.WriteAllText(srcFile, "受保护内容", Encoding.UTF8);
            var rule = new BackupRule { RuleName = "受保护恢复", SourcePath = srcFile, SourceType = SourceTypes.File };
            BackupResult backup = await new BackupService(cfg).BackupRuleAsync(rule);
            Assert.True(backup.Success, backup.Message);
            SnapshotInfo? info = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot).LatestSnapshot();
            Assert.NotNull(info);

            // 恢复到原路径：目标落在受保护目录内，必须被拒绝（防篡改快照破坏应用配置）
            var svc = new RestoreService(cfg);
            RestoreReport report = await svc.RestoreSnapshotAsync(info, null, ConflictPolicy.Overwrite);

            Assert.Equal(1, report.Failed);
            Assert.False(report.Success);
            Assert.Contains(report.Failures, f => f.Contains("受保护目录"));
            // 原文件未被改动
            Assert.Equal("受保护内容", File.ReadAllText(srcFile));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // ReplaceWithRetry 直测（修复 13，已改 internal）
    // ==================================================================
    [Fact]
    public void ReplaceWithRetry_NormalReplacement_Succeeds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fb_rr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dst = Path.Combine(dir, "file.txt");
            string tmp = Path.Combine(dir, "file.tmp");
            File.WriteAllText(dst, "旧内容", Encoding.UTF8);
            File.WriteAllText(tmp, "新内容", Encoding.UTF8);

            bool ok = RestoreService.ReplaceWithRetry(tmp, dst, CancellationToken.None, null);

            Assert.True(ok);
            Assert.Equal("新内容", File.ReadAllText(dst));
            Assert.False(File.Exists(tmp), "替换成功后 tmp 应已被 Move 消费");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReplaceWithRetry_TargetIsSameNameDirectory_FallsBackToRename()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fb_rr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 目标路径是一个目录：覆盖式 Move 对目录恒失败（重现生产中"安全软件拦截覆盖
            // rename"之外可稳定构造的兜底路径），必须经 .fbkold 改名兜底完成替换
            string dst = Path.Combine(dir, "file.txt");
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, "inner.txt"), "目录内容", Encoding.UTF8);
            string tmp = Path.Combine(dir, "file.tmp");
            File.WriteAllText(tmp, "新内容", Encoding.UTF8);

            bool ok = RestoreService.ReplaceWithRetry(tmp, dst, CancellationToken.None, null);

            Assert.True(ok);
            Assert.True(File.Exists(dst), "兜底后 dst 应变成文件");
            Assert.Equal("新内容", File.ReadAllText(dst));
            Assert.False(Directory.Exists(dst + ".fbkold"), "兜底成功后 .fbkold 应清理");
            Assert.False(File.Exists(tmp));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReplaceWithRetry_MissingTmp_ReturnsFalse()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fb_rr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dst = Path.Combine(dir, "missing.txt");
            string tmp = Path.Combine(dir, "missing.tmp");

            bool ok = RestoreService.ReplaceWithRetry(tmp, dst, CancellationToken.None, null);

            // tmp 不存在：重试耗尽 + 兜底均失败，返回 false 且不产生任何目标文件
            Assert.False(ok);
            Assert.False(File.Exists(dst));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
