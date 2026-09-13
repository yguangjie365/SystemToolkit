using System.Text.Json;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using Xunit;

namespace SystemToolkit.Tests;

/// <summary>
/// 清单导入/导出的格式判别与落盘行为 —— 落地计划 B4-④。
/// 关键行为：winget 官方格式**只有包 Id**，导入时不得因此清空手工/驱动清单
/// （那等于用文件没说的东西删数据）；旧自研格式仍可**只读导入**（已裁定的迁移窗口）。
/// </summary>
public sealed class EnvManifestImportTests : IDisposable
{
    private readonly string _dir;

    private readonly EnvListService _env;

    public EnvManifestImportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stk-envmanifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _env = new EnvListService(_dir, Path.Combine(_dir, "drivers"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    [Fact]
    public void ExportWingetManifest_WritesWingetSchemaFile()
    {
        _env.SaveWinget(new[]
        {
            new WingetPackage { Id = "voidtools.Everything", Name = "Everything" },
            new WingetPackage { Id = "XP9KHM4BK9FZ7Q", Name = "VS Code", Source = "msstore" },
        });

        string path = Path.Combine(_dir, "out.json");
        int count = _env.ExportWingetManifest(path);

        Assert.Equal(2, count);
        string json = File.ReadAllText(path);
        Assert.True(WingetExportManifest.LooksLikeWingetManifest(json));
        Assert.True(WingetExportManifest.TryParse(json, out WingetExportManifest? manifest, out List<string> errors));
        Assert.Empty(errors);
        Assert.Equal(2, manifest!.PackageCount());

        // 导出文件**不含** EnvCatalog 的自研字段（格式已切换，不是两套并存）
        Assert.DoesNotContain("\"winget\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"manual\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportManifest_WingetFormat_ReturnsWingetOnly()
    {
        const string sample = """
            {
              "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
              "CreationDate": "2026-09-13T15:44:31.263-00:00",
              "Sources": [
                { "SourceDetails": { "Argument": "https://a", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name": "winget", "Type": "Microsoft.PreIndexed.Package" },
                  "Packages": [ { "PackageIdentifier": "voidtools.Everything" } ] },
                { "SourceDetails": { "Argument": "https://b", "Identifier": "StoreEdgeFD", "Name": "msstore", "Type": "Microsoft.Rest" },
                  "Packages": [ { "PackageIdentifier": "XP9KHM4BK9FZ7Q" } ] }
              ],
              "WinGetVersion": "1.29.290"
            }
            """;
        string path = Path.Combine(_dir, "in.json");
        File.WriteAllText(path, sample);

        EnvManifestImport import = _env.ImportManifest(path);

        Assert.True(import.Success);
        Assert.Equal(EnvManifestFormat.WingetExport, import.Format);
        Assert.Equal(new[] { "voidtools.Everything", "XP9KHM4BK9FZ7Q" }, import.Catalog.Winget.Select(p => p.Id).ToArray());
        Assert.True(import.Catalog.Winget[1].IsMsStore);

        // 该格式没有手工/驱动条目 → 回传空列表，调用方据此**保留**原清单（不是清空）
        Assert.Empty(import.Catalog.Manual);
        Assert.Empty(import.Catalog.Driver);
    }

    [Fact]
    public void ImportManifest_LegacyFormat_ReturnsAllThreeLists()
    {
        var manual = new List<ManualSoftware> { new ManualSoftware { Name = "某手动软件" } };
        var driver = new List<ManualSoftware> { new ManualSoftware { Name = "某驱动工具" } };
        _env.SaveManual(manual);
        _env.SaveDriver(driver);
        _env.SaveWinget(new[] { new WingetPackage { Id = "voidtools.Everything", Name = "Everything" } });

        string path = Path.Combine(_dir, "legacy.json");
        _env.ExportCatalog(path);

        EnvManifestImport import = _env.ImportManifest(path);

        Assert.True(import.Success);
        Assert.Equal(EnvManifestFormat.LegacyCatalog, import.Format);
        Assert.Single(import.Catalog.Winget);
        Assert.Single(import.Catalog.Manual);
        Assert.Single(import.Catalog.Driver);
    }

    [Fact]
    public void ImportManifest_WingetFormatWithIllegalId_IsRejected()
    {
        // 外部 JSON 的 Id 会流入 winget 参数构造 → 必须与旧格式导入同一份校验
        const string sample = """
            {
              "Sources": [
                { "SourceDetails": { "Argument": "https://a", "Identifier": "x", "Name": "winget", "Type": "t" },
                  "Packages": [ { "PackageIdentifier": "--source" } ] }
              ]
            }
            """;
        string path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, sample);

        EnvManifestImport import = _env.ImportManifest(path);

        Assert.False(import.Success);
        Assert.NotEmpty(import.Errors);
    }

    [Fact]
    public void ImportManifest_MissingFile_FailsWithoutThrowing()
    {
        EnvManifestImport import = _env.ImportManifest(Path.Combine(_dir, "nope.json"));

        Assert.False(import.Success);
        Assert.NotEmpty(import.Errors);
    }

    [Fact]
    public void ImportManifest_UnrelatedJson_Fails()
    {
        // 🔴 这条断言钉的是"**不得静默清空**"：EnvCatalog 三个属性都有默认空值，
        // 任意 JSON 对象都能被反序列化成空清单而不报错 —— 放过它 = 用户清单被清空却零提示
        string path = Path.Combine(_dir, "other.json");
        File.WriteAllText(path, """{ "hello": "world" }""");

        EnvManifestImport import = _env.ImportManifest(path);

        Assert.False(import.Success);
        Assert.NotEmpty(import.Errors);
    }

    [Theory]
    [InlineData("""{ "winget": [] }""", true)]
    [InlineData("""{ "Manual": [] }""", true)]
    [InlineData("""{ "driver": [] }""", true)]
    [InlineData("""{ "hello": "world" }""", false)]
    [InlineData("""[1, 2, 3]""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void LooksLikeLegacyCatalog_RequiresKnownKeys(string json, bool expected) =>
        Assert.Equal(expected, EnvListService.LooksLikeLegacyCatalog(json));

    [Fact]
    public void ExportWingetManifest_EmptyList_WritesSchemaValidEmptySources()
    {
        // Sources: [] 经 winget import 实测是**合法**的（返回 NO_APPLICATIONS_FOUND，非格式错）
        string path = Path.Combine(_dir, "empty.json");
        int count = _env.ExportWingetManifest(path);

        Assert.Equal(0, count);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("Sources").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("Sources").EnumerateArray());
    }
}
