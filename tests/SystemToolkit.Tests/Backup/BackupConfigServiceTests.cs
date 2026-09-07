using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// BackupConfigService 契约（批次一新增服务的钉子）：默认重建、round-trip、
/// 损坏文件 .corrupt_* 留痕重建、非法值钳制、动态选盘兜底。
/// </summary>
public class BackupConfigServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bkp-cfg-" + Guid.NewGuid().ToString("N"));

    private BackupConfigService NewService() => new(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_CreatesDefaultSettingsFile()
    {
        BackupConfigService service = NewService();

        service.Load();

        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
        Assert.True(Directory.Exists(service.Settings.BackupRoot) || !string.IsNullOrEmpty(service.Settings.BackupRoot));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        BackupConfigService service = NewService();
        service.Load();
        service.Settings.BackupRoot = @"D:\BackupRoot";
        service.Settings.MaxSnapshots = 12;
        service.Settings.MaxWorkers = 6;
        service.Save();

        BackupConfigService reloaded = NewService();
        reloaded.Load();

        Assert.Equal(@"D:\BackupRoot", reloaded.Settings.BackupRoot);
        Assert.Equal(12, reloaded.Settings.MaxSnapshots);
        Assert.Equal(6, reloaded.Settings.MaxWorkers);
    }

    [Fact]
    public void Load_CorruptedFile_PreservesCopyAsCorrupt_AndRebuilds()
    {
        // 禁止静默丢弃用户配置：损坏文件必须 .corrupt_* 留痕
        BackupConfigService service = NewService();
        service.Load();
        string settingsFile = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settingsFile, "{ 这不是合法 JSON ]]]");

        service.Load();

        Assert.True(Directory.GetFiles(_dir, "settings.json.corrupt_*.json").Length == 1,
            "损坏配置应留痕为 .corrupt_* 文件");
        // 重建后 EnsureBackupRoot 会动态选盘（本机固定盘最大剩余），非空且根路径化即契约
        Assert.False(string.IsNullOrWhiteSpace(service.Settings.BackupRoot));
        Assert.True(Path.IsPathRooted(service.Settings.BackupRoot));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(999, 100)]
    public void Load_ClampsMaxSnapshotsIntoValidRange(int raw, int expected)
    {
        BackupConfigService service = NewService();
        service.Load();
        service.Settings.MaxSnapshots = raw;
        service.Save();

        BackupConfigService reloaded = NewService();
        reloaded.Load();

        Assert.Equal(expected, reloaded.Settings.MaxSnapshots);
    }

    [Fact]
    public void PickDefaultBackupRoot_ReturnsNonEmptyPath()
    {
        string root = BackupConfigService.PickDefaultBackupRoot();

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Path.IsPathRooted(root));
    }
}
