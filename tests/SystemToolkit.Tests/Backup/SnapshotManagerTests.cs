using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>快照管理测试（自旧工程移植，L15 英文命名）：目录布局与删除语义。</summary>
public class SnapshotManagerTests
{
    [Fact]
    public async Task DeleteAllSnapshots_RemovesRuleBaseDir_NotJustSnapshots()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_snap");
        try
        {
            // 备份一次，生成 Rule_xxx 目录树
            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");

            var rule = new BackupRule { RuleName = "清理测试", SourcePaths = [src], SourceType = SourceTypes.Folder };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);
            Assert.True(result.Success, result.Message);

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            Assert.True(Directory.Exists(mgr.RuleBase), "备份后应存在规则专属目录");
            Assert.True(mgr.AllSnapshotDirs().Count > 0);

            // 删除全部快照
            int count = mgr.DeleteAllSnapshots();
            Assert.True(count >= 1);

            // 规则专属目录（含 snapshots 空壳）应一并删除，而非残留
            Assert.False(Directory.Exists(mgr.RuleBase), "删除后规则专属目录应被完全移除");
            Assert.False(Directory.Exists(Path.Combine(mgr.RuleBase, "snapshots")), "不应残留 snapshots 空壳");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void CreateSnapshotDir_CreatesTimestampedDirWithFilesSubdir()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_snap");
        try
        {
            var rule = new BackupRule { RuleName = "dir", SourcePaths = [temp], SourceType = SourceTypes.Folder };
            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            string snap = mgr.CreateSnapshotDir();

            Assert.True(Directory.Exists(snap));
            Assert.True(Directory.Exists(Path.Combine(snap, SnapshotManager.FilesDir)));
            // 2026-09-08 起目录名带 6 位随机后缀（消除 TOCTOU 撞名），格式：时间戳_随机[ _序号]
            Assert.Matches(@"^\d{8}_\d{6}_[0-9a-z]{6}(_\d+)?$", Path.GetFileName(snap));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void CreateSnapshotDir_TwiceInSameSecond_NoNameCollision()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_snap2");
        try
        {
            var rule = new BackupRule { RuleName = "dir2", SourcePaths = [temp], SourceType = SourceTypes.Folder };
            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            string first = mgr.CreateSnapshotDir();
            string second = mgr.CreateSnapshotDir();

            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void EnforceLimit_RemovesOldestWhenExceeding()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_snap");
        try
        {
            var rule = new BackupRule { RuleName = "limit", SourcePaths = [temp], SourceType = SourceTypes.Folder };
            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            string snapRoot = mgr.SnapRoot;
            Directory.CreateDirectory(snapRoot);
            foreach (string? ts in new[] { "20200101_000000", "20200201_000000", "20200301_000000", "20200401_000000" })
                Directory.CreateDirectory(Path.Combine(snapRoot, ts));

            List<string> removed = mgr.EnforceLimit(2);

            Assert.True(removed.Count == 2);                              // 超 2 删 2 个最旧
            Assert.True(Directory.Exists(Path.Combine(snapRoot, "20200401_000000"))); // 最新保留
            Assert.True(Directory.Exists(Path.Combine(snapRoot, "20200301_000000")));
            Assert.False(Directory.Exists(Path.Combine(snapRoot, "20200101_000000")));
            Assert.False(Directory.Exists(Path.Combine(snapRoot, "20200201_000000")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    /// <summary>
    /// 回归守卫：BackupService 在「数据已复制但元数据未写入」时会保留快照目录供人工检查，
    /// CleanupOrphans 绝不能把它当垃圾删掉，否则用户数据被静默清除。
    /// </summary>
    [Fact]
    public void CleanupOrphans_KeepsHalfDoneSnapshotWithData()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_snap");
        try
        {
            var rule = new BackupRule { RuleName = "orphan_keep", SourcePaths = [temp], SourceType = SourceTypes.Folder };
            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            Directory.CreateDirectory(mgr.SnapRoot);

            // 构造中断现场：files 子目录内有真实数据，但 manifest/meta 均未写入
            string half = Path.Combine(mgr.SnapRoot, "20240101_000000");
            string filesDir = Path.Combine(half, SnapshotManager.FilesDir);
            Directory.CreateDirectory(filesDir);
            File.WriteAllText(Path.Combine(filesDir, "important.dat"), "user data");

            mgr.CleanupOrphans();

            Assert.True(Directory.Exists(half), "已含数据的半成品快照必须保留，不得删除用户数据");
            Assert.True(File.Exists(Path.Combine(filesDir, "important.dat")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    /// <summary>无数据、无元数据的「空壳」目录才是真正的孤儿，应当清理掉。</summary>
    [Fact]
    public void CleanupOrphans_RemovesEmptyShellDirs()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_snap");
        try
        {
            var rule = new BackupRule { RuleName = "orphan_drop", SourcePaths = [temp], SourceType = SourceTypes.Folder };
            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            Directory.CreateDirectory(mgr.SnapRoot);
            string empty = Path.Combine(mgr.SnapRoot, "20240102_000000");
            Directory.CreateDirectory(empty);

            int removed = mgr.CleanupOrphans();

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(empty));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
