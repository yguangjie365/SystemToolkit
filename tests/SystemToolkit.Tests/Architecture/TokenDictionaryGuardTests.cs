using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 令牌字典资源完整性守卫。
/// 来源事故（2026-09-04 16:08）：视图 XAML 引用 Brush_SegWarn，但补丁脚本中断导致
/// 令牌字典从未写入该键 → 启动即 XamlParseException，进程存活但窗口永不出现。
/// 本守卫保证：src 下任何视图引用的静态资源键，必须已在 Claude.Light.xaml 中定义。
/// </summary>
public class TokenDictionaryGuardTests
{
    private static string RepoRoot => FindRepoRoot();

    private static readonly string[] TokenDictCandidates =
    [
        Path.Combine("src", "SystemToolkit.UI.Common", "Themes", "Packs", "Claude", "Claude.Light.xaml"),
    ];

    /// <summary>视图允许引用的资源键前缀（字体族 / 颜色 / 画刷）。</summary>
    private static readonly Regex ReferencedKeys = new(
        @"\{StaticResource (?<key>(?:Brush|Color|Font)_[A-Za-z0-9_]+)\}",
        RegexOptions.Compiled);

    [Fact]
    public void TokenGuard_StaticResourceKeysReferencedByViews_MustBeDefinedInTokenDictionary()
    {
        string dictPath = TokenDictCandidates
            .Select(p => Path.Combine(RepoRoot, p))
            .First(File.Exists);
        string dictText = File.ReadAllText(dictPath);

        var defined = new HashSet<string>();
        foreach (Match m in Regex.Matches(dictText, @"x:Key=""(?<key>[A-Za-z0-9_]+)"""))
        {
            defined.Add(m.Groups["key"].Value);
        }

        Assert.NotEmpty(defined);

        // 扫描所有模块视图与 UI.Common 自身的 XAML（排除令牌字典本身）
        var missing = new List<string>();
        IEnumerable<string> xamlFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("Claude.Light.xaml", StringComparison.OrdinalIgnoreCase));

        foreach (string file in xamlFiles)
        {
            string text = File.ReadAllText(file);
            foreach (Match m in ReferencedKeys.Matches(text))
            {
                string key = m.Groups["key"].Value;
                if (!defined.Contains(key))
                {
                    missing.Add($"{Path.GetFileName(file)} → {key}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "以下 XAML 引用的资源键未在 Claude.Light.xaml 定义（启动会抛 XamlParseException）：" +
            string.Join("; ", missing));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根");
    }
}
