using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Drivers;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Modules.AppManager;
using SystemToolkit.Modules.DriverManager;
using SystemToolkit.Modules.FileBackup;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 运行时前景对比度守卫（2026-09-19 v21 遗留项 P1 落地）。
/// <para>
/// <b>为什么用运行时而不是静态扫描</b>：判"某控件有没有前景来源"需要分析整条链
/// （本地 → 自身 Style → 祖先 → 隐式样式 → 宿主），而 <c>ContentPresenter</c> /
/// <c>DataTemplate</c> / <c>ControlTemplate</c> 的层级<b>运行时才展开</b> ——
/// 静态文本分析极易误判（v21 中我已错两次：先按"RadioButton 无背景"推理，实际它有
/// Aero2 不透明白底；又把半透明的 <c>Brush_DangerSoft</c>(#1FE52020) 当成纯红）。
/// 本守卫读<b>实际渲染值</b>，与只断言"令牌对"的 <see cref="ThemeContrastGuardTests"/> 互补。
/// </para>
/// <para>
/// 🔴 <b>两条硬要求</b>（都是踩过的坑）：
/// ① 窗口必须 <c>Show()</c> —— 只 Measure/Arrange 不构建 Window 内容的可视化树（实测可见 TextBlock = 0）；
/// ② 背景必须做 <b>alpha 合成</b> —— 本仓有半透明令牌（<c>Brush_DangerSoft</c> = A0x1F），
/// 只看 RGB 会得出假结论。
/// </para>
/// <para>
/// 禁用态（<c>IsEnabled == false</c>）不参与断言：其灰度是平台约定，非主题缺陷。
/// </para>
/// </summary>
public class RuntimeForegroundContrastGuardTests
{
    /// <summary>正文阈值（WCAG AA）。次要文字若要放宽需在此显式分级，不做隐式豁免。</summary>
    private const double MinRatio = 4.5;

    [Fact]
    public void SoftwareEditWindow_DarkTheme_NoLowContrastText()
        => AssertNoLowContrast("SoftwareEditWindow", () => (Window)Make(typeof(SoftwareEditWindow)), 900, 700);

    [Fact]
    public void DriverBackupWindow_DarkTheme_NoLowContrastText()
        => AssertNoLowContrast("DriverBackupWindow", () => (Window)Make(typeof(DriverBackupWindow), 3, 1), 900, 700);

    [Fact]
    public void PathInputWindow_DarkTheme_NoLowContrastText()
        => AssertNoLowContrast("PathInputWindow",
            () => (Window)Make(typeof(PathInputWindow), "输入要备份的路径", "C:\\demo"), 900, 400);

    [Fact]
    public void RestoreDialog_DarkTheme_NoLowContrastText()
        => AssertNoLowContrast("RestoreDialog",
            () => (Window)Make(typeof(RestoreDialog), "共 3 项待恢复", "C:\\src\\demo"), 900, 700);

    [Fact]
    public void RuleEditWindow_DarkTheme_NoLowContrastText()
        => AssertNoLowContrast("RuleEditWindow", () => (Window)Make(typeof(RuleEditWindow), MakeBackupVm()), 1000, 800);

    // ================= 核心 =================

    private static void AssertNoLowContrast(string label, Func<Window> make, double w, double h)
    {
        var failures = new List<string>();
        Exception? captured = null;
        int checkedCount = 0;

        var thread = new Thread(() =>
        {
            try
            {
                Application app = ViewLoadSmokeGuardTests.EnsureApplication();
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(LoadDarkTheme());

                Window win = make();
                win.Width = w;
                win.Height = h;
                win.Show();
                win.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                win.UpdateLayout();

                var blocks = new List<TextBlock>();
                Walk(win, blocks);
                var targets = blocks
                    .Where(tb => tb.IsEnabled && !string.IsNullOrWhiteSpace(tb.Text))
                    .ToList();

                // 记住各元素中心点（相对窗口）与其前景，再把前景置透明渲染 —— 该点像素即**精确背景**。
                var spots = new List<(TextBlock Tb, Color Fg, Point Pt, string Src)>();
                foreach (TextBlock tb in targets)
                {
                    if ((tb.Foreground as SolidColorBrush)?.Color is not Color fg)
                    {
                        continue;
                    }

                    Point p = tb.TranslatePoint(
                        new Point(tb.ActualWidth / 2, tb.ActualHeight / 2), win);
                    spots.Add((tb, fg, p, Describe(tb)));
                }

                var saved = spots
                    .Select(s => (s.Tb, Brush: s.Tb.Foreground))
                    .ToList();
                foreach ((TextBlock tb, _) in saved)
                {
                    tb.Foreground = Brushes.Transparent;
                }

                win.UpdateLayout();
                var rtb = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(win.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(win.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                rtb.Render(win);

                foreach ((TextBlock tb, Brush old) in saved)
                {
                    tb.Foreground = old;
                }

                foreach ((TextBlock tb, Color fg, Point pt, string src) in spots)
                {
                    int x = Math.Clamp((int)pt.X, 0, rtb.PixelWidth - 1);
                    int y = Math.Clamp((int)pt.Y, 0, rtb.PixelHeight - 1);
                    byte[] px = new byte[4];
                    rtb.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                    // Pbgra32：B,G,R,A（预乘；此处 A=255 不必反预乘）
                    var bg = Color.FromArgb(px[3], px[2], px[1], px[0]);
                    if (bg.A < 255)
                    {
                        continue; // 半透明点不参与（窗口内不应出现）
                    }

                    checkedCount++;
                    double ratio = ThemeContrastMath.ContrastRatio(Hex(fg), Hex(bg));
                    if (ratio < MinRatio)
                    {
                        failures.Add(string.Format(CultureInfo.InvariantCulture,
                            "「{0}」ratio={1:F2}（<{2}）fg={3} bg={4} 文本=\"{5}\"（{6}）—— " +
                            "深色主题下不可读。修法：给该元素（或其容器）设前景，或在窗口根加 TextElement.Foreground。",
                            label, ratio, MinRatio, Hex(fg), Hex(bg), Trunc(tb.Text), src));
                    }
                }

                win.Close();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        Assert.True(captured is null, $"「{label}」运行时对比度检查抛异常：{captured}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(checkedCount > 0,
            $"「{label}」未取到任何可断言的 TextBlock —— 守卫失效（是否忘了 Show()？）");
    }

    /// <summary>反射构造（部分对话框是 private/internal 构造，仅经 static Show 暴露）。</summary>
    private static object Make(Type t, params object[] args)
    {
        ConstructorInfo c = t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .FirstOrDefault(x => x.GetParameters().Length == args.Length)
             ?? throw new InvalidOperationException($"{t.Name} 找不到 {args.Length} 参构造");
        return c.Invoke(args);
    }

    /// <summary>RuleEditWindow 需要 FileBackupViewModel（照 ViewLoadSmokeGuardTests 同款最小构造）。</summary>
    private static FileBackupViewModel MakeBackupVm()
    {
        string cfgDir = Path.Combine(Path.GetTempPath(), $"rb-contrast-{Guid.NewGuid():N}");
        var config = new BackupConfigService(cfgDir);
        config.Load();
        var vss = new ElevatedVssClient(helperPath: Path.Combine(Path.GetTempPath(), "no-such-helper.exe"));
        return new FileBackupViewModel(
            config, new RuleManager(Path.Combine(cfgDir, "rules"), null),
            new BackupService(config, vssClient: vss), new RestoreService(config),
            new RestoreService(config), new BackupTaskSchedulerService(new CommandRunner()));
    }

    /// <summary>加载 Nvidia.Dark（字体替换为宿主可用字体，避免 pack URI 解析失败）。</summary>
    private static ResourceDictionary LoadDarkTheme()
    {
        string path = Path.Combine(ViewLoadSmokeGuardTests.RepoRoot(),
            "src/SystemToolkit.UI.Common/Themes/Packs/Nvidia/Nvidia.Dark.xaml");
        string xaml = File.ReadAllText(path);
        xaml = Regex.Replace(xaml,
            @"<FontFamily x:Key=""(Font_[^""]+)"">[^<]*</FontFamily>",
            @"<FontFamily x:Key=""$1"">Consolas, Microsoft YaHei UI</FontFamily>");
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }

    private static void Walk(DependencyObject d, List<TextBlock> acc)
    {
        if (d is TextBlock tb && tb.IsVisible && tb.ActualWidth > 0)
        {
            acc.Add(tb);
        }

        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            Walk(VisualTreeHelper.GetChild(d, i), acc);
        }
    }

    /// <summary>简要定位信息（供失败消息用）。</summary>
    private static string Describe(TextBlock tb)
    {
        int depth = 0;
        DependencyObject? d = VisualTreeHelper.GetParent(tb);
        string parent = "?";
        if (d is not null)
        {
            parent = d.GetType().Name;
        }

        while (d is not null && depth < 30)
        {
            depth++;
            d = VisualTreeHelper.GetParent(d);
        }

        return $"父={parent}";
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Trunc(string s)
    {
        s = s.Replace("\n", " ").Replace("\r", " ");
        return s.Length <= 30 ? s : s[..30] + "…";
    }
}
