using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// DriverBackupService 编排守卫：按包独立暂存 → 实证核对 → 分类整理 → 三件套。
/// 用 Fake 客户端模拟 pnputil 产物（暂存目录 = StageDirFor，内层目录 = 原始 INF 名，
/// 与 pnputil /export-driver 实际行为一致），不触碰真实 pnputil/提权。
/// </summary>
public class DriverBackupServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stk_bksvc_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>模拟 pnputil /export-driver：每个发布名写入 _stage_&lt;基&gt;\&lt;原始名基&gt;\&lt;名&gt;.inf。
    /// names 中含 "oem-missing" 前缀的项模拟导出失败（不产生产物）。</summary>
    private sealed class FakePnpUtil : IPnpUtilClient
    {
        public Task<IReadOnlyList<DriverPackage>> EnumDriversAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DriverPackage>>([]);

        public Task<DriverRunResult> ExportAsync(string publishedName, string destinationDir, CancellationToken ct = default)
        {
            // 模拟 pnputil 行为：在目标目录内创建以原始 INF 名命名的子目录并写入产物
            string pkgDir = Path.Combine(destinationDir, "payload");
            Directory.CreateDirectory(pkgDir);
            File.WriteAllText(Path.Combine(pkgDir, "payload.inf"), "x");
            return Task.FromResult(DriverRunResult_Success);
        }

        public async Task<DriverRunResult> ExportManyAsync(
            IReadOnlyList<string> publishedNames, string destinationDir, CancellationToken ct = default)
        {
            foreach (string name in publishedNames)
            {
                if (name.StartsWith("oem-missing", StringComparison.Ordinal))
                {
                    continue; // 模拟失败：不产生任何产物
                }

                await ExportAsync(name, DriverBackupOrganizer.StageDirFor(destinationDir, name), ct).ConfigureAwait(false);
            }

            return DriverRunResult_Success;
        }

        public Task<DriverRunResult> DeleteAsync(string publishedName, bool force, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及删除");

        public Task<DriverRunResult> DeleteManyAsync(IReadOnlyList<string> publishedNames, bool force, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及删除");

        public Task<DriverRunResult> AddDriverAsync(string infPath, bool install, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及添加");

        public Task<DriverRunResult> AddManyAsync(IReadOnlyList<string> infPaths, bool install, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及添加");

        private static readonly DriverRunResult DriverRunResult_Success = new(true, 0, "");
    }

    private static DriverPackage Pkg(string published, string original, string version) => new()
    {
        PublishedName = published,
        OriginalName = original,
        Version = version,
        ClassName = "Net",
    };

    [Fact]
    public async Task BackupAsync_SameOriginalInfMultipleVersions_GetSeparateDirectories_NoOverwrite()
    {
        var known = new List<DriverPackage>
        {
            Pkg("oem1.inf", "nvlt.inf", "32.0.15.6102"),
            Pkg("oem2.inf", "nvlt.inf", "32.0.15.6614"),
        };
        string destDir = Path.Combine(_dir, "backup");

        DriverBackupResult result = await new DriverBackupService(new FakePnpUtil(), () => new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>(StringComparer.OrdinalIgnoreCase)).BackupAsync(
            ["oem1.inf", "oem2.inf"], known, destDir, DriverBackupScope.All);

        Assert.Empty(result.Missing);
        Assert.Equal(2, result.Exported.Count);
        Assert.Equal(2, result.MovedDirs);
        // 同名原始 INF 的两个版本各自成目录（D-3 核心断言）
        Assert.True(Directory.Exists(Path.Combine(destDir, "Drivers", "Net", "nvlt_32.0.15.6102")));
        Assert.True(Directory.Exists(Path.Combine(destDir, "Drivers", "Net", "nvlt_32.0.15.6614")));
        // 暂存残壳清理
        Assert.DoesNotContain(
            Directory.GetDirectories(Path.Combine(destDir, "Drivers")),
            d => Path.GetFileName(d).StartsWith("_stage_", StringComparison.Ordinal));
        // 三件套
        Assert.True(File.Exists(Path.Combine(destDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(destDir, "devices.json")));
        Assert.True(File.Exists(Path.Combine(destDir, "checksum.json")));
    }

    [Fact]
    public async Task BackupAsync_PartialFailure_ExportedOnesInManifest_MissingReportedExplicitly()
    {
        var known = new List<DriverPackage>
        {
            Pkg("oem1.inf", "ok.inf", "1.0"),
            Pkg("oem-missing.inf", "ghost.inf", "1.0"),
        };
        string destDir = Path.Combine(_dir, "backup2");

        DriverBackupResult result = await new DriverBackupService(new FakePnpUtil(), () => new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>(StringComparer.OrdinalIgnoreCase)).BackupAsync(
            ["oem1.inf", "oem-missing.inf"], known, destDir, DriverBackupScope.All);

        Assert.Equal(["oem-missing.inf"], result.Missing);
        _ = Assert.Single(result.Exported);
        Assert.True(Directory.Exists(Path.Combine(destDir, "Drivers", "Net", "ok_1.0")));

        string manifest = File.ReadAllText(Path.Combine(destDir, "manifest.json"));
        Assert.Contains("ok.inf", manifest);
        Assert.DoesNotContain("ghost.inf", manifest); // 实证式：清单只含真实产物
    }

    [Fact]
    public async Task BackupAsync_AllFailed_DoesNotWriteTripleArtifacts()
    {
        string destDir = Path.Combine(_dir, "backup3");

        DriverBackupResult result = await new DriverBackupService(new FakePnpUtil(), () => new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>(StringComparer.OrdinalIgnoreCase)).BackupAsync(
            ["oem-missing.inf"], [Pkg("oem-missing.inf", "g.inf", "1.0")], destDir, DriverBackupScope.All);

        _ = Assert.Single(result.Missing);
        Assert.Empty(result.Exported);
        Assert.False(File.Exists(Path.Combine(destDir, "manifest.json")));
    }
}
