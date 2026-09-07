using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Tests;

/// <summary>
/// PathUtil 工具方法测试。
/// 重点：P3.3 IsSafeRelativePath 新增 RemoveEmptyEntries 的连续斜杠防御——
/// 修复前 &quot;a//b/../c&quot; 会拆出空段（&quot;a&quot;,&quot;&quot;,&quot;b&quot;,&quot;..&quot;,&quot;c&quot;）被漏掉，
/// 导致路径判断为 true，但 Path.Combine 实际会把 .. 当回退（产生逃逸风险）。
/// </summary>
public class PathUtilTests
{
    /* ----------------------------------------------------------
     * 1. 正常合法相对路径（应返回 true，回归保护不收紧过度）
     * ---------------------------------------------------------- */

    [Theory]
    [InlineData("a")]
    [InlineData("a/b")]
    [InlineData("a/b/c")]
    [InlineData("a\\b\\c")]
    [InlineData("dir/file.ext")]
    [InlineData("子目录/子子目录/file.txt")]
    [InlineData("a.b.c")]
    public void IsSafeRelativePath_BenignRelativePath_ReturnsTrue(string rel)
    {
        Assert.True(PathUtil.IsSafeRelativePath(rel));
    }

    /* ----------------------------------------------------------
     * 2. 基本回退 .. —— 任何情形下都必须 false（修复前后应一致）
     * ---------------------------------------------------------- */

    [Theory]
    [InlineData("..")]
    [InlineData("../a")]
    [InlineData("a/..")]
    [InlineData("a/../b")]
    [InlineData("..\\a")]
    [InlineData("a\\..\\b")]
    public void IsSafeRelativePath_ContainsExplicitParentTraversal_ReturnsFalse(string rel)
    {
        Assert.False(PathUtil.IsSafeRelativePath(rel));
    }

    /* ----------------------------------------------------------
     * 3. P3.3 新增守卫：连续斜杠叠加回退 —— 修复前会误判为 true
     * ---------------------------------------------------------- */

    [Theory]
    [InlineData("a//b/../c")]
    [InlineData("a\\b\\..\\c")]
    [InlineData("a///..//b")]
    [InlineData("..//a")]
    [InlineData("a//..")]
    [InlineData("a\\\\..\\\\b")]
    public void IsSafeRelativePath_ConsecutiveSlashesWithTraversal_ReturnsFalse(string rel)
    {
        Assert.False(PathUtil.IsSafeRelativePath(rel));
    }

    /* ----------------------------------------------------------
     * 4. 其他非法路径形态（绝对 / UNC / 盘符 / 空）
     * ---------------------------------------------------------- */

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("/a")]
    [InlineData("\\a")]
    [InlineData("C:a")]
    [InlineData("C:\\a")]
    [InlineData("\\\\server\\share")]
    public void IsSafeRelativePath_EmptyOrRootedOrDriveLetter_ReturnsFalse(string? rel)
    {
        Assert.False(PathUtil.IsSafeRelativePath(rel!));
    }

    /* ----------------------------------------------------------
     * 5. IsUnder 目录从属判定（路径规范化 + 大小写不敏感）
     * ---------------------------------------------------------- */

    [Fact]
    public void IsUnder_ChildDirectory_ReturnsTrue()
    {
        string root = @"C:\Backup";
        string child = @"C:\Backup\2026\08";
        Assert.True(PathUtil.IsUnder(child, root));
    }

    [Fact]
    public void IsUnder_DifferentRootDirectory_ReturnsFalse()
    {
        Assert.False(PathUtil.IsUnder(@"C:\Other\file", @"C:\Backup"));
    }

    [Fact]
    public void IsUnder_IdenticalPaths_ReturnsTrue() // 结尾统一追加 DirectorySeparatorChar 后再 StartsWith，等价路径视为 under
    {
        Assert.True(PathUtil.IsUnder(@"C:\Backup", @"C:\Backup"));
    }

    [Fact]
    public void IsUnderAny_MatchesOneOfTheRoots_ReturnsTrue()
    {
        string[] roots = new[] { @"C:\Docs", @"D:\Backup", @"E:\Photo" };
        Assert.True(PathUtil.IsUnderAny(@"D:\Backup\2026\01\a.zip", roots));
    }

    [Fact]
    public void IsUnderAny_TargetEqualsARoot_ReturnsTrue() // IsUnderAny 对相等情况做了二次判定
    {
        string[] roots = new[] { @"C:\Docs", @"D:\Backup" };
        Assert.True(PathUtil.IsUnderAny(@"D:\Backup", roots));
    }
}
