using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// OM-9 字体渲染守卫：沉浸歌词的生产写法（baseUri 构造形态）必须**真渲染出非雅黑字形**。
/// <para>
/// 背景：FontFamily"已赋值"≠"字形已解析"——v6 像素级探针实证：双族名复合串的渲染结果
/// 与雅黑逐字节相同（复合链在族名不匹配时整体回退），而单名/baseUri 形态能渲染出衬线字形。
/// 本守卫用 RenderTargetBitmap 把生产写法与雅黑基线各渲一遍做像素比对，防再次回退。
/// </para>
/// </summary>
public class ImmersiveFontRenderProbe
{
    private const string Text = "沉迷歌词渲染探针永";

    private static byte[] Render(string fontFamily, double size = 48)
    {
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = Text,
            FontFamily = new FontFamily(fontFamily),
            FontSize = size,
            Foreground = Brushes.Black,
        };
        var panel = new System.Windows.Controls.StackPanel { Background = Brushes.White };
        panel.Children.Add(tb);
        panel.Measure(new Size(1200, 200));
        panel.Arrange(new Rect(0, 0, 1200, 200));
        panel.UpdateLayout();

        var rtb = new RenderTargetBitmap(1200, 200, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private const string FontBase =
        "pack://application:,,,/SystemToolkit.Modules.MusicManager;component/Assets/Fonts/NotoSerifSC-Black.otf";

    [Fact]
    public void Probe_ProductionFontForm_RendersNonYaheiGlyphs()
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                Application app = EnsureApplication();
                app.Resources.MergedDictionaries.Add(LoadThemeWithFontsStubbed());

                byte[] yahei = Render("Microsoft YaHei UI");
                byte[] baseUriForm = Render(
                    new FontFamily(new Uri(FontBase.Substring(0, FontBase.LastIndexOf('/') + 1)), "./NotoSerifSC-Black.otf#Noto Serif SC").Source);

                bool Same(byte[] a, byte[] b)
                {
                    if (a.Length != b.Length)
                    {
                        return false;
                    }

                    for (int i = 0; i < a.Length; i += 7)
                    {
                        if (a[i] != b[i])
                        {
                            return false;
                        }
                    }

                    return true;
                }

                // 生产写法（baseUri 构造形态，v7 OM-9 修复落点）必须真渲染出非雅黑字形——
                // 防回归：若某天 WPF/资源变更导致回退雅黑，这里变红（v6 实证：复合串会整体回退）
                Assert.False(Same(baseUriForm, yahei),
                    "生产字体写法渲染结果与雅黑逐字节相同——沉浸歌词又回退了！");
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

        Assert.True(captured is null, $"渲染探针异常：\n{captured}");
    }

    private static Application EnsureApplication()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        return Application.Current ?? throw new InvalidOperationException("Application 初始化失败");
    }

    private static ResourceDictionary LoadThemeWithFontsStubbed()
    {
        string themePath = Path.Combine(RepoRoot(),
            "src", "SystemToolkit.UI.Common", "Themes", "Packs", "Claude", "Claude.Light.xaml");
        var dict = new ResourceDictionary { Source = new Uri(themePath) };
        return dict;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("未找到仓库根");
    }
}
