using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests.Backup;

/// <summary>
/// 排除规则匹配（2026-09-07 新增能力，旧版无）：目录名/文件名段匹配 + 多段路径前缀匹配。
/// </summary>
public class ExcludePatternTests
{
    [Fact]
    public void NoPatterns_NothingExcluded()
        => Assert.False(DirectoryScanner.IsExcluded("a/node_modules/b.txt", null));

    [Fact]
    public void DirectoryNameSegment_MatchesAnyDepth()
    {
        string[] patterns = new[] { "node_modules" };
        Assert.True(DirectoryScanner.IsExcluded("node_modules/pkg/index.js", patterns));
        Assert.True(DirectoryScanner.IsExcluded("src/node_modules/pkg/index.js", patterns));
        Assert.False(DirectoryScanner.IsExcluded("src/index.js", patterns));
    }

    [Fact]
    public void WildcardExtension_MatchesFileName()
    {
        string[] patterns = new[] { "*.tmp" };
        Assert.True(DirectoryScanner.IsExcluded("cache/session.tmp", patterns));
        Assert.False(DirectoryScanner.IsExcluded("cache/session.log", patterns));
    }

    [Fact]
    public void MultiSegmentPattern_MatchesPathPrefix()
    {
        string[] patterns = new[] { "bin/Debug" };
        Assert.True(DirectoryScanner.IsExcluded("bin/Debug/app.dll", patterns));
        Assert.False(DirectoryScanner.IsExcluded("bin/Release/app.dll", patterns));
    }

    [Fact]
    public void CaseInsensitive_AndBackslashNormalized()
    {
        string[] patterns = new[] { ".GIT" };
        Assert.True(DirectoryScanner.IsExcluded(".git/config", patterns));
        Assert.True(DirectoryScanner.IsExcluded(@"src\.git\HEAD", patterns));
    }

    [Fact]
    public void EmptyOrBlankPatterns_Ignored()
        => Assert.False(DirectoryScanner.IsExcluded("a/b.txt", new[] { "", "   " }));
}
