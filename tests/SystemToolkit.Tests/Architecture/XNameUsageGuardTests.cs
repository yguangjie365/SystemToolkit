using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// x:Name 引用守卫（2026-09-19 v21 遗留项 P2 落地）：每个 <c>x:Name</c> 都必须在**三处之一**被引用，
/// 否则视为死字段 —— 命名元素会生成隐藏的后备字段并常驻内存，且是"没人读的状态"。
/// <para>
/// 🔴 <b>三处必须都查</b>（v21 实扫教训：只查前两处会漏，我把
/// <c>Indicator</c>/<c>LiveDot</c>/<c>Seg</c>/<c>SpinnerRotate</c>/<c>PART_Track</c>
/// 这 5 个被 XAML 引用的误判成"可清理"）：
/// <list type="number">
/// <item>同文件 <c>.xaml.cs</c>（同名标识符）；</item>
/// <item><c>tests/</c> 中的**带引号字面量**（裸词会被通用词污染，如 <c>"Text"</c> 命中 60+ 文件）；</item>
/// <item>XAML 内的 <c>TargetName</c> / <c>ElementName</c>（**Storyboard 动画目标常在此**）。</item>
/// </list>
/// </para>
/// <para>
/// 扫描面 = <c>src</c> 下全部 XAML（含主题包 —— 其中的 <c>RowBg</c>/<c>SelBar</c>/<c>PART_*</c>
/// 是 <c>ControlTemplate.Triggers</c> 的 <c>TargetName</c> 目标，靠第 3 处判据天然豁免）。
/// 零容忍：本守卫通过时，全仓**不存在**三处皆无引用的 <c>x:Name</c>。
/// </para>
/// </summary>
public class XNameUsageGuardTests
{
    [Fact]
    public void EveryXName_MustBeReferencedSomewhere()
    {
        string root = ViewLoadSmokeGuardTests.RepoRoot();
        List<string> xamls = EnumerateXamls(root);
        List<string> testsText = ReadAll(Path.Combine(root, "tests"), "*.cs");
        var allXamlText = xamls.ToDictionary(x => x, x => File.ReadAllText(x));

        var dead = new List<string>();
        int total = 0;

        foreach (string xaml in xamls)
        {
            string text = allXamlText[xaml];
            string cbPath = xaml + ".cs";
            string cb = File.Exists(cbPath) ? File.ReadAllText(cbPath) : string.Empty;

            foreach (Match m in Regex.Matches(text, @"x:Name=""(\w+)"""))
            {
                string name = m.Groups[1].Value;
                total++;

                // `PART_` 前缀 = WPF 控件协议约定（消费者经 GetTemplateChild 按名取），
                // 名字本身就是契约，不参与"是否被引用"的判定。
                if (name.StartsWith("PART_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Regex.IsMatch(cb, $@"\b{Regex.Escape(name)}\b"))
                {
                    continue; // ① code-behind 引用
                }

                // ② 测试按字面量锚定 —— 两种形式都要查：
                // 裸 "X"，以及 x:Name="X"（本仓 ReviewV18/V19 用的正是后者，
                // 首版只查裸形式 ⇒ 把 LogToggle / InstalledAppsList 误判成无引用）。
                if (testsText.Any(t =>
                        t.Contains($"\"{name}\"", StringComparison.Ordinal) ||
                        t.Contains($"x:Name=\\\"{name}\\\"", StringComparison.Ordinal) ||
                        t.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal)))
                {
                    continue;
                }

                bool xamlRef = allXamlText.Values.Any(v =>
                    v.Contains($"TargetName=\"{name}\"", StringComparison.Ordinal) ||
                    v.Contains($"ElementName={name}", StringComparison.Ordinal) ||
                    v.Contains($"ElementName=\"{name}\"", StringComparison.Ordinal));
                if (xamlRef)
                {
                    continue; // ③ XAML 内 TargetName/ElementName（含 Storyboard 目标）
                }

                string rel = Path.GetRelativePath(root, xaml).Replace('\\', '/');
                dead.Add($"{rel}  x:Name=\"{name}\" —— code-behind / tests / XAML 三处均无引用");
            }
        }

        // 实扫值 167（2026-09-19）；下限留余量以抓"扫描面失效"，但不得低于实扫的一半。
        Assert.True(total > 80, $"x:Name 扫描总数异常（{total}）—— 扫描面疑似失效（实扫期望 ≈167）");
        Assert.True(dead.Count == 0,
            "存在无引用的 x:Name（删除命名，或补上消费点；若确为预留请在守卫内显式登记理由）：\n" +
            string.Join("\n", dead));
    }

    private static List<string> EnumerateXamls(string root)
    {
        var list = new List<string>();
        foreach (string f in Directory.GetFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories))
        {
            if (f.Contains("/obj/") || f.Contains("\\obj\\") || f.Contains("/bin/") || f.Contains("\\bin\\"))
            {
                continue;
            }

            // 主题包是**控件模板定义端**：其中的 x:Name（PART_Popup / ContentSite / ToggleButton…）
            // 是模板内部约定，由 WPF 模板机制消费（GetTemplateChild），不属"死字段"。
            string norm = f.Replace('\\', '/');
            if (norm.Contains("Themes/Packs/") || norm.EndsWith("Themes/Icons.xaml"))
            {
                continue;
            }

            list.Add(f);
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static List<string> ReadAll(string dir, string pattern)
    {
        var list = new List<string>();
        if (!Directory.Exists(dir))
        {
            return list;
        }

        foreach (string f in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
        {
            if (f.Contains("/obj/") || f.Contains("\\obj\\") || f.Contains("/bin/") || f.Contains("\\bin\\"))
            {
                continue;
            }

            list.Add(File.ReadAllText(f));
        }

        return list;
    }
}
