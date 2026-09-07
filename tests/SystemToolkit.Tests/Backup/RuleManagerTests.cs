using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>规则管理测试（自旧工程移植，L15 英文命名）：导入（Import）的 ID 合并与同名重命名逻辑。</summary>
public class RuleManagerTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "fb_rule_" + Guid.NewGuid().ToString("N"));

    private static RuleManager NewMgr(string dir) => new(dir);

    private static string ExportFile() => Path.Combine(Path.GetTempPath(), $"fb_exp_{Guid.NewGuid():N}.json");

    private static BackupRule MakeRule(string id, string name, params string[] sources) => new()
    {
        RuleId = id,
        RuleName = name,
        SourceType = SourceTypes.Folder,
        SourcePaths = sources.ToList(),
    };

    [Fact]
    public void Import_FromContainer_AddsNewRules()
    {
        string dir = Temp();
        try
        {
            RuleManager src = NewMgr(dir + "_src");
            src.Add(MakeRule("r1", "A", "x"));
            src.Add(MakeRule("r2", "B", "y"));
            (int _, string? file) = src.Export(["r1", "r2"], ExportFile());

            RuleManager target = NewMgr(dir + "_tgt");
            (int ok, List<string>? errors) = target.Import(file);

            Assert.Equal(2, ok);
            Assert.Empty(errors);
            Assert.True(target.All.Count == 2);
        }
        finally { if (Directory.Exists(dir + "_src")) Directory.Delete(dir + "_src", recursive: true); if (Directory.Exists(dir + "_tgt")) Directory.Delete(dir + "_tgt", recursive: true); }
    }

    [Fact]
    public void Import_SameId_OverwritesExistingRule()
    {
        string dir = Temp();
        try
        {
            RuleManager src = NewMgr(dir + "_src");
            src.Add(MakeRule("r1", "updated", "x")); // 用更新后的内容导出
            (int _, string? file) = src.Export(["r1"], ExportFile());

            RuleManager target = NewMgr(dir + "_tgt");
            target.Add(MakeRule("r1", "orig", "x")); // 目标已存在同 ID 旧规则
            Assert.Equal("orig", target.Get("r1")!.RuleName);

            (int ok, List<string>? errors) = target.Import(file);

            Assert.Equal(1, ok);
            Assert.Empty(errors);
            Assert.True(target.All.Count == 1);                       // 不新增，原地覆盖
            Assert.Equal("updated", target.Get("r1")!.RuleName);     // 内容被更新
        }
        finally { if (Directory.Exists(dir + "_src")) Directory.Delete(dir + "_src", recursive: true); if (Directory.Exists(dir + "_tgt")) Directory.Delete(dir + "_tgt", recursive: true); }
    }

    [Fact]
    public void Import_DuplicateSource_RenamesWithImportSuffix()
    {
        string dir = Temp();
        try
        {
            RuleManager src = NewMgr(dir + "_src");
            src.Add(MakeRule("r2", "B", "x")); // 与已有规则同源
            (int _, string? file) = src.Export(["r2"], ExportFile());

            RuleManager target = NewMgr(dir + "_tgt");
            target.Add(MakeRule("r1", "A", "x")); // 已有规则，源路径相同

            (int ok, List<string>? errors) = target.Import(file);

            Assert.Equal(1, ok);
            Assert.True(target.All.Count == 2);
            Assert.Contains(target.All, r => r.RuleName == "B（导入）");
        }
        finally { if (Directory.Exists(dir + "_src")) Directory.Delete(dir + "_src", recursive: true); if (Directory.Exists(dir + "_tgt")) Directory.Delete(dir + "_tgt", recursive: true); }
    }

    [Fact]
    public void Import_PlainArrayFormat_IsSupported()
    {
        string dir = Temp();
        try
        {
            string file = Path.Combine(Path.GetTempPath(), "fb_arr_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file,
                "[{\"rule_id\":\"r9\",\"rule_name\":\"Arr\",\"source_paths\":[\"z\"],\"source_type\":\"folder\"}]");

            RuleManager mgr = NewMgr(dir + "_tgt");
            (int ok, List<string>? errors) = mgr.Import(file);

            Assert.Equal(1, ok);
            Assert.Empty(errors);
            Assert.NotNull(mgr.Get("r9"));
        }
        finally { if (Directory.Exists(dir + "_tgt")) Directory.Delete(dir + "_tgt", recursive: true); }
    }

    [Fact]
    public void Import_InvalidRule_ReportedAsError_NotCounted()
    {
        string dir = Temp();
        try
        {
            string file = Path.Combine(Path.GetTempPath(), "fb_bad_" + Guid.NewGuid().ToString("N") + ".json");
            // 一条合法 + 一条非法（无名称、无源路径）
            File.WriteAllText(file,
                "[{\"rule_id\":\"ok\",\"rule_name\":\"OK\",\"source_paths\":[\"z\"],\"source_type\":\"folder\"}," +
                "{\"rule_id\":\"bad\",\"source_paths\":[]}]");

            RuleManager mgr = NewMgr(dir + "_tgt");
            (int ok, List<string>? errors) = mgr.Import(file);

            Assert.Equal(1, ok);
            Assert.Single(errors);
            Assert.NotNull(mgr.Get("ok"));
            Assert.Null(mgr.Get("bad"));
        }
        finally { if (Directory.Exists(dir + "_tgt")) Directory.Delete(dir + "_tgt", recursive: true); }
    }
}
