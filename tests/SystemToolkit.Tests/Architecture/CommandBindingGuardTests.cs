using System.Reflection;
using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// S1 回归守卫（全面代码审查 2026-09-05）：XAML 的 Command 绑定必须指向真实存在的命令属性——
/// CommunityToolkit 对 async 方法剥 Async 后缀生成命令（ScanAsync→ScanCommand），XAML 拼错时
/// WPF 绑定静默失效、按钮死掉且零日志（DriverManagerView「刷新(R)」按钮实证）。
/// 覆盖两种绑定形态：页级 <c>{Binding XxxCommand}</c> 与行级 <c>{Binding DataContext.XxxCommand, RelativeSource=…}</c>
/// （后者同样指向页 VM——RelativeSource AncestorType=UserControl）。
/// <para>
/// 🔴 2026-09-13（B4-①③）新增第三形态：<c>&lt;ContextMenu&gt;</c> 内的 <c>{Binding XxxCommand}</c>
/// 按仓内纪律落在**项 VM**（ContextMenu 自成一棵可视树，FindAncestor 回不到页 VM）。
/// 故按**作用域**分域校验：ContextMenu 内的命令只需存在于该模块程序集的任一 <c>*Vm</c> 公开类型上；
/// 其余一律仍按页 VM 校验。这样既保住"拼错即红"的能力，又不逼着页面 VM 养一批只为过守卫的转发命令。
/// </para>
/// <para>
/// 🟠 2026-09-14（v8-🟠-7）：扫描范围由**硬编码 3 个模块**改为
/// 「**全量枚举 <c>src/**/*View.xaml</c> − 显式排除**」，与 B10 的令牌棘轮同款 fail-safe 改造。
/// 名单制的致命缺陷是「**新模块忘加名单 = 静默不受检测**」——本批实测：<c>FileTransferView.xaml</c>
/// 有 20+ 条 <c>{Binding Desktop.…}</c>（含 W2c/W3c 新增的 <c>SendFileToPhoneCommand</c>）
/// 完全不在守卫范围，写错命令名就是"按钮点了没反应"而无人拦截。
/// 现在新视图**自动进入检测**；要排除必须在此显式登记并写明理由。
/// </para>
/// <para>
/// 绑定路径按**属性链**解析（<c>Desktop.SendFileToPhoneCommand</c>、
/// <c>DataContext.Mobile.StartWebCommand</c>、<c>Settings.ApplyDnsCommand</c>）——
/// 嵌套子 VM 是各模块的既有形态，只认最后一段会把它们全判成"不存在"。
/// </para>
/// </summary>
public class CommandBindingGuardTests
{
    /// <summary>
    /// 显式排除清单。**空清单是合法的**（当前即空）：排除项必须写明理由，
    /// 否则宁可让守卫报红去查，也不要静默漏检。
    /// </summary>
    private static readonly (string Path, string Reason)[] ExcludedViews = Array.Empty<(string, string)>();

    /// <summary>扫描范围（构造时求值一次，失败信息里能直接看到全量名单）。</summary>
    private static readonly string[] ScannedViews = EnumerateScannedViews();

    private static readonly Regex CommandBinding = new(@"Command=""\{Binding ([^,""}]+)", RegexOptions.Compiled);

    private static readonly Regex ContextMenuBlock = new(@"<ContextMenu[\s\S]*?</ContextMenu>", RegexOptions.Compiled);

    [Fact]
    public void AllViewXaml_CommandBindingNames_MustExistOnCorrespondingVm()
    {
        var failures = new List<string>();

        foreach (string rel in ScannedViews)
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), rel));
            string viewName = Path.GetFileNameWithoutExtension(rel);
            string module = ModuleOf(rel);

            // ContextMenu 段先摘出来（其绑定指向项 VM），避免污染页级判据
            string menuBindings = string.Concat(ContextMenuBlock.Matches(xaml).Select(m => m.Value));
            string pageXaml = ContextMenuBlock.Replace(xaml, string.Empty);

            Type? vmType = TryResolveVmType(module, viewName);
            if (vmType is null)
            {
                failures.Add(
                    $"{rel}：找不到对应的页 VM 类型 SystemToolkit.Modules.{module}.{VmNameOf(viewName)}"
                    + "——命名约定变了？请同步本守卫或在此显式排除");
                continue;
            }

            var pagePaths = CommandBinding.Matches(pageXaml)
                .Select(m => m.Groups[1].Value.Trim())
                .Distinct()
                .ToList();
            foreach (string path in pagePaths)
            {
                if (!BindingPathExistsOn(vmType, path, out string? brokenAt))
                {
                    failures.Add(
                        $"{rel} 绑定了页 VM 上不存在的命令：{path}（断在「{brokenAt}」）"
                        + $"——{vmType.Name} 上没有它，WPF 运行时静默失效，按钮死掉");
                }
            }

            // 项 VM 作用域：只要该模块程序集里有任一 *Vm 类型提供该命令即可
            var menuNames = CommandBinding.Matches(menuBindings)
                .Select(m => m.Groups[1].Value.Trim().Split('.')[^1])
                .Distinct()
                .ToList();
            if (menuNames.Count == 0)
            {
                continue;
            }

            var rowVmTypes = GetModuleAssembly(module).GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract && t.Name.EndsWith("Vm", StringComparison.Ordinal))
                .ToList();
            var menuMissing = menuNames
                .Where(name => !rowVmTypes.Any(t => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null))
                .ToList();

            if (menuMissing.Count > 0)
            {
                failures.Add(
                    $"{rel} 的 ContextMenu 绑定了模块内任何 *Vm 都不存在的命令：{string.Join(", ", menuMissing)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---------------- 反向验证自检（03 §4.1：未经反向验证的守门等于没守门） ----------------

    /// <summary>
    /// 解析器自检：写错的命令名必须被判不存在，嵌套路径必须被正确展开。
    /// <para>这条钉的是"守卫本身能不能红"——只断言当前全绿，无法排除"解析器恒返回 true"。</para>
    /// </summary>
    [Fact]
    public void BindingPathResolver_TypoAndNestedPath_ReverseVerification()
    {
        Type overview = GetVmType("Overview", "SystemToolkit.Modules.Overview.OverviewViewModel");
        Assert.True(BindingPathExistsOn(overview, "RefreshFullCommand", out _));
        Assert.False(BindingPathExistsOn(overview, "RefreshFullCommandTypo", out string? broken));
        Assert.Equal("RefreshFullCommandTypo", broken);

        // 嵌套子 VM 的路径（FileTransfer 的主力形态）
        Type fileTransfer = GetVmType("FileTransfer", "SystemToolkit.Modules.FileTransfer.FileTransferViewModel");
        Assert.True(BindingPathExistsOn(fileTransfer, "Desktop.SendFileToPhoneCommand", out _));
        Assert.False(BindingPathExistsOn(fileTransfer, "Desktop.SendFileToPhoneCommandTypo", out _));
        Assert.False(BindingPathExistsOn(fileTransfer, "NoSuchChild.SomeCommand", out _));
        // 前导 DataContext. 是 WPF 元素属性，不是 VM 成员——必须被剥掉后再解析
        Assert.True(BindingPathExistsOn(fileTransfer, "DataContext.Mobile.StartWebCommand", out _));
        Assert.False(BindingPathExistsOn(fileTransfer, "DataContext.NoSuchChild.SomeCommand", out _));
    }

    /// <summary>
    /// 按**属性链**解析绑定路径，判断它在 <paramref name="root"/> 上是否存在。
    /// <para>
    /// 前导 <c>DataContext.</c> 先剥掉：它是 WPF 元素属性而非 VM 成员
    /// （DataTemplate 里配 <c>RelativeSource AncestorType=UserControl</c> 时指向页 VM）。
    /// </para>
    /// </summary>
    /// <param name="root">解析起点（页 VM 类型）。</param>
    /// <param name="path">绑定路径，如 <c>Desktop.SendFileToPhoneCommand</c>。</param>
    /// <param name="brokenAt">断在哪一段（成功时为 <c>null</c>）。</param>
    private static bool BindingPathExistsOn(Type root, string path, out string? brokenAt)
    {
        string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int start = segments.Length > 1 && segments[0] == "DataContext" ? 1 : 0;

        Type current = root;
        for (int i = start; i < segments.Length; i++)
        {
            PropertyInfo? prop = current.GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance);
            if (prop is null)
            {
                brokenAt = segments[i];
                return false;
            }

            current = prop.PropertyType;
        }

        brokenAt = null;
        return true;
    }

    /// <summary>由视图名推导页 VM 名（<c>FileTransferView</c> → <c>FileTransferViewModel</c>）。</summary>
    private static string VmNameOf(string viewName) =>
        viewName.EndsWith("View", StringComparison.Ordinal)
            ? viewName[..^"View".Length] + "ViewModel"
            : viewName + "ViewModel";

    /// <summary>由视图相对路径推导模块名（<c>src/SystemToolkit.Modules.X/XView.xaml</c> → <c>X</c>）。</summary>
    private static string ModuleOf(string relativePath)
    {
        string dir = Path.GetFileName(Path.GetDirectoryName(relativePath)!)!;
        const string Prefix = "SystemToolkit.Modules.";
        return dir.StartsWith(Prefix, StringComparison.Ordinal) ? dir[Prefix.Length..] : dir;
    }

    private static Type? TryResolveVmType(string module, string viewName)
    {
        try
        {
            return GetModuleAssembly(module).GetType($"SystemToolkit.Modules.{module}.{VmNameOf(viewName)}");
        }
        catch (Exception)
        {
            // 程序集加载失败：由下方"找不到 VM 类型"的失败信息一并暴露
            return null;
        }
    }

    /// <summary>按路径从测试输出目录加载模块程序集取 VM 类型——
    /// 不用 AppDomain.GetAssemblies()（惰性加载，模块程序集未必已进内存，实测 4/5 轮抖动）。</summary>
    private static Type GetVmType(string module, string fullName) =>
        GetModuleAssembly(module).GetType(fullName)
            ?? throw new InvalidOperationException($"未找到 VM 类型：{fullName}");

    /// <summary>从测试输出目录加载模块程序集（不走 AppDomain.GetAssemblies，理由见上）。</summary>
    private static Assembly GetModuleAssembly(string module)
    {
        string binDir = Path.GetDirectoryName(typeof(CommandBindingGuardTests).Assembly.Location)
            ?? throw new InvalidOperationException("无法定位测试输出目录");
        string dll = Path.Combine(binDir, $"SystemToolkit.Modules.{module}.dll");
        return File.Exists(dll)
            ? Assembly.LoadFrom(dll)
            : throw new InvalidOperationException($"未找到模块程序集：{dll}");
    }

    /// <summary>
    /// 枚举 <c>src</c> 下全部 <c>*View.xaml</c>，剔除 <c>obj/</c>、<c>bin/</c>
    /// （WPF 编译产物目录可能含拷贝的 xaml）与 <see cref="ExcludedViews"/>，按相对路径排序保证输出稳定。
    /// </summary>
    private static string[] EnumerateScannedViews()
    {
        var excluded = ExcludedViews.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        return Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*View.xaml", SearchOption.AllDirectories)
            .Where(p => !ViewLoadSmokeGuardTests.IsBuildArtifactPath(p))
            .Select(Relative)
            .Where(rel => !excluded.Contains(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>仓库根相对路径，统一用 <c>/</c> 分隔（Windows 上 <see cref="Path"/> 返回 <c>\</c>）。</summary>
    private static string Relative(string full) => Path.GetRelativePath(RepoRoot(), full).Replace('\\', '/');

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
