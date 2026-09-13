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
/// </summary>
public class CommandBindingGuardTests
{
    [Theory]
    [InlineData("AppManager", "AppManagerView", "AppManagerViewModel")]
    [InlineData("Overview", "OverviewView", "OverviewViewModel")]
    [InlineData("DriverManager", "DriverManagerView", "DriverManagerViewModel")]
    public void ViewXaml_CommandBindingNames_MustExistOnCorrespondingVm(string module, string viewName, string vmName)
    {
        string xamlPath = Path.Combine(RepoRoot(), "src", $"SystemToolkit.Modules.{module}", $"{viewName}.xaml");
        string xaml = File.ReadAllText(xamlPath);

        // ContextMenu 段先摘出来（其绑定指向项 VM），避免污染页级判据
        string menuBindings = string.Concat(
            Regex.Matches(xaml, @"<ContextMenu[\s\S]*?</ContextMenu>").Select(m => m.Value));
        string pageXaml = Regex.Replace(xaml, @"<ContextMenu[\s\S]*?</ContextMenu>", string.Empty);

        MatchCollection matches = Regex.Matches(pageXaml, @"Command=""\{Binding ([^,""}]+)");
        Assert.NotEmpty(matches);

        string fullName = $"SystemToolkit.Modules.{module}.{vmName}";
        Type vmType = GetVmType(module, fullName);

        var missing = matches
            .Select(m => m.Groups[1].Value.Trim().Split('.')[^1])
            .Distinct()
            .Where(name => vmType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is null)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{viewName}.xaml 绑定了页 VM 上不存在的命令：{string.Join(", ", missing)}——WPF 运行时静默失效，按钮死掉");

        // 项 VM 作用域：只要该模块程序集里有任一 *Vm 类型提供该命令即可
        var menuNames = Regex.Matches(menuBindings, @"Command=""\{Binding ([^,""}]+)")
            .Select(m => m.Groups[1].Value.Trim().Split('.')[^1])
            .Distinct()
            .ToList();
        if (menuNames.Count == 0)
        {
            return;
        }

        var rowVmTypes = GetModuleAssembly(module).GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && t.Name.EndsWith("Vm", StringComparison.Ordinal))
            .ToList();
        var menuMissing = menuNames
            .Where(name => !rowVmTypes.Any(t => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null))
            .ToList();

        Assert.True(
            menuMissing.Count == 0,
            $"{viewName}.xaml 的 ContextMenu 绑定了模块内任何 *Vm 都不存在的命令：{string.Join(", ", menuMissing)}");
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
