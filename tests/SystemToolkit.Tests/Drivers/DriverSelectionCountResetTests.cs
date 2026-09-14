using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 「驱动包勾选计数复位」结构锁（B-🔴-1，v11~v14 后续批次）。
/// <para>
/// 缺陷类：<c>ScanAsync</c> 重建 <c>Packages</c> 集合时**未复位底栏计数**。
/// 新行 VM 的 <c>_isSelected</c> 默认 false，而 <c>SetField</c> 判等 ⇒ 不触发 <c>PropertyChanged</c>；
/// 计数唯一刷新点是勾选事件回调 ⇒ 上一轮的「已选 N 项」会**残留**，且 <c>ClearSelection</c>
/// （赋"已是 false"的值）同样被短路，救不回来。用户看到底栏说"已选 3 项"、列表却无勾选，
/// 点删除报"勾选中没有可删除的第三方驱动包"。
/// </para>
/// <para>
/// 🔴 为什么既有守卫抓不到：<c>CommandCanExecuteRefreshGuardTests</c> 只对 <c>Selected*</c> 前缀 +
/// <c>[RelayCommand(CanExecute=…)]</c> 检查通知，而本缺陷里 <c>SelectedCount</c> 绑的是 XAML 的
/// Visibility/Text，**不经 CanExecute** ⇒ 落其扫描面之外。且 <c>DriverPackageVm</c> 全仓零测试构造
/// ⇒ 也拿不到行为锁。故以**结构不变量**补位。
/// </para>
/// <para>
/// 判据三条：① <c>SelectedCount</c> 只允许在 <c>RecountSelection</c> 内赋值（单一判据）；
/// ② <c>ScanAsync</c> 内出现 <c>Packages.Clear()</c> 则同方法体必须调用 <c>RecountSelection()</c>；
/// ③ <c>ClearSelection</c> 必须调用 <c>RecountSelection()</c>。
/// </para>
/// </summary>
public class DriverSelectionCountResetTests
{
    /// <summary><c>SelectedCount = …</c> 赋值（排除 <c>==</c> 比较与 <c>_selectedCount</c> 字段声明）。</summary>
    private static readonly Regex CountAssign =
        new(@"(?<![=!<>+\-*/])\bSelectedCount\s*=(?!=)", RegexOptions.Compiled);

    private const string Recount = "RecountSelection()";

    [Fact]
    public void SelectedCount_MustBeAssignedOnlyInsideRecountSelection()
    {
        string[] lines = ViewModelLines();
        (int start, int end) = MethodRange(lines, "RecountSelection");
        Assert.True(start >= 0,
            "未找到 RecountSelection 定义（已改名/搬移？本锁需同步修订，不要直接删锁）");

        var outside = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (CountAssign.IsMatch(lines[i]) && (i < start || i > end))
            {
                outside.Add($"  L{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(outside.Count == 0,
            "SelectedCount 只允许在 RecountSelection 内赋值（单一判据，勿两处各写一份口径）。越界："
            + Environment.NewLine + string.Join(Environment.NewLine, outside));
    }

    [Fact]
    public void ScanAsync_RebuildingPackagesMustRecount()
    {
        string[] lines = ViewModelLines();
        (int start, int end) = MethodRange(lines, "ScanAsync");
        Assert.True(start >= 0, "未找到 ScanAsync（已改名/搬移？本锁需同步修订）");
        Assert.True(HasToken(lines, start, end, "Packages.Clear()"),
            "ScanAsync 内未找到 Packages.Clear() —— 扫描重建路径已变，本锁需重新审视");
        Assert.True(HasToken(lines, start, end, Recount),
            "ScanAsync 重建 Packages 集合必须调用 RecountSelection()——"
            + "否则底栏「已选 N 项」残留（B-🔴-1 回归）");
    }

    [Fact]
    public void ClearSelection_MustRecountAfterReset()
    {
        string[] lines = ViewModelLines();
        (int start, int end) = MethodRange(lines, "ClearSelection");
        Assert.True(start >= 0, "未找到 ClearSelection（已改名/搬移？本锁需同步修订）");
        Assert.True(HasToken(lines, start, end, Recount),
            "ClearSelection 末尾必须调用 RecountSelection()——全为 false 时 SetField 判等短路，"
            + "勾选回调不会跑（B-🔴-1 的第二个入口）");
    }

    /// <summary>
    /// 反向验证（规则 6）：判据必须能区分「有复位」与「漏复位」。
    /// 直接喂文本，不依赖真实文件。
    /// </summary>
    [Fact]
    public void Judge_DistinguishesResetFromMissingReset()
    {
        string[] missing =
        [
            "    private async Task ScanAsync(CancellationToken ct)",
            "    {",
            "        Packages.Clear();",
            "        foreach (DriverPackage pkg in list)",
            "        {",
            "            Packages.Add(new DriverPackageVm(pkg));",
            "        }",
            "    }",
        ];
        (int ms, int me) = MethodRange(missing, "ScanAsync");
        Assert.True(ms >= 0, "自证用例：ScanAsync 定义未被识别");
        Assert.False(HasToken(missing, ms, me, Recount), "自证失败：漏复位的样本被误判为已复位");

        string[] reset =
        [
            "    private async Task ScanAsync(CancellationToken ct)",
            "    {",
            "        Packages.Clear();",
            "        RecountSelection();",
            "        foreach (DriverPackage pkg in list)",
            "        {",
            "            Packages.Add(new DriverPackageVm(pkg));",
            "        }",
            "    }",
        ];
        (int rs, int re) = MethodRange(reset, "ScanAsync");
        Assert.True(HasToken(reset, rs, re, Recount), "自证失败：已复位的样本未被识别");
    }

    // ── 判据助手（抽成静态方法：反向验证直接喂文本，无需真实文件） ──

    private static string[] ViewModelLines()
        => File.ReadAllLines(Path.Combine(ModuleDir(), "DriverManagerViewModel.cs"));

    /// <summary>定位**定义**行（含修饰符）的方法起止行；跳过同名的调用行。找不到返回 (-1,-1)。</summary>
    private static (int Start, int End) MethodRange(string[] lines, string method)
    {
        var def = new Regex(@"\b(private|public|internal|protected)\b[^\n]*\b" + method + @"\s*\(");
        for (int i = 0; i < lines.Length; i++)
        {
            if (!def.IsMatch(lines[i]))
            {
                continue;
            }

            // 表达式体（单行）与块体分别处理
            return lines[i].Contains("=>") ? (i, i) : (i, BodyEnd(lines, i));
        }

        return (-1, -1);
    }

    /// <summary>方法体末尾行（花括号深度配对；找不到闭合则取文件末行）。</summary>
    private static int BodyEnd(string[] lines, int sigLine)
    {
        int depth = 0;
        bool opened = false;
        for (int i = sigLine; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
        }

        return lines.Length - 1;
    }

    private static bool HasToken(string[] lines, int start, int end, string token)
    {
        for (int i = start; i <= end && i < lines.Length; i++)
        {
            if (lines[i].Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ModuleDir()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return Path.Combine(d?.FullName ?? throw new InvalidOperationException("未找到仓库根"),
            "src/SystemToolkit.Modules.DriverManager");
    }
}
