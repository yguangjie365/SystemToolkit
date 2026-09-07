using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// M9/L14 回归（全面代码审查 2026-09-05）：旧配置根迁移与历史备份修剪。
/// 全程临时目录，不触真实注册表/用户目录（03 测试规范）。
/// </summary>
public class EnvListServiceMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"stk_env_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyDir_LegacyRootHasManifests_CopiesToNewRoot()
    {
        string legacy = Path.Combine(_root, "legacy");
        string newDir = Path.Combine(_root, "new");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "env_winget_list.json"), "[]");
        File.WriteAllText(Path.Combine(legacy, "env_manual_list.json"), "[]");
        File.WriteAllText(Path.Combine(legacy, "readme.txt"), "x"); // 非 json 不迁移

        EnvListService.MigrateLegacyDir(legacy, newDir);

        Assert.True(File.Exists(Path.Combine(newDir, "env_winget_list.json")));
        Assert.True(File.Exists(Path.Combine(newDir, "env_manual_list.json")));
        Assert.False(File.Exists(Path.Combine(newDir, "readme.txt")));
    }

    [Fact]
    public void MigrateLegacyDir_NewRootExists_DoesNotOverwrite()
    {
        string legacy = Path.Combine(_root, "legacy");
        string newDir = Path.Combine(_root, "new");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "env_winget_list.json"), "old");
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "env_winget_list.json"), "current");

        EnvListService.MigrateLegacyDir(legacy, newDir);

        Assert.Equal("current", File.ReadAllText(Path.Combine(newDir, "env_winget_list.json")));
    }

    [Fact]
    public void MigrateLegacyDir_LegacyRootMissing_NoOpDoesNotThrow()
    {
        string newDir = Path.Combine(_root, "new");
        EnvListService.MigrateLegacyDir(Path.Combine(_root, "missing"), newDir);
        Assert.False(Directory.Exists(newDir));
    }

    [Fact]
    public void PruneTimestampedFiles_KeepsLatestNFiles_DeletesTheRest()
    {
        string dir = Path.Combine(_root, "prune");
        Directory.CreateDirectory(dir);
        // 文件名含时间戳，Ordinal 序即时间序
        foreach (string ts in new[] { "20250101_000001", "20250102_000002", "20250103_000003", "20250104_000004", "20250105_000005" })
        {
            File.WriteAllText(Path.Combine(dir, $"catalog_backup_{ts}.json"), ts);
        }
        File.WriteAllText(Path.Combine(dir, "catalog_backup_other.json"), "x"); // 同前缀非时间戳形态，不得误删

        EnvListService.PruneTimestampedFiles(dir, "catalog_backup_", keep: 3);

        Assert.True(File.Exists(Path.Combine(dir, "catalog_backup_20250105_000005.json")));
        Assert.True(File.Exists(Path.Combine(dir, "catalog_backup_20250104_000004.json")));
        Assert.True(File.Exists(Path.Combine(dir, "catalog_backup_20250103_000003.json")));
        Assert.False(File.Exists(Path.Combine(dir, "catalog_backup_20250102_000002.json")));
        Assert.False(File.Exists(Path.Combine(dir, "catalog_backup_20250101_000001.json")));
        Assert.True(File.Exists(Path.Combine(dir, "catalog_backup_other.json")));
    }

    [Fact]
    public void PruneTimestampedFiles_FewerThanKeep_KeepsAllDoesNotThrow()
    {
        string dir = Path.Combine(_root, "prune2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "catalog_backup_20250101_000001.json"), "x");

        EnvListService.PruneTimestampedFiles(dir, "catalog_backup_", keep: 5);

        Assert.True(File.Exists(Path.Combine(dir, "catalog_backup_20250101_000001.json")));
    }
}
