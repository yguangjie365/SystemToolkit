using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 规范↔主题包同步守卫（2026-09-06 UI 审计 A1/A3/A5 沉淀）：主题包里每个 Color_* 令牌
/// 必须在 04 规范 §3.1 色值表中以同名 key + 同值 hex 出现——改值不改文档即红。
/// 反向验证：把规范中任一 Color 值改错 → 红 → 恢复 → 绿（已演练）。
/// </summary>
public class DesignSpecTokenSyncTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("未找到仓库根");
    }

    [Fact]
    public void SpecColorTable_MatchesThemeValues_FieldByField()
    {
        string theme = File.ReadAllText(Path.Combine(RepoRoot(),
            "src/SystemToolkit.UI.Common/Themes/Packs/Claude/Claude.Light.xaml"));
        string spec = File.ReadAllText(Path.Combine(RepoRoot(), "Docs/04-UI设计规范.md")).ToLowerInvariant();
        string[] specLines = spec.Split('\n');

        var failures = new List<string>();
        foreach (Match m in Regex.Matches(theme, "<Color x:Key=\"(Color_[A-Za-z]+)\">(#?[0-9A-Fa-f]{6,8})</Color>"))
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string hex = m.Groups[2].Value.ToLowerInvariant().TrimStart('#');
            bool listed = specLines.Any(line =>
                line.Contains(key, StringComparison.Ordinal) && line.Contains(hex, StringComparison.Ordinal));
            if (!listed)
            {
                failures.Add($"{m.Groups[1].Value}={m.Groups[2].Value} 未在 04 规范 §3.1 以同值登记");
            }
        }

        Assert.True(failures.Count == 0,
            "主题包色值与 04 规范漂移（先加 CHANGELOG 条目再改代码）：" + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Brush_* hex 同步守卫（REVIEW-3 沉淀：Color_* 守卫只覆盖 <c>&lt;Color&gt;</c> 节点，
    /// 主题里 Brush 的低透明度 Soft/Border 值与渐变笔刷此前可脱离规范漂移——
    /// 实证：SegWarn/OnDarkMuted/NavySoft/DangerSoft 四处旧值漂移至 M-UI-2 才发现）。
    /// 主题中每个 SolidColorBrush/LinearGradientBrush 的显式 Color hex（不经 Color_* 引用的）
    /// 必须在 04 规范 §3.1 以同值出现。
    /// </summary>
    [Fact]
    public void SpecBrushHexTable_MatchesThemeLiterals()
    {
        string theme = File.ReadAllText(Path.Combine(RepoRoot(),
            "src/SystemToolkit.UI.Common/Themes/Packs/Claude/Claude.Light.xaml"));
        string spec = File.ReadAllText(Path.Combine(RepoRoot(), "Docs/04-UI设计规范.md")).ToLowerInvariant();
        string[] specLines = spec.Split('\n');

        // 抓主题中所有「显式 hex」笔刷：SolidColorBrush/GradientStop 的 Color="#..." 字面量
        // （引用 Color_* 的节点不是字面量，由上一个 Color 守卫覆盖）
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(theme, @"<(?:SolidColorBrush|GradientStop)\b[^>]*?Color=""(#?[0-9A-Fa-f]{6,8})"""))
        {
            string hex = m.Groups[1].Value.ToLowerInvariant().TrimStart('#');
            if (hex == "00000000")
            {
                continue; // 全透明占位色（渐变起点）无视觉语义，豁免
            }

            if (seen.Add(hex) && !specLines.Any(l => l.Contains(hex, StringComparison.Ordinal)))
            {
                failures.Add($"主题笔刷 hex #{hex} 未在 04 规范 §3.1 登记");
            }
        }

        Assert.True(failures.Count == 0,
            "主题包 Brush 显式 hex 与 04 规范漂移（先加 CHANGELOG 条目再改代码）：\n"
            + string.Join(Environment.NewLine, failures));
    }
}
