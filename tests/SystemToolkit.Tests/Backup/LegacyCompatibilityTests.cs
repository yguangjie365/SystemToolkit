using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>验证旧版工具生成的 JSON 数据可被正确解析（数据兼容，自旧工程移植）。</summary>
public class LegacyCompatibilityTests
{
    [Fact]
    public void BackupRule_Deserializes_LegacyRulesJson()
    {
        // 旧版 rules.json 中的单条规则（小写字符串字段）
        string json = """
            {
              "rule_id": "a1b2c3d4e5f6a1b2",
              "rule_name": "工作文档",
              "source_path": "D:\\Docs",
              "source_paths": ["D:\\Docs", "D:\\Notes"],
              "source_type": "folder",
              "backup_root": "",
              "use_global_backup_root": true,
              "enabled": true,
              "max_snapshots": 7,
              "created_at": "2026-08-10T10:00:00",
              "updated_at": "2026-08-10T10:00:00",
              "description": "测试规则"
            }
            """;

        BackupRule? rule = System.Text.Json.JsonSerializer.Deserialize<BackupRule>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });

        Assert.NotNull(rule);
        Assert.Equal("a1b2c3d4e5f6a1b2", rule!.RuleId);
        Assert.Equal("工作文档", rule.RuleName);
        Assert.Equal("folder", rule.SourceType);
        Assert.Equal(7, rule.MaxSnapshots);
        Assert.True(rule.Enabled);
        Assert.Equal(2, rule.Sources().Count);
    }

    [Fact]
    public void SnapshotInfo_Deserializes_LegacyManifestJson()
    {
        // 旧版 manifest.json（含小写 status / checksum_status）
        string json = """
            {
              "snapshot_id": "r1_20260810_120000",
              "rule_id": "r1",
              "created_at": "20260810_120000",
              "source_path": "D:\\Docs",
              "source_paths": ["D:\\Docs"],
              "backup_path": "D:\\Backup\\Rule_r1\\snapshots\\20260810_120000\\files",
              "file_count": 1,
              "total_size": 1024,
              "status": "success",
              "checksum_status": "passed",
              "files": [
                {
                  "source_path": "D:\\Docs\\readme.txt",
                  "relative_path": "readme.txt",
                  "size": 1024,
                  "mtime": "2026-08-10T09:00:00",
                  "sha256": "abc123",
                  "backup_time": "2026-08-10T12:00:00"
                }
              ],
              "empty_dirs": ["sub"]
            }
            """;

        SnapshotInfo? info = System.Text.Json.JsonSerializer.Deserialize<SnapshotInfo>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });

        Assert.NotNull(info);
        Assert.Equal("success", info!.Status);
        Assert.Equal("passed", info.ChecksumStatus);
        Assert.Equal(1, info.FileCount);
        Assert.Single(info.Files);
        Assert.Equal("readme.txt", info.Files[0].RelativePath);
        Assert.Equal("abc123", info.Files[0].Sha256);
        Assert.Equal("2026-08-10 12:00:00", info.DisplayTime);
    }

    [Fact]
    public void SnapshotManager_Reads_LegacyDirStructure()
    {
        // 模拟旧版工具生成的快照目录结构
        string temp = Path.Combine(Path.GetTempPath(), "fb_legacy_" + Guid.NewGuid().ToString("N"));
        try
        {
            string ruleBase = Path.Combine(temp, "Rule_a1b2c3d4");
            string snapDir = Path.Combine(ruleBase, "snapshots", "20260810_120000");
            Directory.CreateDirectory(Path.Combine(snapDir, "files"));
            File.WriteAllText(Path.Combine(snapDir, "meta.json"), """
                {"snapshot_id":"s1","rule_id":"r1","created_at":"20260810_120000",
                 "source_path":"D:\\Docs","source_paths":["D:\\Docs"],
                 "backup_path":"files","file_count":2,"total_size":2048,
                 "status":"success","checksum_status":"passed"}
                """);

            var mgr = new SnapshotManager(ruleBase);
            List<string> dirs = mgr.AllSnapshotDirs();
            Assert.Single(dirs);
            SnapshotInfo? light = mgr.ReadSnapshotLight(dirs[0]);
            Assert.NotNull(light);
            Assert.Equal(2, light!.FileCount);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void SnapshotManager_Sorts_ByTimestamp()
    {
        string temp = Path.Combine(Path.GetTempPath(), "fb_sort_" + Guid.NewGuid().ToString("N"));
        try
        {
            string ruleBase = Path.Combine(temp, "Rule_x");
            foreach (string? ts in new[] { "20260810_100000", "20260810_120000", "20260810_110000" })
                Directory.CreateDirectory(Path.Combine(ruleBase, "snapshots", ts));

            var mgr = new SnapshotManager(ruleBase);
            List<string> dirs = mgr.AllSnapshotDirs();
            Assert.Equal(3, dirs.Count);
            Assert.Equal("20260810_120000", Path.GetFileName(dirs[0])); // 最新在前
            Assert.Equal("20260810_100000", Path.GetFileName(dirs[^1]));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void RuleManager_Import_Handles_LegacyExport()
    {
        string temp = Path.Combine(Path.GetTempPath(), "fb_import_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            string importFile = Path.Combine(temp, "export.json");
            // 旧版导出格式：容器对象
            File.WriteAllText(importFile, """
                {
                  "schema_version": 1,
                  "tool": "FileBackupTool",
                  "exported_at": "2026-08-10T12:00:00",
                  "rules": [
                    {"rule_id":"r1","rule_name":"规则A","source_path":"D:\\A",
                     "source_paths":["D:\\A"],"source_type":"folder",
                     "use_global_backup_root":true,"enabled":true,"max_snapshots":7}
                  ]
                }
                """);

            var mgr = new RuleManager(temp);
            (int ok, List<string>? errors) = mgr.Import(importFile);
            Assert.Equal(1, ok);
            Assert.Empty(errors);
            BackupRule imported = mgr.All.First();
            Assert.Equal("规则A", imported.RuleName);
            Assert.Equal("folder", imported.SourceType);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void RuleManager_RoundTrip_SaveAndReload()
    {
        string temp = Path.Combine(Path.GetTempPath(), "fb_roundtrip_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var mgr = new RuleManager(temp);
            var rule = new BackupRule { RuleName = "往返测试", SourcePath = "D:\\X", SourceType = SourceTypes.Folder };
            mgr.Add(rule);

            var mgr2 = new RuleManager(temp);
            Assert.Single(mgr2.All);
            Assert.Equal("往返测试", mgr2.All[0].RuleName);
            Assert.Equal("folder", mgr2.All[0].SourceType);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void RuleManager_Reorder_PersistsNewOrder()
    {
        string temp = Path.Combine(Path.GetTempPath(), "fb_reorder_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var mgr = new RuleManager(temp);
            var a = new BackupRule { RuleName = "A", SourcePath = "D:\\A", SourceType = SourceTypes.Folder };
            var b = new BackupRule { RuleName = "B", SourcePath = "D:\\B", SourceType = SourceTypes.Folder };
            var c = new BackupRule { RuleName = "C", SourcePath = "D:\\C", SourceType = SourceTypes.Folder };
            mgr.Add(a);
            mgr.Add(b);
            mgr.Add(c);

            // 把 A 移到末尾：C B A
            mgr.Reorder(new[] { c.RuleId, b.RuleId, a.RuleId });
            Assert.Equal(new[] { "C", "B", "A" }, mgr.All.Select(r => r.RuleName).ToArray());

            // 重载后顺序保持
            var mgr2 = new RuleManager(temp);
            Assert.Equal(new[] { "C", "B", "A" }, mgr2.All.Select(r => r.RuleName).ToArray());

            // 未出现在列表中的规则追加到末尾（容错）
            mgr.Reorder(new[] { a.RuleId });
            Assert.Equal(new[] { "A", "C", "B" }, mgr.All.Select(r => r.RuleName).ToArray());
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }
}
