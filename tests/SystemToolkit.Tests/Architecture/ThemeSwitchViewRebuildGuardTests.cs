using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using SystemToolkit.Modules.AppManager;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 主题切换 · 视图重建守卫（2026-09-15 实机截图事故沉淀）。
/// <para>
/// <b>事故现象</b>（用户截图，浅色主题下的「软件管理」页）：表格首行整行黑底白字、
/// 第 2~10 行「名称」列文字消失（图标仍在，白字压白底）、首行复选框在深色行上显示成实心白块。
/// </para>
/// <para>
/// <b>根因</b>：模块 XAML 以 <c>{StaticResource}</c> 引用的派生样式
/// （如 <c>&lt;Style BasedOn="{StaticResource ListItemRowTallStyle}"&gt;</c>）在 XAML <b>解析期</b>
/// 就绑定了主题包里的样式/画刷对象，而 <c>Style.BasedOn</c> 不支持 <c>DynamicResource</c>
/// （WPF 硬限制）——<b>只有重新创建视图</b>（重新解析 XAML）才会按新主题包解析。
/// 而当时的实现里"重建"是空操作：9 个模块的 View 全部注册为 DI 单例，
/// <c>CreateView</c> 恒返回同一实例，<c>PageHost.Content = 同一对象</c> 被依赖属性的同值短路吞掉。
/// ⇒ 行前景停在旧包的 <c>Brush_TextPrimary</c>（深色包 = 白），页面底却已刷成浅色 ⇒ 文字不可见。
/// </para>
/// <para>
/// <b>本守卫的判据</b>：走**真实链路**（真 <c>MainWindow</c> + 真 <c>IModule</c> + 真
/// <c>ThemeManager.Apply</c>）切主题后，页面上**已实例化元素**的生效画刷必须等于新主题包的令牌取值——
/// 不是"代码看起来对"，而是从 <c>ListViewItem.Foreground</c> / 模板子元素 <c>RowBg.Background</c> /
/// 复选框模板 <c>CheckBoxBox.Background</c> 上读出来的真实值。
/// 反向验证：把任一模块的 View 注册改回 <c>AddSingleton</c> ⇒ 本守卫变红。
/// </para>
/// </summary>
public class ThemeSwitchViewRebuildGuardTests
{
    /// <summary>
    /// 探针行（名称全局唯一）：<c>AppManagerViewModel.LoadAsync</c> 会先灌入
    /// <c>EnvListService.DefaultWingetPackages()</c> 的内置清单（与机器无关但顺序不由本测试控制），
    /// 故断言前先把集合换成这三行，按**名称**定位元素，避免依赖行号/机器环境。
    /// </summary>
    private static readonly (string Id, string Name)[] ProbePackages =
    [
        ("Probe.Zeta.Toolkit", "Probe Zeta"),
        ("Probe.Alpha.Toolkit", "Probe Alpha"),
        ("Probe.Mid.Toolkit", "Probe Mid"),
    ];

    /// <summary>
    /// 端到端：挂真 MainWindow → 切主题 → 断言**页面上生效的画刷**跟随新主题包。
    /// 三个阶段 + 一个反向阶段，覆盖用户实测的两条到达路径：
    /// ① 停留在该页时切主题（宿主 onThemeChanged 重建当前页）；
    /// ② 切完主题后离开再回来（旧实现里视图被 DI 单例钉住，永久停留在旧包）。
    /// </summary>
    [Fact]
    public void Guard_ThemeSwitch_MountedPage_MustResolveNewPackBrushes()
    {
        var failures = new List<string>();
        RunOnStaWithPump(dispatcher =>
        {
            Application app = ViewLoadSmokeGuardTests.EnsureApplication();

            // 启动序列等价：App.xaml 的令牌占位字典 + ThemeManager 的启动 Apply 路径。
            // 🔴 必须先清空：Application 是全 AppDomain 单实例，其它用例会往 MergedDictionaries 里
            // 追加各自的字典，后追加者优先——不清空则本守卫读到的"当前包"可能不是自己应用的那个。
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary());
            ThemeManager.Apply(ThemeManager.DefaultThemeId);

            string tempDir = Path.Combine(Path.GetTempPath(), $"theme-switch-guard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            Window? window = null;
            try
            {
                // 组合根 = 模块真实注册 + 只替换有外部副作用的实现（winget / 配置根走 temp）
                var services = new ServiceCollection();
                var module = new AppManagerModule();
                module.RegisterServices(services);
                services.AddSingleton<IWingetClient>(new StubWingetClient());
                services.AddSingleton(new EnvListService(tempDir));
                services.AddSingleton(new PackageIgnoreStore(tempDir));
                services.AddSingleton(new InstallHistoryStore(tempDir));
                using ServiceProvider provider = services.BuildServiceProvider();

                AppManagerViewModel vm = provider.GetRequiredService<AppManagerViewModel>();
                window = new SystemToolkit.Shell.MainWindow([module], provider)
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    Width = 1400,
                    Height = 900,
                };
                window.Show();

                // 视图 Loaded → LoadAsync 在 Task.Run 之后续体（本守卫已安装 Dispatcher 同步上下文，靠泵推进）
                PumpUntil(dispatcher, () => vm.StoreView is not null, TimeSpan.FromSeconds(20));

                // 换成探针行：此时 LoadAsync 已跑完（_initialized=true），新实例重建时不会再被灌回默认清单
                vm.StorePackages.Clear();
                foreach ((string id, string name) in ProbePackages)
                {
                    vm.StorePackages.Add(MakePackage(id, name));
                }

                // ① 基线：断言在"正确状态"下必须成立，否则红不能算缺陷证据
                CheckMountedPage(window, dispatcher, "① 浅色基线", failures);

                // ② 切深色：ThemeManager.Apply → ThemeChanged → MainWindow.OnThemeChanged → 重建当前页
                ThemeManager.Apply("nvidia");
                CheckMountedPage(window, dispatcher, "② 切到 Nvidia.Dark 后", failures);

                // ③ 离开再回来（用户实测的另一条路径：先去设置切主题，再回到软件管理页）
                if (window.FindName("NavList") is ListBox nav && nav.SelectedIndex >= 0)
                {
                    int selected = nav.SelectedIndex;
                    nav.SelectedIndex = -1;
                    nav.SelectedIndex = selected;
                }

                CheckMountedPage(window, dispatcher, "③ 切主题后离开再回到同一页", failures);

                // ④ 反向切回浅色：镜像症状（深色包的白色文字压在浅色底上）
                ThemeManager.Apply("claude");
                CheckMountedPage(window, dispatcher, "④ 切回 Claude.Light 后", failures);
            }
            catch (Exception ex)
            {
                failures.Add($"守卫自身异常（非产品缺陷也要修测试）：{ex}");
            }
            finally
            {
                try
                {
                    window?.Close();
                    ThemeManager.Apply(ThemeManager.DefaultThemeId); // 不留副作用给后续用例
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                    // 清理失败不影响判定
                }
            }
        });

        Assert.True(failures.Count == 0,
            "主题切换后视图未按新主题包重新解析（{StaticResource} 派生样式绑定的是解析期的包对象）：\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// 契约：<see cref="IModule.CreateView"/> 每次调用都必须产出**新实例**（宿主在主题切换时靠它重建页面）。
    /// <para>
    /// 与上一条互补——上一条证"AppManager 这一页行为正确"，本条把 9 个模块**全部**钉住：
    /// 任何模块把 View 注册回 <c>AddSingleton</c>（或自己缓存视图实例）都会在这里变红，
    /// 从而在构建期拦住"新模块抄错注册方式 / 老模块被改回单例"的退化。
    /// </para>
    /// <para>
    /// 为什么不用纯文本扫描：DI 的行为才是事实（工厂里 <c>new</c>、<c>AddSingleton(sp =&gt; ...)</c> 等
    /// 写法扫不全）。这里按模块建**真容器**并实调两次，读的是运行期结果。
    /// 视图构造需要 Application 级资源字典（<c>DiRegistrationGuardTests</c> 因此跳过 View），
    /// 故本用例在应用了主题的 STA 线程上跑。
    /// </para>
    /// </summary>
    [Fact]
    public void Guard_ModuleCreateView_MustYieldFreshInstancePerCall()
    {
        var failures = new List<string>();
        RunOnStaWithPump(async dispatcher =>
        {
            Application app = ViewLoadSmokeGuardTests.EnsureApplication();
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary());
            ThemeManager.Apply(ThemeManager.DefaultThemeId);

            int landed = 0;
            foreach (IModule module in SystemToolkit.Shell.App.KnownModules())
            {
                var services = new ServiceCollection();
                module.RegisterServices(services);
                SystemToolkit.Shell.App.RegisterSharedInfrastructure(services);
                // 异步释放：IAsyncDisposable-only 服务（如 FileWebServer）同步 Dispose 会抛
                await using ServiceProvider provider = services.BuildServiceProvider();

                object? first = module.CreateView(provider);
                if (first is null)
                {
                    continue; // 未落地模块（宿主显示「建设中」占位），无视图可重建
                }

                landed++;
                object? second = module.CreateView(provider);
                if (ReferenceEquals(first, second))
                {
                    failures.Add(
                        $"  · {module.Id}：CreateView 两次返回**同一实例**（{first.GetType().Name}）——"
                        + "宿主切主题时的\"重建\"会变成空操作，该页永久停留在旧主题包。"
                        + "修法：该模块的 View 注册改 AddTransient（VM 保持 AddSingleton）。");
                }

                if (first is not FrameworkElement)
                {
                    failures.Add($"  · {module.Id}：CreateView 返回的不是 WPF 元素（{first.GetType().FullName}）");
                }
            }

            if (landed < 8)
            {
                failures.Add($"  · 仅 {landed} 个模块返回了视图（预期 ≥8）——扫描面疑似缩水，本守卫会失去牙齿");
            }
        });

        Assert.True(failures.Count == 0,
            "以下模块的 CreateView 不满足「每次产出新实例」契约（IModule.CreateView 的 XML doc §实现契约）：\n"
            + string.Join("\n", failures));
    }

    // ══════════════════ 断言实现 ══════════════════

    /// <summary>
    /// 读取**当前挂载页**上已实例化元素的生效画刷，逐条与"当前主题包令牌"比对。
    /// <para>
    /// 断言对象刻意选三类"用户看得见"的载体：
    /// ① 行前景（<c>ListViewItem.Foreground</c>）——「名称」列文字继承它（白字压白底的那一处）；
    /// ② 行文字与底色的实际对比度——直接对应"文字不可见"这一症状（WCAG ≥4.5:1 是本仓正文红线）；
    /// ③ 选中行的模板子元素 <c>RowBg</c> 底 + 复选框模板 <c>CheckBoxBox</c> 底——截图里的"黑底行"与"实心白块"。
    /// </para>
    /// </summary>
    private static void CheckMountedPage(Window window, Dispatcher dispatcher, string phase, List<string> failures)
    {
        window.UpdateLayout();
        Pump(dispatcher);
        window.UpdateLayout();

        if (window.FindName("PageHost") is not ContentControl host)
        {
            failures.Add($"{phase}：宿主的 PageHost 元素缺失");
            return;
        }

        if (host.Content is not UserControl view)
        {
            failures.Add($"{phase}：PageHost 未挂载模块视图（Content={host.Content?.GetType().Name ?? "null"}）");
            return;
        }

        if (view.FindName("StoreList") is not ListView list)
        {
            failures.Add($"{phase}：视图 {view.GetType().Name} 内找不到 StoreList");
            return;
        }

        var rows = new List<ListViewItem>();
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem row)
            {
                rows.Add(row);
            }
        }

        if (rows.Count != ProbePackages.Length)
        {
            failures.Add($"{phase}：实化行数 {rows.Count} ≠ 探针行数 {ProbePackages.Length}（守卫无法取证，先修测试）");
            return;
        }

        // 选中第 0 行：截图的"整行黑底"只可能来自行模板的 IsSelected 触发器（静止态是 Transparent）
        rows[0].IsSelected = true;
        window.UpdateLayout();

        SolidColorBrush textPrimary = ShellResource("Brush_TextPrimary");
        SolidColorBrush selection = ShellResource("Brush_Selection");
        SolidColorBrush surface = ShellResource("Brush_Surface");

        foreach (ListViewItem row in rows)
        {
            int index = list.ItemContainerGenerator.IndexFromContainer(row);
            string name = ((WingetPackageVm)list.Items[index]).Name;

            Check(row.Foreground, textPrimary, $"{phase}／{name}：行前景（名称列文字继承它）", failures);

            TextBlock? nameText = Descendants<TextBlock>(row).FirstOrDefault(t => t.Text == name);
            if (nameText is null)
            {
                failures.Add($"{phase}／{name}：行内找不到名称文本元素");
                continue;
            }

            Check(nameText.Foreground, textPrimary, $"{phase}／{name}：名称文字生效前景", failures);
            CheckTextContrast(nameText, $"{phase}／{name}", failures);

            // 复选框：截图里首行"实心白块"= 复选框方框底色，两种主题包的 Brush_Surface 差异极大（#FFFFFF vs #1A1A1A）
            CheckBox? check = Descendants<CheckBox>(row).FirstOrDefault();
            if (check?.Template?.FindName("CheckBoxBox", check) is not Border box)
            {
                failures.Add($"{phase}／{name}：找不到复选框模板内的 CheckBoxBox");
                continue;
            }

            Check(box.Background, surface, $"{phase}／{name}：复选框方框底色", failures);
        }

        // 选中行底：行模板 RowBg 上的触发器画刷（指向旧包的 Brush_Selection 就是"整行黑底"）
        if (rows[0].Template?.FindName("RowBg", rows[0]) is not Border selectedRowBg)
        {
            failures.Add($"{phase}：选中行模板内找不到 RowBg");
            return;
        }

        Check(selectedRowBg.Background, selection, $"{phase}／选中行底色（RowBg）", failures);
    }

    private static void Check(Brush? actual, SolidColorBrush expected, string what, List<string> failures)
    {
        if (actual is SolidColorBrush solid && solid.Color == expected.Color)
        {
            return;
        }

        failures.Add($"  · {what}：实际={Describe(actual)}，应为当前主题包的 {expected.Color}"
            + "（差异意味着该元素仍绑定旧主题包的画刷 ⇒ 主题切换视觉残留）");
    }

    /// <summary>
    /// 正文对比度（WCAG 2.1，本仓 04 规范 §三红线：正文 ≥ 4.5:1）。
    /// 底色取**视觉树上的第一个不透明祖先底色**（行静止态是 Transparent，真正显示的是卡片底），
    /// 取不到时回退到当前主题包的 <c>Brush_Background</c>。
    /// </summary>
    private static void CheckTextContrast(TextBlock text, string what, List<string> failures)
    {
        SolidColorBrush? backdrop = Backdrop(text);
        if (text.Foreground is not SolidColorBrush fg || backdrop is null)
        {
            return; // 非实色（渐变/图片底）不在本守卫判据内
        }

        double ratio = ThemeContrastMath.ContrastRatio(fg.Color.ToString(), backdrop.Color.ToString());
        if (ratio < 4.5)
        {
            failures.Add($"  · {what}：文字 {fg.Color} 压底色 {backdrop.Color} 仅 {ratio:0.00}:1"
                + "（正文红线 4.5:1）——正是实机「名称列文字不可见」的形态");
        }
    }

    private static SolidColorBrush? Backdrop(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            Brush? background = current switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                _ => null,
            };

            if (background is SolidColorBrush { Color.A: 255 } opaque)
            {
                return opaque;
            }
        }

        return ShellResource("Brush_Background");
    }

    private static SolidColorBrush ShellResource(string key)
        => Application.Current.TryFindResource(key) as SolidColorBrush
           ?? throw new InvalidOperationException($"主题包缺少令牌 {key}（守卫依赖它做期望值）");

    private static string Describe(Brush? brush)
        => brush switch
        {
            null => "null",
            SolidColorBrush solid => solid.Color.ToString(),
            _ => brush.GetType().Name,
        };

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static WingetPackageVm MakePackage(string id, string name)
        => new(new WingetPackage { Id = id, Name = name, Description = name, Source = "msstore" });

    // ══════════════════ STA + Dispatcher 泵 ══════════════════

    /// <summary>
    /// 在 STA 线程上跑 WPF 场景并**泵消息**。
    /// <para>
    /// 🔴 为什么必须泵：视图 <c>Loaded</c> 里的 <c>await vm.LoadAsync()</c> 含 <c>Task.Run</c>，
    /// 其续体经 <see cref="DispatcherSynchronizationContext"/> 回到本线程——不泵就永远不执行，
    /// 真机上"页面已加载"的状态在测试里不会出现（守卫会假红/假绿）。
    /// 安装同步上下文还顺带挡住了另一种失败：续体落到线程池去改 ObservableCollection，
    /// 会变成跨线程异常（本仓既有用例踩过同类坑）。
    /// </para>
    /// </summary>
    private static void RunOnStaWithPump(Func<Dispatcher, Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                Task task = body(dispatcher);
                while (!task.IsCompleted)
                {
                    Pump(dispatcher);
                }

                task.GetAwaiter().GetResult(); // 已结束 → 只是把异常抛回本线程
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(120));

        Assert.True(error is null, $"STA 场景执行失败：{error}");
    }

    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时（视图未进入已加载状态）");
            }

            Pump(dispatcher);
        }
    }

    private static void RunOnStaWithPump(Action<Dispatcher> body)
        => RunOnStaWithPump(dispatcher =>
        {
            body(dispatcher);
            return Task.CompletedTask;
        });
}
