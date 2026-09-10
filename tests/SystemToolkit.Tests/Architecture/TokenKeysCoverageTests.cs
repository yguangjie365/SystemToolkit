using System.Text.RegularExpressions;
using SystemToolkit.UI.Common.Themes.Tokens;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 令牌契约覆盖守卫（P1 落地；ADR-005 扩展为遍历全部主题包）：
/// <b>每个</b>主题包（Packs/**/*.xaml）都必须覆盖 TokenKeys.AllKeys 全部 key，缺一即红；
/// 反向约束：任何包里的 key 必须真实存在于契约（防漂移成两张皮）。
/// TokenKeys 与各主题包由人工同步维护——新增令牌时所有包必须一起加；新增主题包自动纳入扫描。
/// </summary>
public class TokenKeysCoverageTests
{
    private static IEnumerable<string> ThemePackPaths()
    {
        string packsDir = Path.Combine(RepoRoot(), "src/SystemToolkit.UI.Common/Themes/Packs");
        return Directory.EnumerateFiles(packsDir, "*.xaml", SearchOption.AllDirectories);
    }

    [Fact]
    public void TokenGuard_EveryThemePack_MustCoverFullTokenKeysContract()
    {
        var failures = new List<string>();
        foreach (string pack in ThemePackPaths())
        {
            var defined = KeysOf(pack);
            var missing = TokenKeys.AllKeys.Where(k => !defined.Contains(k)).ToList();
            if (missing.Count > 0)
            {
                failures.Add($"{Path.GetFileName(pack)} 缺少：{string.Join(", ", missing)}");
            }
        }

        Assert.True(failures.Count == 0,
            "主题包缺少以下令牌（TokenKeys 契约要求全量覆盖）：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TokenGuard_EveryThemePack_MustNotContainGhostKeys()
    {
        // 反向约束：包里的 key 必须真实存在于契约，防止契约与字典漂移
        var failures = new List<string>();
        foreach (string pack in ThemePackPaths())
        {
            var defined = KeysOf(pack);
            var ghosts = defined
                .Where(k => !TokenKeys.AllKeys.Contains(k))
                .ToList();
            if (ghosts.Count > 0)
            {
                failures.Add($"{Path.GetFileName(pack)} 幽灵键：{string.Join(", ", ghosts)}");
            }
        }

        Assert.True(failures.Count == 0,
            "主题包存在未纳入契约的令牌（请同步 TokenKeys.cs）：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static HashSet<string> KeysOf(string packPath)
        => Regex.Matches(File.ReadAllText(packPath), @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

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
