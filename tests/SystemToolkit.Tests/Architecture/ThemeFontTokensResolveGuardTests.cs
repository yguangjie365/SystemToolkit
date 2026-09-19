using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using Xunit;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 主题包字体令牌的 pack URI 可解析性守卫（🟠-1，UI-v2 2026-09-19）。
/// <para>
/// 🔴 背景：两主题包的 5 个 <c>Font_*</c> 令牌原写作
/// <c>pack://application:,,,/Resources/Fonts/…</c>（**无 <c>;component</c>**）——
/// 三逗号空程序集段的解析目标是<strong>应用主程序集</strong>（<c>SystemToolkit.Shell.dll</c>），
/// 而字体资源实际嵌在 <c>SystemToolkit.UI.Common</c>（Shell.dll 产物仅 48 KB、零字体条目）。
/// 后果：复合字体族逐级跳过 → 全站正文静默回退 Microsoft YaHei UI，且无任何报警。
/// </para>
/// <para>
/// 本守卫**读真实主题包**（不硬编码 URI 清单 ⇒ fail-safe 全扫）：
/// 每个包必须恰好出现 5 个 <c>Font_*</c> 键、键名必须在登记表内，
/// 且每个键的**每一段** <c>pack://</c> 字体源都要能被 <see cref="Fonts.GetFontFamilies(Uri)"/>
/// 解析出非空族集合；首段还必须命中该键的期望族名。
/// </para>
/// <para>
/// ⚠️ 不能用 <c>Application.GetResourceStream</c> 断言：字体 URI 是**目录**形态，
/// 该方法对未尾斜杠 URI 直接抛 <c>ArgumentException（Part URI cannot end with a forward slash）</c>。
/// ⚠️ 复合串的分隔符是 <c>", "</c> 而非 <c>","</c> —— <c>pack://application:,,,</c> 自身含逗号，
/// 按 <c>","</c> 切会把 URI 切碎（本守卫首版探针即踩此坑）。
/// </para>
/// </summary>
public sealed class ThemeFontTokensResolveGuardTests
{
    private static readonly string[] Packs =
    [
        "src/SystemToolkit.UI.Common/Themes/Packs/Claude/Claude.Light.xaml",
        "src/SystemToolkit.UI.Common/Themes/Packs/Nvidia/Nvidia.Dark.xaml",
    ];

    /// <summary>键 → 首段字体源应解析出的族名。表外的 <c>Font_*</c> 键视为未登记 ⇒ 直接失败（fail-safe）。</summary>
    private static readonly Dictionary<string, string> ExpectedFamilies = new(StringComparer.Ordinal)
    {
        ["Font_Body"] = "Inter",
        ["Font_Mono"] = "JetBrains Mono",
        ["Font_TitleZh"] = "LXGW WenKai",
        ["Font_TitleEn"] = "Cormorant Garamond",
        ["Font_ImmersiveLyric"] = "Noto Serif SC",
    };

    private const int ExpectedKeysPerPack = 5;

    [Fact]
    public void ThemeFontTokens_PackUri_MustResolveToDeclaredFamilies()
    {
        List<string> failures = [];
        string detail = string.Empty;

        var thread = new Thread(() =>
        {
            ViewLoadSmokeGuardTests.EnsureApplication();
            string root = ViewLoadSmokeGuardTests.RepoRoot();

            foreach (string pack in Packs)
            {
                string name = Path.GetFileName(pack);
                string path = Path.Combine(root, pack.Replace('/', Path.DirectorySeparatorChar));
                string text = File.ReadAllText(path);
                MatchCollection matches = Regex.Matches(
                    text, "<FontFamily x:Key=\"(Font_[A-Za-z]+)\">([^<]*)</FontFamily>");

                if (matches.Count != ExpectedKeysPerPack)
                {
                    failures.Add(string.Format(
                        "{0}：Font_* 键 {1} 个，期望 {2} 个 —— 扫描面或令牌集已变，须同步本守卫登记表",
                        name, matches.Count, ExpectedKeysPerPack));
                }

                foreach (Match m in matches)
                {
                    string key = m.Groups[1].Value;
                    if (!ExpectedFamilies.TryGetValue(key, out string? expected))
                    {
                        failures.Add(name + "：" + key + " 未登记在本守卫的期望族名表内");
                        continue;
                    }

                    string[] parts = m.Groups[2].Value
                        .Split(", ")
                        .Select(s => s.Trim())
                        .Where(s => s.StartsWith("pack://", StringComparison.Ordinal))
                        .ToArray();

                    if (parts.Length == 0)
                    {
                        failures.Add(name + "：" + key + " 没有任何 pack:// 字体源");
                        continue;
                    }

                    for (int i = 0; i < parts.Length; i++)
                    {
                        int hash = parts[i].IndexOf('#');
                        string filePart = hash >= 0 ? parts[i][..hash] : parts[i];
                        List<string> families;
                        try
                        {
                            families = [.. Fonts.GetFontFamilies(new Uri(filePart, UriKind.Absolute))
                                .SelectMany(f => f.FamilyNames.Values)
                                .Distinct(StringComparer.OrdinalIgnoreCase)];
                        }
                        catch (Exception ex)
                        {
                            failures.Add(name + "：" + key + " 第 " + (i + 1) + " 段解析抛异常 —— "
                                         + filePart + " → " + ex.GetType().Name + ": " + ex.Message);
                            continue;
                        }

                        detail += "  " + name + " " + key + "[" + (i + 1) + "] " + filePart
                                  + " → " + families.Count + " [" + string.Join("|", families) + "]" + Environment.NewLine;

                        if (families.Count == 0)
                        {
                            failures.Add(name + "：" + key + " 第 " + (i + 1) + " 段解析出空族集合 —— "
                                         + filePart + "（字体未随程序集解析，运行时将静默回退）");
                            continue;
                        }

                        if (i == 0 && !families.Any(f => f.Contains(expected, StringComparison.OrdinalIgnoreCase)))
                        {
                            failures.Add(name + "：" + key + " 首段族名不含期望的「" + expected + "」，实得 ["
                                         + string.Join("|", families) + "]");
                        }
                    }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(120));

        Assert.True(failures.Count == 0,
            "主题包字体令牌解析失败（" + failures.Count + " 项）：" + Environment.NewLine
            + string.Join(Environment.NewLine, failures)
            + Environment.NewLine + "--- 实测明细 ---" + Environment.NewLine + detail);
    }
}
