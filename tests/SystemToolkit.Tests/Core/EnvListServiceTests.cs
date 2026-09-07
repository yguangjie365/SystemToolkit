using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// ImportCatalog 导入校验测试（S5，REVIEW-2026-08-30）。
/// 清单是外部 JSON：任何一条字段违规即整体拒绝导入，杜绝恶意条目
/// 流入 winget 参数构造或浏览器唤起。
/// </summary>
public class EnvListServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _logs = new();

    public EnvListServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "envsvc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private EnvListService CreateService()
        => new(envDir: Path.Combine(_tempDir, "env"), log: msg => _logs.Add(msg));

    private string WriteCatalog(string json)
    {
        string path = Path.Combine(_tempDir, "catalog_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ImportCatalog_InjectedId_RejectsEntireCatalog()
    {
        string path = WriteCatalog(
            """
            {
              "Winget": [
                { "Id": "--file=C:/evil.json", "Name": "恶意条目", "Source": "winget" }
              ]
            }
            """);

        EnvCatalog? result = CreateService().ImportCatalog(path);

        Assert.Null(result);
        Assert.Contains(_logs, l => l.Contains("导入清单被拒绝"));
        Assert.Contains(_logs, l => l.Contains("非法字符"));
    }

    [Fact]
    public void ImportCatalog_ValidCatalog_ImportsNormally()
    {
        string path = WriteCatalog(
            """
            {
              "Winget": [
                { "Id": "Git.Git", "Name": "Git", "Source": "winget" }
              ],
              "Manual": [
                { "Name": "手工软件", "DownloadUrl": "https://example.com/setup.exe" }
              ]
            }
            """);

        EnvCatalog? result = CreateService().ImportCatalog(path);

        Assert.NotNull(result);
        Assert.Single(result.Winget);
        Assert.Equal("Git.Git", result.Winget[0].Id);
        Assert.Single(result.Manual);
    }

    [Fact]
    public void ImportCatalog_NullCatalogContent_ReturnsNull()
    {
        string path = WriteCatalog("null");

        EnvCatalog? result = CreateService().ImportCatalog(path);

        Assert.Null(result);
        Assert.Contains(_logs, l => l.Contains("文件内容为空或不是有效的清单 JSON"));
    }
}
