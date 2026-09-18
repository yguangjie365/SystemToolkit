using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 未主题化控件守卫（v21 批次落地）。
/// <para>
/// 背景（STA 探针实测，2026-09-19）：WPF 的 <c>TextElement.Foreground</c> 默认值是<b>纯黑 #000000</b>，
/// 且本仓主题包只给 <c>ScrollBar</c> / <c>ComboBoxItem</c> 设了隐式样式 —— 凡"本地/Style/祖先/宿主四层
/// 都无前景来源"的裸文本，在深色主题（背景 #000000）下就是黑字黑底，对比度 1.0:1。
/// </para>
/// <para>
/// 探针同时实测了<b>哪些元素继承根前景</b>（这决定了修法，勿凭直觉）：
/// <c>TextBlock</c> / <c>Run</c>(in TextBlock) <b>继承</b>；
/// <c>RadioButton</c> / <c>CheckBox</c> / <c>Label</c> / <c>Button</c> / <c>TextBox</c>
/// <b>均不继承</b>（各自默认样式自带前景，恒 #000000）。
/// ⇒ 只给窗口根加前景<b>修不掉</b> RadioButton，必须逐个挂样式或显式前景。
/// </para>
/// <para>
/// 扫描面 = <c>src</c> 下<b>全部</b> XAML（动态枚举）− 显式排除项。白名单制的致命缺陷是
/// "新文件忘加名单 = 静默不受检测"，故此处为 fail-safe 默认全扫。
/// </para>
/// </summary>
public class UnstyledControlGuardTests
{
    /// <summary>主题包是样式<b>定义端</b>，不是消费端；其中的裸 Setter 不属"未主题化控件"。</summary>
    private static readonly string[] Excluded =
    [
        "src/SystemToolkit.UI.Common/Themes/Packs/Claude/Claude.Light.xaml",
        "src/SystemToolkit.UI.Common/Themes/Packs/Nvidia/Nvidia.Dark.xaml",
        "src/SystemToolkit.UI.Common/Themes/Icons.xaml",
    ];

    /// <summary>
    /// 规则 1（零容忍）：<c>&lt;RadioButton&gt;</c> 必须挂 <c>Style</c>。
    /// 探针实测 RadioButton <b>不继承</b>根前景且背景透明 ⇒ 深色下黑字黑底完全不可见，
    /// 且它们常承载核心选项（备份范围 / 恢复目标 / 冲突策略）。
    /// </summary>
    [Fact]
    public void RadioButton_MustHaveStyle()
    {
        var failures = new List<string>();
        foreach (string rel in Scanned())
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), rel));
            foreach ((int lineNo, string body) in Elements(text, "RadioButton"))
            {
                if (!body.Contains("Style="))
                {
                    failures.Add($"{rel}:{lineNo} <RadioButton> 未挂 Style —— 实测不继承根前景，深色主题下文字不可见");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// 规则 2（零容忍）：<c>&lt;TextBox&gt;</c> 必须有 <c>Style</c> 或显式 <c>Foreground</c>。
    /// 探针实测 TextBox 默认前景恒 #000000、背景恒 #FFFFFF —— 不随主题。
    /// 透明搜索框（<c>Background="Transparent"</c>）属有意设计，只需补 Foreground，不强求 Style。
    /// </summary>
    [Fact]
    public void TextBox_MustHaveStyleOrForeground()
    {
        var failures = new List<string>();
        foreach (string rel in Scanned())
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), rel));
            foreach ((int lineNo, string body) in Elements(text, "TextBox"))
            {
                if (!body.Contains("Style=") && !body.Contains("Foreground"))
                {
                    failures.Add($"{rel}:{lineNo} <TextBox> 既无 Style 也无 Foreground —— 深色主题下白底黑字，不随主题");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// 规则 3（零容忍）：五个对话框窗口的<b>根元素</b>必须设 <c>TextElement.Foreground</c>。
    /// 这五个窗口曾全文件零前景来源（v21 🔴-1），根设一次即可覆盖文件内全部裸 TextBlock
    /// （TextBlock 继承；已显式设前景的子元素因局部值优先不受影响 —— 探针 CASE5 实测）。
    /// </summary>
    [Fact]
    public void DialogRoots_MustSetInheritedForeground()
    {
        string[] dialogs =
        [
            "src/SystemToolkit.Modules.AppManager/SoftwareEditWindow.xaml",
            "src/SystemToolkit.Modules.DriverManager/DriverBackupWindow.xaml",
            "src/SystemToolkit.Modules.FileBackup/PathInputWindow.xaml",
            "src/SystemToolkit.Modules.FileBackup/RestoreDialog.xaml",
            "src/SystemToolkit.Modules.FileBackup/RuleEditWindow.xaml",
        ];

        var failures = new List<string>();
        foreach (string rel in dialogs)
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), rel));
            bool ok = false;
            foreach ((int _, string body) in Elements(text, "Window"))
            {
                if (body.Contains("TextElement.Foreground"))
                {
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                failures.Add($"{rel} 根 <Window> 未设 TextElement.Foreground —— 裸 TextBlock 将回落为黑字（深色主题不可读）");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// 规则 4（总数断言）：扫描到的 RadioButton 元素数必须等于登记值。
    /// 防止"整段删除"式改动绕过规则 1 —— 只断言"每一处都有 Style"抓不到"少了一处"。
    /// </summary>
    [Fact]
    public void RadioButton_Count_MatchesRegistered()
    {
        // 37 = 全仓 <RadioButton> 元素实扫总数（2026-09-19 v21 批次登记）。
        // 其中本次修复挂上 ThemedRadioButton 的是 DriverBackupWindow 2 + RestoreDialog 5；
        // 其余 30 处此前已各有样式（如 SegmentedRadioItem），规则 1 已覆盖。
        // 本断言的意义：只断言"每处都有 Style"抓不到"整段删除"，总数对齐才能拦住减员。
        int expected = 37;
        int actual = 0;
        foreach (string rel in Scanned())
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), rel));
            actual += Elements(text, "RadioButton").Count;
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>按标签取元素体（含续行，直到遇到 <c>&gt;</c>）；返回 (1-based 行号, 元素体)。</summary>
    private static List<(int LineNo, string Body)> Elements(string text, string tag)
    {
        var result = new List<(int, string)>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        // 标签后必须紧跟 空白 / "/" / ">" —— 否则会把 <TextBox.InputBindings> 这类
        // **属性元素语法**误当成元素起点（本守卫首跑即因此误报 AppManagerView:175）。
        var start = new Regex("<" + tag + @"(?=[\s/>])", RegexOptions.Compiled);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!start.IsMatch(lines[i]))
            {
                continue;
            }

            string body = lines[i];
            int j = i;
            while (!body.Contains('>') && j + 1 < lines.Length)
            {
                j++;
                body += " " + lines[j];
            }

            result.Add((i + 1, body));
        }

        return result;
    }

    private static List<string> Scanned()
    {
        string root = RepoRoot();
        var list = new List<string>();
        foreach (string full in Directory.GetFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories))
        {
            if (full.Contains("/obj/") || full.Contains("\\obj\\") || full.Contains("/bin/") || full.Contains("\\bin\\"))
            {
                continue;
            }

            string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (!Excluded.Contains(rel))
            {
                list.Add(rel);
            }
        }

        list.Sort(StringComparer.Ordinal);
        return list;
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
