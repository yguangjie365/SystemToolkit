using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// C# 端 UI 画笔字面量守卫（2026-09-08 建立，第三方审查 S-1/S-2 的落地防线）。
///
/// <b>背景</b>：项目已有 XAML 侧令牌守卫（UiTokenRatchet / TokenDictionaryGuard /
/// DesignSpecTokenSync），但 C# 侧完全失守——<c>Color.FromRgb(0x04,0x78,0x57)</c>、
/// <c>VssStatusColor = "#DC2626"</c> 这类硬编码让颜色脱离主题机制（换肤/主题迭代不跟随），
/// 正是旧工程 428 处裸值事故的 C# 版。
///
/// <b>规则</b>：C# 中出现 <c>Color.FromRgb/FromArgb/FromHex</c>、<c>new SolidColorBrush(</c>
/// 或「*Color*/*Brush* = "#RRGGBB"」时，必须走主题取值
/// （<c>ThemeBrush.Find(...)</c> 或 <c>TryFindResource(...)</c>），否则构建失败。
/// 主题基础设施自身（UI.Common/Themes、ThemeBrush.cs）豁免。
/// </summary>
public class CsBrushLiteralGuardTests
{
    private static readonly Regex BrushCtor = new(
        @"Color\.FromRgb\s*\(|Color\.FromArgb\s*\(|Color\.FromHex\s*\(|new\s+SolidColorBrush\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex HexColorAssignment = new(
        @"""(?i:Color|Brush)\w*\s*=\s*""#[0-9A-Fa-f]{6}""|\b\w*[Cc]olor\w*\s*=\s*""#[0-9A-Fa-f]{6}""",
        RegexOptions.Compiled);

    private static readonly Regex ThemeLookup = new(
        @"ThemeBrush\.Find|TryFindResource",
        RegexOptions.Compiled);

    /// <summary>允许出现画笔字面量的文件（主题基础设施自身，回退色值即事实来源）。</summary>
    private static readonly string[] AllowedFiles =
    {
        "ThemeBrush.cs",

        // 封面主色工厂（OM-6）：运行时色——每首封面主色不同，无法用静态设计令牌表达；
        // 全仓唯一的 Color.FromRgb 字面量收敛点（见文件头注释）。
        "CoverColorFactory.cs",
    };

    [Fact]
    public void CSharpBrushLiterals_MustGoThroughThemeLookup()
    {
        var violations = new List<string>();
        foreach (string file in SourceFiles())
        {
            string name = Path.GetFileName(file);
            if (AllowedFiles.Contains(name) || name.Contains("Theme", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool isBrushLiteral = BrushCtor.IsMatch(line) || HexColorAssignment.IsMatch(line);
                if (!isBrushLiteral || ThemeLookup.IsMatch(line))
                {
                    continue;
                }

                violations.Add(
                    $"{Relative(file)}:{i + 1}  {line.Trim()}\n    → 改用 ThemeBrush.Find(\"Brush_Xxx\", \"#RRGGBB\")" +
                    "（先取主题资源，失败才回退 hex）");
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<string> SourceFiles()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        string root = dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根");
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static string Relative(string path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return path.Replace((dir?.FullName ?? "") + Path.DirectorySeparatorChar, "");
    }
}
