using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UiEditor.Core;
using SystemToolkit.Core.Utilities;
using SystemToolkit.UI.Common;
using SystemToolkit.UI.Common.Themes.Tokens;

namespace UiEditor.App;

/// <summary>UI 编辑器 M1 主窗：源文件行映射 → 令牌约束编辑 → 示意式实时预览 → 字节 splice 回写 + 裸值预检 + 重校验回滚。</summary>
public partial class MainWindow : Window
{
    private readonly string _repoRoot;
    private string? _targetPath;
    private string _rawText = string.Empty;
    private SourceNode? _tree;
    private readonly Dictionary<int, Dictionary<string, string>> _pending = new();
    private SourceNode? _selected;
    private readonly Dictionary<int, Border> _borderByLine = new();
    private bool _suppress;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _repoRoot = FindRepoRoot();

        RowCombo.ItemsSource = Enumerable.Range(0, 7).Select(i => i.ToString()).ToList();
        ColumnCombo.ItemsSource = Enumerable.Range(0, 7).Select(i => i.ToString()).ToList();
        MarginCombo.ItemsSource = TokenKeys.BaseTokens.Where(k => k.StartsWith("Space_", StringComparison.Ordinal)).ToList();
        FontSizeCombo.ItemsSource = TokenKeys.BaseTokens.Where(k => k.StartsWith("Font_Size", StringComparison.Ordinal)).ToList();
        ForegroundCombo.ItemsSource = TokenKeys.BaseTokens.Where(k => k.StartsWith("Brush_", StringComparison.Ordinal)).ToList();
        StyleCombo.ItemsSource = TokenKeys.ComponentStyles.ToList();

        Loaded += (_, _) =>
        {
            DiscoverTargets();
            if (TargetCombo.Items.Count > 0)
            {
                TargetCombo.SelectedIndex = 0;
            }
        };
    }

    // ───────────── 目标发现与载入 ─────────────
    private void DiscoverTargets()
    {
        TargetCombo.Items.Clear();
        string modulesRoot = Path.Combine(_repoRoot, "src");
        var files = Directory.GetFiles(modulesRoot, "*View.xaml", SearchOption.AllDirectories)
            .Where(f => f.Contains(System.IO.Path.DirectorySeparatorChar + "SystemToolkit.Modules."))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string f in files)
        {
            TargetCombo.Items.Add(Path.GetFileName(f));
            _targetIndexByName[Path.GetFileName(f)] = f;
        }
    }

    private readonly Dictionary<string, string> _targetIndexByName = new();

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetCombo.SelectedItem is string name && _targetIndexByName.TryGetValue(name, out string? path))
        {
            LoadTarget(path);
        }
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_targetPath is not null)
        {
            LoadTarget(_targetPath);
        }
    }

    private void LoadTarget(string path)
    {
        try
        {
            _targetPath = path;
            _rawText = File.ReadAllText(path);
            _tree = SourceMap.BuildTree(_rawText);
            _pending.Clear();
            RebuildTree();
            RebuildSchematic();
            StatusText.Text = $"已载入 {Path.GetFileName(path)}（{SourceMap.Build(_rawText).Count} 个作者元素）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "❌ 载入失败：" + ex.Message;
        }
    }

    // ───────────── 元素树 ─────────────
    private void RebuildTree()
    {
        ElementTree.Items.Clear();
        if (_tree is not null)
        {
            ElementTree.Items.Add(MakeTreeItem(_tree));
        }
    }

    private TreeViewItem MakeTreeItem(SourceNode n)
    {
        var item = new TreeViewItem
        {
            Header = $"{n.LocalName}{(string.IsNullOrEmpty(n.Name) ? "" : " #" + n.Name)}  ·L{n.Line}",
            Tag = n,
        };
        foreach (SourceNode c in n.Children)
        {
            item.Items.Add(MakeTreeItem(c));
        }

        return item;
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: SourceNode n })
        {
            SelectNode(n);
        }
    }

    private void SelectNode(SourceNode n)
    {
        _selected = n;
        InspectorPanel.IsEnabled = true;
        SelectedInfo.Text = $"{n.LocalName}" + (string.IsNullOrEmpty(n.Name) ? "" : $"  #{n.Name}") + $"  ·源行 {n.Line}";

        _suppress = true;
        RowCombo.SelectedItem = CurrentAttr(n, "Grid.Row");
        ColumnCombo.SelectedItem = CurrentAttr(n, "Grid.Column");
        MarginCombo.SelectedItem = TokenOf(CurrentAttr(n, "Margin"));
        FontSizeCombo.SelectedItem = TokenOf(CurrentAttr(n, "FontSize"));
        ForegroundCombo.SelectedItem = TokenOf(CurrentAttr(n, "Foreground"));
        StyleCombo.SelectedItem = TokenOf(CurrentAttr(n, "Style"));
        _suppress = false;

        Highlight(n.Line);
    }

    private string? CurrentAttr(SourceNode n, string attr)
    {
        if (_pending.TryGetValue(n.Line, out Dictionary<string, string>? pend) && pend.TryGetValue(attr, out string? v))
        {
            return attr is "Grid.Row" or "Grid.Column" ? v : TokenOf(v);
        }

        if (!n.Attributes.TryGetValue(attr, out string? raw))
        {
            return null;
        }

        return attr is "Grid.Row" or "Grid.Column" ? raw : TokenOf(raw);
    }

    private static string? TokenOf(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return null;
        }

        int i = markup.IndexOf("Resource ", StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        string rest = markup[(i + "Resource ".Length)..].TrimEnd('}', ' ');
        return rest;
    }

    // ───────────── 示意式布局预览 ─────────────
    private void RebuildSchematic()
    {
        _borderByLine.Clear();
        PreviewHost.Content = _tree is null ? null : RenderNode(_tree);
        if (_selected is not null)
        {
            Highlight(_selected.Line);
        }
    }

    private FrameworkElement RenderNode(SourceNode n)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = $"{n.LocalName}{(string.IsNullOrEmpty(n.Name) ? "" : " #" + n.Name)}" +
                   $"  [{DescribePos(n)}]",
            FontSize = 10,
            Margin = new Thickness(3),
        });
        ForegroundToken(body.Children[0] as TextBlock, "Brush_TextMuted");

        if (n.Children.Count > 0)
        {
            var grid = new Grid();
            int maxRow = n.Children.Select(c => IntAttr(c, "Grid.Row")).DefaultIfEmpty(0).Max();
            int maxCol = n.Children.Select(c => IntAttr(c, "Grid.Column")).DefaultIfEmpty(0).Max();
            for (int r = 0; r <= maxRow; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int c = 0; c <= maxCol; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            foreach (SourceNode child in n.Children)
            {
                FrameworkElement cv = RenderNode(child);
                Grid.SetRow(cv, IntAttr(child, "Grid.Row"));
                Grid.SetColumn(cv, IntAttr(child, "Grid.Column"));
                grid.Children.Add(cv);
            }

            body.Children.Add(grid);
        }

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2),
            Child = body,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush_Border");
        string? bgToken = TokenOf(Attr(n, "Background"));
        if (bgToken is not null)
        {
            border.SetResourceReference(Border.BackgroundProperty, bgToken);
        }

        _borderByLine[n.Line] = border;
        return border;
    }

    private string DescribePos(SourceNode n)
    {
        string r = Attr(n, "Grid.Row") ?? "-";
        string c = Attr(n, "Grid.Column") ?? "-";
        return $"R{r} C{c}";
    }

    private static void ForegroundToken(TextBlock? tb, string key)
    {
        tb?.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    private string? Attr(SourceNode n, string key)
    {
        if (_pending.TryGetValue(n.Line, out Dictionary<string, string>? pend) && pend.TryGetValue(key, out string? v))
        {
            return v;
        }

        return n.Attributes.TryGetValue(key, out string? raw) ? raw : null;
    }

    private int IntAttr(SourceNode n, string key) => int.TryParse(Attr(n, key), out int v) ? v : 0;

    private void Highlight(int line)
    {
        foreach (KeyValuePair<int, Border> kv in _borderByLine)
        {
            kv.Value.SetResourceReference(Border.BorderBrushProperty, "Brush_Border");
            kv.Value.BorderThickness = new Thickness(1);
        }

        if (_borderByLine.TryGetValue(line, out Border? b))
        {
            b.SetResourceReference(Border.BorderBrushProperty, "Brush_Accent");
            b.BorderThickness = new Thickness(2);
        }
    }

    // ───────────── 检视器编辑 → pending ─────────────
    private void OnEditChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _selected is null || sender is not ComboBox cb || cb.SelectedItem is not string token)
        {
            return;
        }

        (string attr, string value) = cb switch
        {
            var _ when ReferenceEquals(cb, RowCombo) => ("Grid.Row", token),
            var _ when ReferenceEquals(cb, ColumnCombo) => ("Grid.Column", token),
            var _ when ReferenceEquals(cb, MarginCombo) => ("Margin", $"{{DynamicResource {token}}}"),
            var _ when ReferenceEquals(cb, FontSizeCombo) => ("FontSize", $"{{DynamicResource {token}}}"),
            var _ when ReferenceEquals(cb, ForegroundCombo) => ("Foreground", $"{{DynamicResource {token}}}"),
            var _ when ReferenceEquals(cb, StyleCombo) => ("Style", $"{{StaticResource {token}}}"),
            _ => default,
        };

        if (string.IsNullOrEmpty(attr))
        {
            return;
        }

        if (!_pending.TryGetValue(_selected.Line, out Dictionary<string, string>? dict))
        {
            dict = new Dictionary<string, string>();
            _pending[_selected.Line] = dict;
        }

        dict[attr] = value;
        RebuildSchematic();
        StatusText.Text = $"{_pending.Count} 个元素待保存";
    }

    // ───────────── 应用 pending → 新文本（字节 splice） ─────────────
    private string BuildPatched()
    {
        string cur = _rawText;
        foreach (KeyValuePair<int, Dictionary<string, string>> byLine in _pending)
        {
            foreach (KeyValuePair<string, string> kv in byLine.Value)
            {
                (int s, int e) = XamlSplicer.LocateTagByLine(cur, byLine.Key);
                cur = XamlSplicer.SetAttributeInTag(cur, s, e, kv.Key, kv.Value);
            }
        }

        return cur;
    }

    private void OnPreviewDiff(object sender, RoutedEventArgs e)
    {
        if (_pending.Count == 0)
        {
            DiffText.Text = "(无改动)";
            return;
        }

        DiffText.Text = MakeDiff(_rawText, BuildPatched());
    }

    // ───────────── 落盘 + 裸值预检 + 重校验回滚 ─────────────
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return; // 重校验进行中，拒绝重入
        }

        try
        {
            if (_pending.Count == 0 || _targetPath is null)
            {
                StatusText.Text = "无改动";
                return;
            }

            string patched = BuildPatched();
            IReadOnlyList<string> violations = BareValueChecker.Scan(patched);
            if (violations.Count > 0)
            {
                DiffText.Text = "⛔ 裸值预检未通过，未落盘：\n" + string.Join("\n", violations);
                StatusText.Text = "⛔ 中止：检出裸值";
                return;
            }

            string original = _rawText;
            AtomicFile.WriteAllText(_targetPath, patched);
            _busy = true;
            StatusText.Text = "已落盘，重校验中（Release 构建 + 全量测试，约 1–2 分钟）…";

            (int buildCode, int testCode) = await Task.Run(Revalidate);
            _busy = false;

            if (buildCode != 0 || testCode != 0)
            {
                AtomicFile.WriteAllText(_targetPath, original); // 回滚
                _rawText = original;
                StatusText.Text = $"❌ 重校验失败（build={buildCode} test={testCode}），已回滚原文件";
                DiffText.Text = "重校验未通过，改动已自动回滚。请检查 diff 是否破坏了标记/令牌纪律。";
                return;
            }

            _rawText = patched;
            _pending.Clear();
            _tree = SourceMap.BuildTree(_rawText);
            RebuildTree();
            RebuildSchematic();
            DiffText.Text = MakeDiff(original, patched) + "\n\n✔ 落盘 + Release 构建 + 全量测试全绿";
            StatusText.Text = "✔ 已落盘并通过重校验";
        }
        catch (Exception ex)
        {
            _busy = false;
            StatusText.Text = "❌ 保存异常：" + ex.Message;
        }
    }

    private (int, int) Revalidate()
    {
        int build = RunDotnet($"build SystemToolkit.sln -c Release -v quiet");
        int test = build == 0 ? RunDotnet("test SystemToolkit.sln -c Release --no-build -v quiet") : -1;
        return (build, test);
    }

    private int RunDotnet(string args)
    {
        // 只需退出码：不重定向 stdout/stderr（顺序 ReadToEnd 会在子进程写满另一条管道时死锁）。
        // GUI 宿主无控制台，子进程输出自然丢弃；CreateNoWindow 避免弹黑窗。
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    // ───────────── 主题 ─────────────
    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ThemeManager.Apply(ThemeNvidia.IsChecked == true ? "nvidia" : "claude");
        }
    }

    // ───────────── 工具 ─────────────
    private static string MakeDiff(string oldText, string newText)
    {
        string[] o = oldText.Replace("\r\n", "\n").Split('\n');
        string[] n = newText.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        int rows = Math.Max(o.Length, n.Length);
        for (int i = 0; i < rows; i++)
        {
            string ol = i < o.Length ? o[i] : string.Empty;
            string nl = i < n.Length ? n[i] : string.Empty;
            if (ol != nl)
            {
                sb.AppendLine($"-{i + 1}: {ol.Trim()}");
                sb.AppendLine($"+{i + 1}: {nl.Trim()}");
            }
        }

        return sb.Length == 0 ? "(无差异)" : sb.ToString();
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根");
    }
}
