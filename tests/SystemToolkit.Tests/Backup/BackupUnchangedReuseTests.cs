using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Tests;

/// <summary>
/// B5b-③「未变文件跳过」用例。
/// <para>
/// 本功能动的是**"哪些文件不复制"**这条路径：判据写错 = 快照里留下旧内容的副本
/// = **静默漏备份**。因此分两层钉：① 判据纯函数逐分支断言（离线、可反向验证）；
/// ② 端到端真跑两遍备份，验"复用后快照依然自洽可校验"。
/// </para>
/// </summary>
public class BackupUnchangedReuseTests
{
    // ==================================================================
    // 一、判据（纯函数）
    // ==================================================================

    private static FileEntry Entry(string source, long size, DateTime mtime, string sha = "abc123")
        => new FileEntry
        {
            SourcePath = source,
            RelativePath = "a.txt",
            Size = size,
            Mtime = mtime.ToString("o"),
            Sha256 = sha,
        };

    [Fact]
    public void Detect_EverythingMatches_IsReusable()
    {
        DateTime t = new(2026, 9, 13, 10, 30, 0, DateTimeKind.Local);

        Assert.True(UnchangedFileReuse.IsReusable(Entry(@"C:\data\a.txt", 100, t), @"C:\data\a.txt", 100, t));
    }

    [Theory]
    [InlineData(101, 0, true, @"C:\data\a.txt")]   // 字节数变了
    [InlineData(100, 1, true, @"C:\data\a.txt")]   // 修改时间变了
    [InlineData(100, 0, false, @"C:\data\a.txt")]  // 上一份快照里没有这个条目（新增/改名）
    [InlineData(100, 0, true, @"C:\other\a.txt")]  // 源路径不同（防张冠李戴）
    public void Detect_AnyMismatch_IsNotReusable(long size, int mtimeDeltaSeconds, bool hasPrevious, string currentSource)
    {
        DateTime t = new(2026, 9, 13, 10, 30, 0, DateTimeKind.Local);
        FileEntry? prev = hasPrevious ? Entry(@"C:\data\a.txt", 100, t) : null;

        Assert.False(UnchangedFileReuse.IsReusable(prev, currentSource, size, t.AddSeconds(mtimeDeltaSeconds)));
    }

    [Fact]
    public void Detect_SubSecondMtimeDifference_IsNotReusable()
    {
        // 同一秒但 tick 不同：判据必须比 tick，不能只到秒
        DateTime t = new(2026, 9, 13, 10, 30, 0, DateTimeKind.Local);

        Assert.False(UnchangedFileReuse.IsReusable(Entry(@"C:\data\a.txt", 100, t), @"C:\data\a.txt", 100, t.AddTicks(1)));
    }

    [Fact]
    public void Detect_PreviousWithoutChecksum_IsNotReusable()
    {
        // 旧版快照未记录哈希：复用会让本次清单失去校验依据（恢复后也无法校验）→ 必须重新复制
        DateTime t = new(2026, 9, 13, 10, 30, 0, DateTimeKind.Local);

        Assert.False(UnchangedFileReuse.IsReusable(Entry(@"C:\data\a.txt", 100, t, sha: ""), @"C:\data\a.txt", 100, t));
    }

    [Fact]
    public void Detect_UnparsableMtime_IsNotReusable()
    {
        // 时间不可解析（旧格式/损坏）→ 无法比对 → 不冒险
        DateTime t = new(2026, 9, 13, 10, 30, 0, DateTimeKind.Local);
        FileEntry prev = Entry(@"C:\data\a.txt", 100, t);
        prev.Mtime = "不是时间";

        Assert.False(UnchangedFileReuse.IsReusable(prev, @"C:\data\a.txt", 100, t));
    }

    [Fact]
    public void Detect_SameSizeAndMtime_DifferentContent_IsReusable_ThisIsTheAcceptedRisk()
    {
        // 🔴 钉住**已知且已裁定接受**的风险：判据只看「字节数 + 修改时间」，**不看内容**。
        //    因此"内容变了、但把修改时间改回原值、且大小恰好相同"会被判为未变，
        //    该文件不会重新复制 —— 这正是本功能**默认关闭**的原因。
        //    本用例存在的意义：把这条风险**显式记录**下来。将来若有人想默认开启，
        //    必须先正面推翻它（而不是以为判据比实际更严）。
        DateTime t = new(2026, 9, 13, 10, 30, 0, DateTimeKind.Local);
        FileEntry prev = Entry(@"C:\data\a.txt", 100, t, sha: "旧内容的哈希");

        Assert.True(UnchangedFileReuse.IsReusable(prev, @"C:\data\a.txt", 100, t));
    }

    // ==================================================================
    // 二、端到端：真跑两遍备份
    // ==================================================================

    private static (BackupConfigService Config, string TempDir) MakeEnv(bool skipUnchanged)
    {
        (BackupConfigService cfg, string temp) = TestHelpers.MakeConfig("fb_skip");
        cfg.Settings.SkipUnchangedFiles = skipUnchanged;
        cfg.Save();
        return (cfg, temp);
    }

    private static BackupRule MakeRule(string sourceFolder)
        => new() { RuleName = "未变跳过", SourcePath = sourceFolder, SourceType = SourceTypes.Folder };

    /// <summary>
    /// 等跨到下一秒再发起下一次备份。
    /// <para>
    /// 🔴 为什么用例必须这么做：快照目录名前 15 位是 <c>yyyyMMdd_HHmmss</c>，而
    /// <c>SnapshotManager.AllSnapshotDirs()</c> 的排序键是「前 15 位时间戳 + 数字序号」，
    /// **不含 2026-09-08 起追加的随机 6 位后缀** —— 同一秒内创建的两份快照排序键完全相同，
    /// 实际顺序取决于 <c>Directory.GetDirectories</c> 的返回顺序，**无法确定哪份才是"最新"**。
    /// 用例要比较"上一份 / 这一份"，就必须把两次备份落到不同的秒。
    /// （生产环境两次备份相隔以分钟/小时计，不受此影响；该同秒歧义属既有边界，
    /// 已在变更记录中登记，未顺手改动。）
    /// </para>
    /// </summary>
    private static void WaitForNextSecond()
    {
        int startSecond = DateTime.Now.Second;
        DateTime deadline = DateTime.Now.AddSeconds(5);
        while (DateTime.Now.Second == startSecond && DateTime.Now < deadline)
        {
            Thread.Sleep(20);
        }
    }

    [Fact]
    public async Task SkipUnchanged_IsOffByDefault()
    {
        // 计划裁定的默认值：不配置 = 与升级前行为完全一致（全部重新复制）
        (BackupConfigService cfg, string temp) = TestHelpers.MakeConfig("fb_skip");
        try
        {
            Assert.False(cfg.Settings.SkipUnchangedFiles);

            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "内容A");

            BackupRule rule = MakeRule(src);
            var svc = new BackupService(cfg);

            BackupResult first = await svc.BackupRuleAsync(rule);
            BackupResult second = await svc.BackupRuleAsync(rule);

            Assert.True(first.Success, first.Message);
            Assert.True(second.Success, second.Message);
            Assert.Equal(0, second.ReusedFileCount);
            Assert.DoesNotContain("未重读源文件", second.Message);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task SkipUnchanged_SecondBackup_ReusesBytesAndSnapshotStaysVerifiable()
    {
        (BackupConfigService cfg, string temp) = MakeEnv(skipUnchanged: true);
        try
        {
            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "内容A");
            File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 4096));

            BackupRule rule = MakeRule(src);
            var svc = new BackupService(cfg);

            BackupResult first = await svc.BackupRuleAsync(rule);
            Assert.True(first.Success, first.Message);
            Assert.Equal(0, first.ReusedFileCount);   // 第一份没有可复用的基准

            WaitForNextSecond();
            BackupResult second = await svc.BackupRuleAsync(rule);
            Assert.True(second.Success, second.Message);
            Assert.Equal(2, second.ReusedFileCount);

            // 🔴 有复用就**不许**报 passed（本次根本没读那些文件的源）→ 只能是 skipped
            Assert.Equal(ChecksumStatuses.Skipped, second.ChecksumStatus);
            Assert.Contains("未重读源文件", second.Message);

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            (string Dir, SnapshotInfo Info)? latest = mgr.LatestSnapshotWithDir();
            Assert.True(latest.HasValue);
            Assert.Equal(2, latest!.Value.Info.ReusedFileCount);   // 计数已持久化进清单

            // 复用后的快照必须**自洽**：用「校验快照」的同一判据读回比对
            SnapshotVerifyReport report = await new SnapshotVerifier()
                .VerifyAsync(latest.Value.Info, latest.Value.Dir);
            Assert.True(report.Success, report.Message);
            Assert.Equal(2, report.Ok);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task ReusedSnapshot_SurvivesDeletionOfItsReuseSource()
    {
        // 🔴 这是"必须用**硬**链接、不能用符号链接"的证明：
        //    硬链接与旧快照共享同一份数据，把**复用源**快照删掉后，新快照仍能通过完整性校验。
        //    若改用符号链接，这里读文件会直接失败（链接指向的路径已被删除）
        //    —— 用户以为有备份、实际打不开，正是本仓最不能接受的失败模式。
        (BackupConfigService cfg, string temp) = MakeEnv(skipUnchanged: true);
        try
        {
            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "内容A");

            BackupRule rule = MakeRule(src);
            var svc = new BackupService(cfg);
            BackupResult first = await svc.BackupRuleAsync(rule);
            Assert.True(first.Success, first.Message);

            WaitForNextSecond();
            BackupResult second = await svc.BackupRuleAsync(rule);
            Assert.Equal(1, second.ReusedFileCount);

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            List<string> dirs = mgr.AllSnapshotDirs();   // 新 → 旧
            Assert.Equal(2, dirs.Count);
            // 自证前提：两份快照确实落在不同的秒，dirs[0] 才是"最新"（否则本用例会默默测错对象）
            Assert.True(
                string.CompareOrdinal(Path.GetFileName(dirs[0]), Path.GetFileName(dirs[1])) > 0,
                $"快照目录名未能区分新旧：{Path.GetFileName(dirs[0])} / {Path.GetFileName(dirs[1])}");
            string newest = dirs[0];
            mgr.DeleteSnapshot(dirs[1]);                 // 删掉复用源

            SnapshotInfo? info = mgr.ReadSnapshot(newest);
            Assert.NotNull(info);

            SnapshotVerifyReport report = await new SnapshotVerifier().VerifyAsync(info!, newest);
            Assert.True(report.Success, report.Message);
            Assert.Equal(1, report.Ok);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task ReuseSourceBytesMissing_FallsBackToRealCopy()
    {
        // 复用源的清单还在、但 files 里的副本没了（被人工清理 / 复用源被移走）
        // → 硬链接必失败 → **必须回退真实复制**：文件仍在、哈希仍对，
        //    绝不允许出现"清单里登记了、快照里却没有"的条目。
        (BackupConfigService cfg, string temp) = MakeEnv(skipUnchanged: true);
        try
        {
            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "内容A");

            BackupRule rule = MakeRule(src);
            var svc = new BackupService(cfg);
            BackupResult first = await svc.BackupRuleAsync(rule);
            Assert.True(first.Success, first.Message);

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            string oldSnap = mgr.AllSnapshotDirs()[0];
            Directory.Delete(Path.Combine(oldSnap, SnapshotManager.FilesDir), recursive: true);

            WaitForNextSecond();
            BackupResult second = await svc.BackupRuleAsync(rule);
            Assert.True(second.Success, second.Message);
            Assert.Equal(0, second.ReusedFileCount);   // 回退复制 = 本次没有复用

            (string Dir, SnapshotInfo Info)? latest = mgr.LatestSnapshotWithDir();
            Assert.True(latest.HasValue);
            SnapshotVerifyReport report = await new SnapshotVerifier()
                .VerifyAsync(latest!.Value.Info, latest.Value.Dir);
            Assert.True(report.Success, report.Message);
            Assert.Equal(1, report.Ok);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task SourceChanged_IsRecopiedNotReused()
    {
        // 变了的文件必须重新复制，且清单里记的是**新内容**的哈希（这条是"漏备份"的直接反面）
        (BackupConfigService cfg, string temp) = MakeEnv(skipUnchanged: true);
        try
        {
            string src = Path.Combine(temp, "src");
            Directory.CreateDirectory(src);
            string changed = Path.Combine(src, "a.txt");
            File.WriteAllText(changed, "内容A");
            File.WriteAllText(Path.Combine(src, "b.txt"), "内容B");

            BackupRule rule = MakeRule(src);
            var svc = new BackupService(cfg);
            BackupResult first = await svc.BackupRuleAsync(rule);
            Assert.True(first.Success, first.Message);

            File.WriteAllText(changed, "内容A 变长了，长度不同");

            WaitForNextSecond();
            BackupResult second = await svc.BackupRuleAsync(rule);
            Assert.True(second.Success, second.Message);
            Assert.Equal(1, second.ReusedFileCount);   // b.txt 复用；a.txt 重新复制

            var mgr = SnapshotManager.FromRule(rule, cfg.Settings.BackupRoot);
            (string Dir, SnapshotInfo Info)? latest = mgr.LatestSnapshotWithDir();
            Assert.True(latest.HasValue);
            FileEntry a = latest!.Value.Info.Files.Single(f => f.RelativePath == "a.txt");
            Assert.Equal(Sha256Hasher.HashFile(changed), a.Sha256);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ==================================================================
    // 三、硬链接原语（正例 + 反例）
    // ==================================================================

    [Fact]
    public void HardLink_Succeeds_AndSurvivesDeletionOfTheOriginalPath()
    {
        // 硬链接语义的直接证明：删掉**原路径**后，链接仍能读出完整内容
        // （这正是"删掉旧快照后新快照依然可恢复"的底层依据）
        string dir = Path.Combine(Path.GetTempPath(), "fb_link_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string original = Path.Combine(dir, "original.txt");
            string link = Path.Combine(dir, "link.txt");
            File.WriteAllText(original, "同一份数据");

            Assert.True(FileLinker.TryCreateHardLink(link, original));
            Assert.True(File.Exists(link));

            File.Delete(original);
            Assert.Equal("同一份数据", File.ReadAllText(link));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void HardLink_Fails_WhenSourceMissingOrTargetOccupiedOrTargetIsDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fb_link_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string original = Path.Combine(dir, "original.txt");
            string link = Path.Combine(dir, "link.txt");
            File.WriteAllText(original, "x");

            // ① 来源不存在（对应"复用源的副本已被清理"）
            Assert.False(FileLinker.TryCreateHardLink(link, Path.Combine(dir, "不存在.txt")));

            // ② 目标已存在：不能覆盖
            File.WriteAllText(link, "已占用");
            Assert.False(FileLinker.TryCreateHardLink(link, original));

            // ③ 目标路径是个**目录** → File.Exists 为 false，绕过前置检查，
            //    真正走到 CCreateHardLinkW 失败分支。
            // 🔴 这条是唯一能验证"**系统调用本身**失败也必须回退"的用例：只测前置检查的话，
            //    把 P/Invoke 的返回值改成恒 true 也不会变红（反向验证实测发现的覆盖洞）。
            string occupiedDir = Path.Combine(dir, "occupied");
            Directory.CreateDirectory(occupiedDir);
            Assert.False(FileLinker.TryCreateHardLink(occupiedDir, original));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
