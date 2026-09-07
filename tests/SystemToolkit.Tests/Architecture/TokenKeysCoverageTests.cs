using System.Text.RegularExpressions;
using SystemToolkit.UI.Common.Themes.Tokens;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 令牌契约覆盖守卫（P1 落地）：主题包必须覆盖 TokenKeys.AllKeys 全部 key，缺一即红。
/// TokenKeys 与 Claude.Light.xaml 由人工同步维护（生成自 x:Key 清单）——
/// 新增令牌时两处必须一起加；主题包删 key 而不删契约 → 红。
/// </summary>
public class TokenKeysCoverageTests
{
    private const string ThemePackPath = "src/SystemToolkit.UI.Common/Themes/Packs/Claude/Claude.Light.xaml";

    [Fact]
    public void TokenGuard_ThemePack_MustCoverFullTokenKeysContract()
    {
        string xaml = File.ReadAllText(RepoRoot() + "/" + ThemePackPath);
        var defined = Regex.Matches(xaml, @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = TokenKeys.AllKeys.Where(k => !defined.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            "主题包缺少以下令牌（TokenKeys 契约要求全量覆盖）：" + string.Join(", ", missing));
    }

    [Fact]
    public void TokenGuard_Contract_MustNotContainGhostKeys()
    {
        // 反向约束：TokenKeys 里的 key 必须真实存在于主题包，防止契约与字典漂移成两张皮
        string xaml = File.ReadAllText(RepoRoot() + "/" + ThemePackPath);
        var defined = Regex.Matches(xaml, @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var ghosts = defined
            .Where(k => !TokenKeys.AllKeys.Contains(k))
            .ToList();
        Assert.True(ghosts.Count == 0,
            "主题包存在未纳入契约的令牌（请同步 TokenKeys.cs）：" + string.Join(", ", ghosts));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根（SystemToolkit.sln）");
    }
}
