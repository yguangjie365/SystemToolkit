using System.Text;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>备份引擎核心用例测试（自旧工程移植）。</summary>
public class BackupServiceTests
{
    [Fact]
    public async Task SingleFile_Backup_CreatesSnapshot()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string src = Path.Combine(temp, "src_single");
            Directory.CreateDirectory(src);
            string f1 = Path.Combine(src, "hello.txt");
            File.WriteAllText(f1, "你好，文件备份。Hello Backup!", Encoding.UTF8);

            var rule = new BackupRule { RuleName = "单文件规则", SourcePath = f1, SourceType = SourceTypes.File };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.FileCount);

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            SnapshotInfo? info = mgr.LatestSnapshot();
            Assert.NotNull(info);
            Assert.Equal(1, info!.FileCount);
            Assert.Equal("hello.txt", info.Files[0].RelativePath);
            Assert.False(string.IsNullOrEmpty(info.Files[0].Sha256));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Folder_Backup_Recursive_WithChinesePaths()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string src = Path.Combine(temp, "src_folder");
            Directory.CreateDirectory(Path.Combine(src, "sub", "深"));
            File.WriteAllText(Path.Combine(src, "readme.txt"), "readme 内容", Encoding.UTF8);
            File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "a");
            File.WriteAllText(Path.Combine(src, "sub", "深", "中文字符文件.txt"), "中文路径测试", Encoding.UTF8);

            var rule = new BackupRule { RuleName = "文件夹规则", SourcePath = src, SourceType = SourceTypes.Folder };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);

            Assert.True(result.Success, result.Message);

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            SnapshotInfo? info = mgr.LatestSnapshot();
            Assert.NotNull(info);
            var rels = info!.Files.Select(f => f.RelativePath).OrderBy(x => x).ToList();
            Assert.Equal(3, rels.Count);
            Assert.Contains("readme.txt", rels);
            Assert.Contains("sub/a.txt", rels);
            Assert.Contains("sub/深/中文字符文件.txt", rels);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task SameNameFiles_AreIsolated()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string srcA = Path.Combine(temp, "a");
            string srcB = Path.Combine(temp, "b");
            Directory.CreateDirectory(srcA);
            Directory.CreateDirectory(srcB);
            File.WriteAllText(Path.Combine(srcA, "hello.txt"), "内容A");
            File.WriteAllText(Path.Combine(srcB, "hello.txt"), "内容B");

            var r1 = new BackupRule { RuleName = "同名A", SourcePath = Path.Combine(srcA, "hello.txt"), SourceType = SourceTypes.File };
            var r2 = new BackupRule { RuleName = "同名B", SourcePath = Path.Combine(srcB, "hello.txt"), SourceType = SourceTypes.File };
            var svc = new BackupService(cfg);
            await svc.BackupRuleAsync(r1);
            await svc.BackupRuleAsync(r2);

            SnapshotInfo? m1 = SnapshotManager.FromRule(r1, cfg.Settings.BackupRoot).LatestSnapshot();
            SnapshotInfo? m2 = SnapshotManager.FromRule(r2, cfg.Settings.BackupRoot).LatestSnapshot();
            Assert.NotNull(m1);
            Assert.NotNull(m2);
            Assert.Equal("内容A", File.ReadAllText(Path.Combine(m1!.BackupPath, m1.Files[0].RelativePath)));
            Assert.Equal("内容B", File.ReadAllText(Path.Combine(m2!.BackupPath, m2.Files[0].RelativePath)));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task SnapshotLimit_Enforced()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "f.txt"), "x");

            var rule = new BackupRule { RuleName = "快照上限", SourcePath = src, SourceType = SourceTypes.Folder, MaxSnapshots = 5 };
            var svc = new BackupService(cfg);
            for (int i = 0; i < 8; i++)
            {
                BackupResult r = await svc.BackupRuleAsync(rule);
                Assert.True(r.Success, r.Message);
                await Task.Delay(20); // 保证时间戳不同
            }

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            Assert.True(mgr.AllSnapshotDirs().Count <= 5, $"期望≤5，实际{mgr.AllSnapshotDirs().Count}");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task EmptyDirs_Recorded()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string src = Path.Combine(temp, "src_empty");
            Directory.CreateDirectory(Path.Combine(src, "empty_sub", "deep"));
            File.WriteAllText(Path.Combine(src, "file.txt"), "x");

            var rule = new BackupRule { RuleName = "空目录", SourcePath = src, SourceType = SourceTypes.Folder };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);

            Assert.True(result.Success, result.Message);
            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            SnapshotInfo? info = mgr.LatestSnapshot();
            Assert.NotNull(info);
            Assert.Contains("empty_sub/deep", info!.EmptyDirs);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task MultiSource_SameName_Isolated()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            // 源文件放在 src 子目录下，避免与 backup_root 形成路径重叠
            string srcDir = Path.Combine(temp, "src");
            Directory.CreateDirectory(srcDir);
            string fA = Path.Combine(srcDir, "a.txt");
            string fB = Path.Combine(srcDir, "b.txt");
            File.WriteAllText(fA, "AAA");
            File.WriteAllText(fB, "BBB");

            var rule = new BackupRule
            {
                RuleName = "多源",
                SourcePath = fA,
                SourcePaths = [fA, fB],
                SourceType = SourceTypes.File,
            };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);

            Assert.True(result.Success, result.Message);
            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            SnapshotInfo? info = mgr.LatestSnapshot();
            Assert.NotNull(info);
            Assert.Equal(2, info!.FileCount);
            // 两个前缀不同的 hello.txt / a.txt / b.txt
            Assert.Equal(2, info.Files.Count);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Canceled_Backup_NoLeftoverSnapshot()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            for (int i = 0; i < 50; i++)
                File.WriteAllText(Path.Combine(src, $"f{i}.txt"), new string('x', 1000));

            var rule = new BackupRule { RuleName = "取消", SourcePath = src, SourceType = SourceTypes.Folder };
            var svc = new BackupService(cfg);
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // 一开始就取消

            BackupResult result = await svc.BackupRuleAsync(rule, ct: cts.Token);
            Assert.True(result.Canceled);

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            Assert.Empty(mgr.AllSnapshotDirs()); // 无残留快照
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task PathOverlap_SourceContainsBackupRoot_Fails()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string src = Path.Combine(temp, "src_overlap");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "f.txt"), "x");

            // 备份根设在源目录内部 → 必须被拒绝
            cfg.Settings.BackupRoot = Path.Combine(src, "nested_backup");
            cfg.Save();

            var rule = new BackupRule { RuleName = "重叠", SourcePath = src, SourceType = SourceTypes.Folder };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);

            Assert.False(result.Success);
            Assert.Contains("重叠", result.Message);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task PathOverlap_BackupRootContainsSource_Fails()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            // 备份根在 temp 下，源也在 temp 下（备份根包含源）
            string backupRoot = Path.Combine(temp, "backup_root_container");
            string src = Path.Combine(backupRoot, "src_sub");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "f.txt"), "x");

            cfg.Settings.BackupRoot = backupRoot;
            cfg.Save();

            var rule = new BackupRule { RuleName = "反向重叠", SourcePath = src, SourceType = SourceTypes.Folder };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);

            Assert.False(result.Success);
            Assert.Contains("重叠", result.Message);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    /// <summary>
    /// 回归守卫：并发复制时多个文件共享同一目标父目录，曾因 "createdDirs" 缓存导致竞态
    /// （某线程刚标记 key 但目录尚未创建完，其它线程跳过创建直接写文件），从而抛出
    /// "Could not find a part of the path"。此测试在同一目录下创建大量文件，确保无该失败。
    /// </summary>
    [Fact]
    public async Task FolderBackup_ManyFilesInSameDir_NoMissingPartError()
    {
        (BackupConfigService? cfg, string? temp) = TestHelpers.MakeConfig("fb_bak");
        try
        {
            string src = Path.Combine(temp, "src_many");
            string sameDir = Path.Combine(src, "deep", "dir");
            Directory.CreateDirectory(sameDir);
            for (int i = 0; i < 30; i++)
                File.WriteAllText(Path.Combine(sameDir, $"file{i:D2}.txt"), $"content {i}");

            var rule = new BackupRule { RuleName = "并发目录规则", SourcePath = src, SourceType = SourceTypes.Folder };
            var svc = new BackupService(cfg);
            BackupResult result = await svc.BackupRuleAsync(rule);

            Assert.True(result.Success, result.Message);
            Assert.Empty(result.Failures);
            Assert.Equal(30, result.FileCount);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
