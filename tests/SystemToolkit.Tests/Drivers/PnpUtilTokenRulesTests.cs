using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// pnputil 提权令牌白名单守卫（05 安全设计：进入提权命令行的值必须白名单校验）。
/// </summary>
public class PnpUtilTokenRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stk_tok_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("oem1.inf")]
    [InlineData("OEM12.INF")]
    [InlineData("nvlt.inf")]
    public void IsValidInfFileName_ValidName_Passes(string token)
    {
        Assert.True(PnpUtilTokenRules.IsValidInfFileName(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../evil.inf")]
    [InlineData("sub/oem1.inf")]
    [InlineData("sub\\oem1.inf")]
    [InlineData("oem1.inf & whoami")]
    [InlineData("oem1.inf\n")]
    [InlineData("oem 1.inf")] // 含空格
    [InlineData("oem1.dll")]
    public void IsValidInfFileName_InvalidName_Rejected(string? token)
    {
        Assert.False(PnpUtilTokenRules.IsValidInfFileName(token));
    }

    [Theory]
    [InlineData("oem1.inf")]
    [InlineData("oem99999.inf")]
    public void IsDeletablePublishedName_OnlyAcceptsNumberedOemPackages(string token)
    {
        Assert.True(PnpUtilTokenRules.IsDeletablePublishedName(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("inbox.inf")]      // 收件箱不可删
    [InlineData("oem.inf")]        // 无编号
    [InlineData("oem123456.inf")]  // 编号超 5 位
    [InlineData("../oem1.inf")]    // 路径穿越
    [InlineData("oem1.inf /force extra")] // 注入尝试
    [InlineData("oem1.inf\n")]     // 尾换行（M7：\z 锚定）
    public void IsDeletablePublishedName_InvalidTarget_Rejected(string? token)
    {
        Assert.False(PnpUtilTokenRules.IsDeletablePublishedName(token));
    }

    [Fact]
    public void IsValidInfPathForAdd_ExistingRootedInfPath_Passes()
    {
        Directory.CreateDirectory(_dir);
        string inf = Path.Combine(_dir, "driver.inf");
        File.WriteAllText(inf, "[Version]");

        Assert.True(PnpUtilTokenRules.IsValidInfPathForAdd(inf));
        Assert.True(PnpUtilTokenRules.IsValidInfPathForAdd(inf.ToUpperInvariant()));
    }

    [Fact]
    public void IsValidInfPathForAdd_RelativeOrMissingOrNonInf_Rejected()
    {
        Directory.CreateDirectory(_dir);
        string inf = Path.Combine(_dir, "driver.inf");
        File.WriteAllText(inf, "[Version]");

        Assert.False(PnpUtilTokenRules.IsValidInfPathForAdd("driver.inf")); // 相对路径
        Assert.False(PnpUtilTokenRules.IsValidInfPathForAdd(Path.Combine(_dir, "missing.inf")));
        Assert.False(PnpUtilTokenRules.IsValidInfPathForAdd(Path.Combine(_dir, "driver.txt")));
        File.WriteAllText(Path.Combine(_dir, "readme.inf"), "x");
        _ = Path.Combine(_dir, "readme.inf");
        Assert.False(PnpUtilTokenRules.IsValidInfPathForAdd(null));
    }
}
