using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 恢复冲突预演测试（批次二）：与真实恢复同目标解析规则、只读不落盘、
/// 越界条目按 Blocked 呈现。真实恢复行为由 RestoreServiceTests 另行钉死。
/// </summary>
public class RestorePreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bkp-preview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SnapshotInfo MakeSnapshot(params string[] relativePaths)
    {
        Directory.CreateDirectory(_dir);
        return new SnapshotInfo
        {
            RuleName = "预演规则",
            SourcePaths = [Path.Combine(_dir, "src")],
            BackupPath = Path.Combine(_dir, "files"),
            Files = relativePaths.Select(rel => new FileEntry
            {
                SourcePath = Path.Combine(Path.Combine(_dir, "src"), rel.Replace('/', Path.DirectorySeparatorChar)),
                RelativePath = rel,
                Size = 3,
            }).ToList(),
        };
    }

    [Fact]
    public async Task Preview_TargetEmpty_AllFresh_NoWrites()
    {
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var service = new RestoreService();

        RestorePreviewReport report = await service.PreviewConflictsAsync(
            MakeSnapshot("a.txt", "sub/b.txt"), target);

        Assert.Equal(2, report.Total);
        Assert.Equal(0, report.ExistsCount);
        Assert.Equal(0, report.BlockedCount);
        Assert.True(report.Entries.All(e => !e.ExistsInTarget));
        // 预演绝不落盘
        Assert.True(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any() == false);
    }

    [Fact]
    public async Task Preview_TargetHasExistingFile_CountsAsConflict()
    {
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        File.WriteAllText(Path.Combine(target, "a.txt"), "existing");
        var service = new RestoreService();

        RestorePreviewReport report = await service.PreviewConflictsAsync(
            MakeSnapshot("a.txt", "sub/b.txt"), target);

        Assert.Equal(1, report.ExistsCount);
        Assert.Contains(report.Entries, e => e.RelativePath == "a.txt" && e.ExistsInTarget);
    }

    [Fact]
    public async Task Preview_UnsafeRelativePath_MarkedBlocked()
    {
        // 恶意清单：相对路径带 .. → 真实恢复会拒绝，预演按 Blocked 呈现
        var service = new RestoreService();
        SnapshotInfo snapshot = MakeSnapshot("..\\evil.txt");

        RestorePreviewReport report = await service.PreviewConflictsAsync(snapshot, Path.Combine(_dir, "target"));

        Assert.Equal(1, report.BlockedCount);
        Assert.Equal(0, report.ExistsCount);
    }
}
