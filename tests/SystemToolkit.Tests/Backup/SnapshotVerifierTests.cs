using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Tests.Backup;

/// <summary>
/// 快照校验（2026-09-07 补齐旧版「校验」命令，Core 下沉为服务以便单测）。
/// 场景：全部一致 / 内容被篡改 / 文件缺失。
/// </summary>
public class SnapshotVerifierTests : IDisposable
{
    private readonly string _root;

    public SnapshotVerifierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "stk_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, SnapshotManager.FilesDir));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试
        }
        GC.SuppressFinalize(this);
    }

    private (SnapshotInfo Info, string FilesDir) BuildSnapshot(int fileCount)
    {
        string filesDir = Path.Combine(_root, SnapshotManager.FilesDir);
        var info = new SnapshotInfo { SnapshotId = "snap-1", RuleId = "rule-1", RuleName = "T" };
        for (int i = 0; i < fileCount; i++)
        {
            string rel = $"f{i}.txt";
            string path = Path.Combine(filesDir, rel);
            File.WriteAllText(path, "content-" + i);
            info.Files.Add(new FileEntry
            {
                SourcePath = Path.Combine(_root, rel),
                RelativePath = rel,
                Size = new FileInfo(path).Length,
                Sha256 = Sha256Hasher.HashFile(path),
            });
        }

        info.FileCount = fileCount;
        return (info, filesDir);
    }

    [Fact]
    public async Task AllFilesIntact_ReportsSuccess()
    {
        (SnapshotInfo info, _) = BuildSnapshot(3);
        var verifier = new SnapshotVerifier(2);

        SnapshotVerifyReport report = await verifier.VerifyAsync(info, _root);

        Assert.True(report.Success);
        Assert.Equal(3, report.Total);
        Assert.Equal(3, report.Ok);
        Assert.Equal(0, report.Failed);
        Assert.Equal(0, report.Missing);
    }

    [Fact]
    public async Task TamperedFile_ReportsHashMismatch()
    {
        (SnapshotInfo info, string filesDir) = BuildSnapshot(2);
        File.WriteAllText(Path.Combine(filesDir, "f1.txt"), "tampered!");
        var verifier = new SnapshotVerifier(2);

        SnapshotVerifyReport report = await verifier.VerifyAsync(info, _root);

        Assert.False(report.Success);
        Assert.Equal(1, report.Ok);
        Assert.Equal(1, report.Failed);
        Assert.Contains("f1.txt", report.Failures[0]);
    }

    [Fact]
    public async Task MissingFile_ReportedAsMissing()
    {
        (SnapshotInfo info, string filesDir) = BuildSnapshot(2);
        File.Delete(Path.Combine(filesDir, "f0.txt"));
        var verifier = new SnapshotVerifier(2);

        SnapshotVerifyReport report = await verifier.VerifyAsync(info, _root);

        Assert.False(report.Success);
        Assert.Equal(1, report.Missing);
        Assert.Equal(1, report.Ok);
        Assert.Contains("缺失", report.Failures[0]);
    }

    [Fact]
    public async Task EmptySnapshotDir_RequiresPath()
        => await Assert.ThrowsAsync<ArgumentException>(() => new SnapshotVerifier(1).VerifyAsync(new SnapshotInfo(), ""));
}
