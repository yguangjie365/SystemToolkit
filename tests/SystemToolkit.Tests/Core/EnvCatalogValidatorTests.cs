using SystemToolkit.Core.Software.Models;

namespace SystemToolkit.Tests;

/// <summary>
/// M7 回归（全面代码审查 2026-09-05）：导入校验正则 \z 锚定——带尾换行的脏数据
/// 必须在导入即拒绝，而不是推迟到操作时（S5/S6 防线的完整性）。
/// </summary>
public class EnvCatalogValidatorTests
{
    [Fact]
    public void PackageId_TrailingNewline_Rejected()
    {
        Assert.DoesNotMatch(EnvCatalogValidator.PackageIdPattern, "Microsoft.VisualStudioCode\n");
    }

    [Fact]
    public void HttpUrl_TrailingNewline_Rejected()
    {
        Assert.False(EnvCatalogValidator.IsValidHttpUrl("https://example.com/a.zip\n"));
    }

    [Fact]
    public void PackageId_NormalValue_Passes()
    {
        Assert.Matches(EnvCatalogValidator.PackageIdPattern, "Microsoft.VisualStudioCode");
        Assert.Matches(EnvCatalogValidator.PackageIdPattern, "msstore");
        Assert.True(EnvCatalogValidator.IsValidHttpUrl("https://example.com/a.zip"));
    }

    [Fact]
    public void PackageId_StartingWithHyphen_Rejected()
    {
        Assert.DoesNotMatch(EnvCatalogValidator.PackageIdPattern, "-evil");
    }
}
