using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;
using SystemToolkit.Modules.MusicManager;
using SystemToolkit.Tests.Architecture;
using SystemToolkit.Tests.Music;
using Xunit;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 2026-09-09 实测视频复现（歌词显示 BUG：行渐隐/消失 + 无高亮 + 滚动错位）。
/// 三问：Q1 队列点选是否清空队列（日志实锤 UpNext 267→0）；Q2 现代模板歌词行渲染属性；
/// Q3 滚动动画落点。产出位图到 .artifacts-video 供人眼复核。
/// </summary>
public class MusicModernLyricsReproTests
{
    private static (MusicManagerViewModel Vm, FakePlaybackEngine Engine) CreateVm()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"music-repro-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var engine = new FakePlaybackEngine();
        var vm = new MusicManagerViewModel(
            store: new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json")),
            scanner: new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger())),
            queue: new PlaybackQueueService(),
            log: new NoopLogger(),
            engineProvider: () => engine,
            tagReader: new TagLibMusicTagReader(new NoopLogger()),
            dispatcher: null,
            urlResolver: null,
            audioProxy: null,
            catalog: null,
            credentials: null);
        return (vm, engine);
    }

    private static MusicSong Song(string name) => new()
    {
        Id = "local:" + name,
        LocalPath = $"C:/m/{name}.mp3",
        Name = name,
        Artist = "艺术家",
    };

    private static List<LyricLine> Lines(int count)
    {
        var list = new List<LyricLine>();
        for (int i = 0; i < count; i++)
        {
            list.Add(new LyricLine { Time = 5 + i * 4, Duration = 3.5, Text = $"歌词第{i + 1}行测试文本" });
        }
        return list;
    }

    private static void SetPrivate(object target, string name, object? value)
    {
        PropertyInfo? p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p is not null)
        {
            p.SetValue(target, value);
            return;
        }

        FieldInfo? f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        f?.SetValue(target, value);
    }

    // ════════ Q1：队列点选（复现 UpNext 267→0） ════════

    [Fact]
    public async Task Queue_ClickItem_DoesNotWipeQueue()
    {
        (MusicManagerViewModel vm, _) = CreateVm();
        var songs = Enumerable.Range(1, 50).Select(i => Song($"s{i}")).ToList();
        // 直接灌队列（模拟播放整库后的状态）
        FieldInfo? qf = vm.GetType().GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance);
        var queue = (PlaybackQueueService)qf!.GetValue(vm)!;
        queue.SetQueue(songs, songs[0]);

        // 用户在队列弹窗点选第 30 首（走 PlayQueueItemCommand）
        vm.PlayQueueItemCommand.Execute(songs[29]);

        Assert.True(queue.Queue.Count > 0,
            $"队列点选后队列被清空（实际 {queue.Queue.Count} 首）——SetQueue(自身活视图) 自清空实锤");
        Assert.Equal(songs[29].Id, queue.Current?.Id);
    }

    // ════════ Q2/Q3：现代模板歌词渲染探针 ════════

    [Fact]
    public void ModernLyrics_RenderProbe_RowAttributesAndScrollOffset()
    {
        Exception? captured = null;
        string report = "init";
        string pngPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".artifacts-video", "repro-modern.png");

        var thread = new Thread(() =>
        {
            try
            {
                ViewLoadSmokeGuardTests.EnsureApplication()
                    .Resources.MergedDictionaries.Add(ViewLoadSmokeGuardTests.LoadThemeWithFontsStubbed());

                (MusicManagerViewModel vm, _) = CreateVm();
                var view = new MusicManagerView(vm);
                view.Measure(new Size(1600, 400)); // 故意压低窗口：区分滚动单位（项≈10 vs 像素≈400）
                view.Arrange(new Rect(0, 0, 1600, 400));

                // 现代风格 + 打开完整播放器（复现视频场景）
                vm.SwitchPlayerStyleCommand.Execute("Modern");
                vm.OpenFullPlayerCommand.Execute(null);

                // 歌词 15 行 + 当前行推进（模拟播放中段）
                List<LyricLine> lines = Lines(15);
                SetPrivate(vm, "_lyricLineSource", lines);
                vm.LyricRows.Clear();
                foreach (LyricLine line in lines)
                {
                    vm.LyricRows.Add(new MusicManagerViewModel.LyricRowVm(line.Text, IsActive: false));
                }

                SetPrivate(vm, "ActiveLyricIndex", 7);
                SetPrivate(vm, "LyricProgress", 0.6);
                view.UpdateLayout();

                // 滚动动画跑完（350ms 插值 + 布局余量）
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalMilliseconds < 900)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
                }

                // 探针：ModernLyricsList 每行 TextBlock 属性 + ScrollViewer 偏移
                var sb = new System.Text.StringBuilder();
                var list = (ListBox)view.FindName("ModernLyricsList")!;
                ScrollViewer? sv = FindSv(list);
                // 面板自证：ItemsHost 实际类型 + ScrollUnit 附加属性值（瞬移根因定位用）
                DependencyObject? itemsHost = FindPresenterChild(list);
                var vsp = itemsHost as System.Windows.Controls.VirtualizingPanel;
                sb.AppendLine($"ItemsHost={itemsHost?.GetType().FullName ?? "null"}, " +
                    $"ScrollUnit={System.Windows.Controls.VirtualizingPanel.GetScrollUnit(list)}, " +
                    $"IsVirtualizing={System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(list)}, " +
                    $"IsVsp={vsp is not null}");
                sb.AppendLine($"ScrollOffset={sv?.VerticalOffset}, Scrollable={sv?.ScrollableHeight}, Extent={sv?.ExtentHeight}, Viewport={sv?.ViewportHeight}");

                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
                    {
                        sb.AppendLine($"row {i}: <no container>");
                        continue;
                    }

                    TextBlock? tb = FindTb(item);
                    double opacity = GetEffectiveOpacity(tb);
                    string fg = tb?.Foreground is SolidColorBrush sc ? $"#{sc.Color}" : tb?.Foreground?.GetType().Name ?? "null";
                    sb.AppendLine($"row {i}: text='{(tb?.Text.Length > 8 ? tb.Text[..8] : tb?.Text)}' opacity={opacity:F2} fg={fg} inView={IsInView(sv, item)}");
                }

                // 位图留证
                view.UpdateLayout();
                string? pngDir = Path.GetDirectoryName(Path.GetFullPath(pngPath));
                if (pngDir is not null)
                {
                    Directory.CreateDirectory(pngDir);
                }
                var rtb = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(view);
                var encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(pngPath))
                {
                    encoder.Save(fs);
                }

                report = sb.ToString();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".artifacts-video", "repro-report.txt"), report);
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

        Assert.True(captured is null, $"复现探针异常：{captured}");
        Assert.True(report.Contains("row 14"), "未遍历到全部行");
        // 输出报告到测试名下（trx / 控制台均可见）
        Assert.True(true, report);
    }

    private static ScrollViewer? FindSv(DependencyObject from)
    {
        int n = VisualTreeHelper.GetChildrenCount(from);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(from, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            ScrollViewer? found = FindSv(child);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static TextBlock? FindTb(DependencyObject from)
    {
        if (from is TextBlock tb)
        {
            return tb;
        }

        int n = VisualTreeHelper.GetChildrenCount(from);
        for (int i = 0; i < n; i++)
        {
            TextBlock? found = FindTb(VisualTreeHelper.GetChild(from, i));
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>找 ItemsPresenter 的第一个子节点（即实际 items host 面板）。</summary>
    private static DependencyObject? FindPresenterChild(DependencyObject from)
    {
        if (from is System.Windows.Controls.ItemsPresenter presenter)
        {
            return VisualTreeHelper.GetChild(presenter, 0);
        }

        int n = VisualTreeHelper.GetChildrenCount(from);
        for (int i = 0; i < n; i++)
        {
            DependencyObject? found = FindPresenterChild(VisualTreeHelper.GetChild(from, i));
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static double GetEffectiveOpacity(DependencyObject? o)
    {
        double v = 1.0;
        while (o is not null && o is Visual)
        {
            if (o is UIElement uie)
            {
                v *= uie.Opacity;
            }
            o = VisualTreeHelper.GetParent(o);
        }
        return v;
    }

    private static bool IsInView(ScrollViewer? sv, ListBoxItem item)
    {
        if (sv is null)
        {
            return false;
        }

        double top = item.TranslatePoint(new Point(0, 0), sv).Y;
        return top + item.ActualHeight > 0 && top < sv.ViewportHeight;
    }
}
