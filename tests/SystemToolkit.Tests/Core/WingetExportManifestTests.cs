using System.Text.Json;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using Xunit;

namespace SystemToolkit.Tests;

/// <summary>
/// winget 官方导出清单（schema 2.0）的读写锁 —— 落地计划 B4-④。
/// <para>
/// 夹具形态取自**真实产物**（2026-09-13 本机 winget v1.29.290：<c>winget export -o</c>），
/// 包名为真实值、条数为节选。schema 硬约束（<c>WinGetVersion</c> 不可为空、
/// <c>SourceDetails.Identifier</c> 必填、<c>Packages</c> 至少 1 条）是把候选文件交给
/// <c>winget import</c> 试出来的，不是按文档推断的。
/// </para>
/// </summary>
public sealed class WingetExportManifestTests
{
    /// <summary>真实 winget 导出产物（结构完整，包名为真实值，条数为节选）。</summary>
    private const string RealExportSample = """
        {
          "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
          "CreationDate": "2026-09-13T15:44:31.263-00:00",
          "Sources": [
            {
              "SourceDetails": {
                "Argument": "https://mirrors.ustc.edu.cn/winget-source",
                "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe",
                "Name": "winget",
                "Type": "Microsoft.PreIndexed.Package"
              },
              "Packages": [
                { "PackageIdentifier": "chen08209.FlClash" },
                { "PackageIdentifier": "Bandisoft.Bandizip" },
                { "PackageIdentifier": "voidtools.Everything" }
              ]
            }
          ],
          "WinGetVersion": "1.29.290"
        }
        """;

    /// <summary>含两个源的样本（winget + msstore）。</summary>
    private const string TwoSourceSample = """
        {
          "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
          "CreationDate": "2026-09-13T15:44:31.263-00:00",
          "Sources": [
            {
              "SourceDetails": { "Argument": "https://a", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name": "winget", "Type": "Microsoft.PreIndexed.Package" },
              "Packages": [ { "PackageIdentifier": "voidtools.Everything" } ]
            },
            {
              "SourceDetails": { "Argument": "https://b", "Identifier": "StoreEdgeFD", "Name": "msstore", "Type": "Microsoft.Rest" },
              "Packages": [ { "PackageIdentifier": "XP9KHM4BK9FZ7Q" } ]
            }
          ],
          "WinGetVersion": "1.29.290"
        }
        """;

    [Fact]
    public void Parse_RealExportSample_ReadsIdsInSourceOrder()
    {
        Assert.True(WingetExportManifest.TryParse(RealExportSample, out WingetExportManifest? manifest, out List<string> errors));
        Assert.Empty(errors);

        List<WingetPackage> packages = manifest!.ToPackages();

        Assert.Equal(3, manifest.PackageCount());
        Assert.Equal(
            new[] { "chen08209.FlClash", "Bandisoft.Bandizip", "voidtools.Everything" },
            packages.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Parse_MsStoreSource_MapsToMsStoreFlag()
    {
        Assert.True(WingetExportManifest.TryParse(TwoSourceSample, out WingetExportManifest? manifest, out List<string> errors));
        Assert.Empty(errors);

        List<WingetPackage> packages = manifest!.ToPackages();

        Assert.False(packages[0].IsMsStore);
        Assert.True(packages[1].IsMsStore);
    }

    [Fact]
    public void Parse_EntryWithoutPackageIdentifier_RejectsWholeFile()
    {
        const string broken = """
            { "Sources": [ { "SourceDetails": { "Name": "winget" },
              "Packages": [ { "Version": "1.0" } ] } ] }
            """;

        Assert.False(WingetExportManifest.TryParse(broken, out _, out List<string> errors));
        Assert.Contains(errors, e => e.Contains("PackageIdentifier"));
    }

    [Fact]
    public void Parse_MissingSources_RejectsFile()
    {
        Assert.False(WingetExportManifest.TryParse("""{ "WinGetVersion": "1.2.3" }""", out _, out List<string> errors));
        Assert.Contains(errors, e => e.Contains("Sources"));
    }

    [Theory]
    [InlineData("""{ "Sources": [] }""", true)]
    [InlineData("""{ "Winget": [ { "id": "a" } ] }""", false)]
    [InlineData("not json at all", false)]
    [InlineData("", false)]
    public void LooksLikeWingetManifest_OnlyAcceptsSourcesShape(string json, bool expected) =>
        Assert.Equal(expected, WingetExportManifest.LooksLikeWingetManifest(json));

    [Fact]
    public void Build_OmitsWinGetVersionAndWritesIdentifier()
    {
        // 🔴 实测约束：WinGetVersion 出现则必须匹配 pattern，空串会被 winget 拒绝；
        // SourceDetails.Identifier 必填（缺则 winget 报 "Missing required property 'Identifier'"）
        var manifest = WingetExportManifest.FromPackages(new[]
        {
            new WingetPackage { Id = "voidtools.Everything", Source = "winget" },
        });

        using var doc = JsonDocument.Parse(Serialize(manifest));

        Assert.False(doc.RootElement.TryGetProperty("WinGetVersion", out _));

        JsonElement details = doc.RootElement.GetProperty("Sources")[0].GetProperty("SourceDetails");
        Assert.Equal("Microsoft.Winget.Source_8wekyb3d8bbwe", details.GetProperty("Identifier").GetString());
        Assert.Equal("winget", details.GetProperty("Name").GetString());
    }

    [Fact]
    public void Build_GroupsBySourceAndKeepsInputOrder()
    {
        // 刻意不做字母重排：排序会替上游做取舍（OUI 生成器踩过的同型问题）
        var manifest = WingetExportManifest.FromPackages(new[]
        {
            new WingetPackage { Id = "voidtools.Everything", Source = "winget" },
            new WingetPackage { Id = "XP9KHM4BK9FZ7Q", Source = "msstore" },
            new WingetPackage { Id = "Bandisoft.Bandizip", Source = "winget" },
        });

        Assert.Equal(2, manifest.Sources.Count);
        Assert.Equal("winget", manifest.Sources[0].SourceDetails.Name);
        Assert.Equal(new[] { "voidtools.Everything", "Bandisoft.Bandizip" },
            manifest.Sources[0].Packages.Select(p => p.PackageIdentifier).ToArray());
        Assert.Equal("msstore", manifest.Sources[1].SourceDetails.Name);
        Assert.Equal("StoreEdgeFD", manifest.Sources[1].SourceDetails.Identifier);
    }

    [Fact]
    public void PruneForWrite_DropsEmptySource()
    {
        // 🔴 实测约束：Packages 空数组即 schema 失败（"Array should contain no fewer than 1 elements"）
        var manifest = new WingetExportManifest
        {
            Sources =
            {
                new WingetExportSource
                {
                    SourceDetails = WingetExportSourceDetails.ForKnownSource("winget"),
                    Packages = { new WingetExportPackageRef { PackageIdentifier = "a.b" } },
                },
                new WingetExportSource { SourceDetails = WingetExportSourceDetails.ForKnownSource("msstore") },
            },
        };

        manifest.PruneForWrite();

        Assert.Single(manifest.Sources);
        Assert.Equal(1, manifest.PackageCount());
    }

    [Fact]
    public void RoundTrip_PreservesOrderAndDedupes()
    {
        var written = WingetExportManifest.FromPackages(new[]
        {
            new WingetPackage { Id = "a.a", Source = "winget" },
            new WingetPackage { Id = "a.a", Source = "winget" },
            new WingetPackage { Id = "b.b", Source = "winget" },
        });
        written.PruneForWrite();

        Assert.True(WingetExportManifest.TryParse(Serialize(written), out WingetExportManifest? read, out List<string> errors));
        Assert.Empty(errors);
        // 写出侧允许重复；读入侧按「源 + Id」去重且保留首次出现顺序
        Assert.Equal(new[] { "a.a", "b.b" }, read!.ToPackages().Select(p => p.Id).ToArray());
    }

    [Fact]
    public void ValidateWingetPackages_RejectsDashLeadingId()
    {
        // 导入的 Id 会流入 winget 参数构造 —— 与旧格式导入共用同一份判据
        List<string> errors = EnvCatalogValidator.ValidateWingetPackages(new[]
        {
            new WingetPackage { Id = "-o", Name = "-o" },
        });

        Assert.Contains(errors, e => e.Contains("非法字符"));
    }

    [Fact]
    public void BuildManifestArgs_ExportShape_IsPinned()
    {
        List<string> args = WingetService.BuildManifestArgs("export", @"D:\out\winget.json");

        Assert.Equal(
            new[] { "export", "--output", @"D:\out\winget.json", "--accept-source-agreements", "--disable-interactivity" },
            args.ToArray());
    }

    [Theory]
    [InlineData("-o")]
    [InlineData("-")]
    public void BuildManifestArgs_RejectsOptionLikePath(string path) =>
        Assert.Throws<ArgumentException>(() => WingetService.BuildManifestArgs("export", path));

    private static string Serialize(WingetExportManifest manifest)
    {
        // 与 EnvListService.SaveJson 同款选项（缩进 + 显式字段名走 [JsonPropertyName]）
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        return JsonSerializer.Serialize(manifest, options);
    }
}
