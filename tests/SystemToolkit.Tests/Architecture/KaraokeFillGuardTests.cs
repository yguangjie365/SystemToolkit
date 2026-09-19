using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SystemToolkit.Modules.MusicManager;
using Xunit;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 逐字卡拉OK填充守卫（2026-09-19 建立，锁「同一行两处高亮」缺陷）。
/// <para>
/// 缺陷根因：TextBlock 的 Foreground 用默认 <see cref="BrushMappingMode.RelativeToBoundingBox"/> 时，
/// WPF **按 glyph run 映射**渐变 —— 含空格的整句被切成多个 run 后，填充在每个 run 上各自从 0 重启。
/// </para>
/// <para>
/// 本守卫直接调用生产方法 <see cref="CoverColorFactory.KaraokeFill"/>，STA 渲染后用像素验证
/// 「填充边界 = 文本宽 × progress」；并静态锁住两个歌词模板必须设 HorizontalAlignment
/// （否则 ActualWidth 变成列表宽，填充会提前填满整行）与滚动条隐藏。
/// </para>
/// </summary>
public sealed class KaraokeFillGuardTests
{
    private const string LyricLine = "剩下嘴巴逞强 眼睛无力支撑";

    [Fact]
    public void KaraokeFill_MustReachProgressTimesTextWidth()
    {
        var sb = new StringBuilder();
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                ViewLoadSmokeGuardTests.EnsureApplication();
                Render(sb);
            }
            catch (Exception e)
            {
                err = e;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(err);

        double width = double.Parse(Extract(sb.ToString(), "WIDTH"), CultureInfo.InvariantCulture);
        double last = double.Parse(Extract(sb.ToString(), "LAST"), CultureInfo.InvariantCulture);
        double runs = double.Parse(Extract(sb.ToString(), "RUNS"), CultureInfo.InvariantCulture);
        string raw = sb.ToString();
        const double p = 0.33;

        // 填充必须抵达 progress×文本宽（容差 = 一个字符宽，取不到时可能落在字缝里）
        double expected = width * p;
        Assert.True(last >= expected - 30,
            $"填充边界 {last:F0} 未达到 progress×文本宽 {expected:F0}（文本宽 {width:F0}）—— " +
            "渐变很可能退回了 RelativeToBoundingBox（按 glyph run 映射 ⇒ 逐 run 重启填充）。\n探针输出:\n" + raw);

        // 反向：填充不得越过 progress×文本宽（多填 = 用了列表宽而不是文本宽）
        Assert.True(last <= expected + 30,
            $"填充边界 {last:F0} 越过了 progress×文本宽 {expected:F0} —— " +
            "很可能渐变宽度取了列表宽（TextBlock 忘了设 HorizontalAlignment，ActualWidth 被拉伸）。\n探针输出:\n" + raw);

        Assert.True(runs >= 1, "未检测到任何填充像素。\n探针输出:\n" + raw);
    }

    /// <summary>静态锁：两个歌词模板的 TextBlock 必须设 HorizontalAlignment；两个列表必须隐藏竖向滚动条。</summary>
    [Fact]
    public void LyricTemplates_MustSetHorizontalAlignmentAndHideScrollBar()
    {
        string xaml = File.ReadAllText(
            Path.Combine(ViewLoadSmokeGuardTests.RepoRoot(), "src", "SystemToolkit.Modules.MusicManager", "MusicManagerView.xaml"));

        MatchCollection blocks = Regex.Matches(xaml, "Text=\"\\{Binding Text, Mode=OneWay\\}\"");
        Assert.True(blocks.Count >= 2, $"歌词模板数异常（{blocks.Count}）—— 扫描面疑似失效。");

        foreach (Match m in blocks)
        {
            string tail = xaml.Substring(m.Index, Math.Min(400, xaml.Length - m.Index));
            Assert.Contains("HorizontalAlignment=", tail, StringComparison.Ordinal);
        }

        foreach (string list in new[] { "FullLyricsList", "ModernLyricsList" })
        {
            int i = xaml.IndexOf($"x:Name=\"{list}\"", StringComparison.Ordinal);
            Assert.True(i > 0, $"未找到 {list}。");
            string block = xaml.Substring(i, Math.Min(900, xaml.Length - i));
            Assert.Contains("VerticalScrollBarVisibility=\"Hidden\"", block, StringComparison.Ordinal);
        }
    }

    private static string Extract(string text, string key)
    {
        Match m = Regex.Match(text, key + @"=([-\d.]+)");
        Assert.True(m.Success, $"探针输出缺少 {key}：\n{text}");
        return m.Groups[1].Value;
    }

    private static void Render(StringBuilder sb)
    {
        const int w = 600;
        const int h = 40;
        const double p = 0.33;
        var accent = Color.FromRgb(0xFF, 0x6A, 0x00);
        var muted = Color.FromRgb(0x80, 0x80, 0x80);

        // 与生产模板同构：TextBlock 非 Stretch ⇒ ActualWidth = 文本实际宽度
        var tb = new TextBlock
        {
            Text = LyricLine,
            FontSize = 24,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var host = new Grid { Background = Brushes.Black, Width = w, Height = h };
        host.Children.Add(tb);
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        double textWidth = tb.ActualWidth;
        tb.Foreground = CoverColorFactory.KaraokeFill(
            new SolidColorBrush(accent), new SolidColorBrush(muted), textWidth, p);
        host.UpdateLayout(); // 设 Foreground 后必须重新布局，否则渲染的是旧视觉树

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        byte[] px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);

        int last = -1;
        int runs = 0;
        int ink = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                byte gr = px[i + 1];
                byte re = px[i + 2];
                if (re > 20 || gr > 20 || px[i] > 20)
                {
                    ink++;
                }

                if (re > 120 && (re - gr) > 60)
                {
                    runs++;
                    if (x > last)
                    {
                        last = x;
                    }
                }
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"WIDTH={textWidth:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"LAST={last:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"RUNS={runs:F0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"INK={ink:F0}");
    }
}
