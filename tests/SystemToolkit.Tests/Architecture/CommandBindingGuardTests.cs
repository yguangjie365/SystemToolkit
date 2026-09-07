using System.Reflection;
using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// S1 回归守卫（全面代码审查 2026-09-05）：XAML 的 Command 绑定必须指向真实存在的命令属性——
/// CommunityToolkit 对 async 方法剥 Async 后缀生成命令（ScanAsync→ScanCommand），XAML 拼错时
/// WPF 绑定静默失效、按钮死掉且零日志（DriverManagerView「刷新(R)」按钮实证）。
/// 覆盖两种绑定形态：页级 <c>{Binding XxxCommand}</c> 与行级 <c>{Binding DataContext.XxxCommand, RelativeSource=…}</c>
/// （后者同样指向页 VM——RelativeSource AncestorType=UserControl）。
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
        MatchCollection matches = Regex.Matches(xaml, @"Command=""\{Binding ([^,""}]+)");
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
            $"{viewName}.xaml 绑定了 VM 上不存在的命令：{string.Join(", ", missing)}——WPF 运行时静默失效，按钮死掉");
    }

    /// <summary>按路径从测试输出目录加载模块程序集取 VM 类型——
    /// 不用 AppDomain.GetAssemblies()（惰性加载，模块程序集未必已进内存，实测 4/5 轮抖动）。</summary>
    private static Type GetVmType(string module, string fullName)
    {
        string binDir = Path.GetDirectoryName(typeof(CommandBindingGuardTests).Assembly.Location)
            ?? throw new InvalidOperationException("无法定位测试输出目录");
        string dll = Path.Combine(binDir, $"SystemToolkit.Modules.{module}.dll");
        Assembly asm = File.Exists(dll)
            ? Assembly.LoadFrom(dll)
            : throw new InvalidOperationException($"未找到模块程序集：{dll}");

        return asm.GetType(fullName)
            ?? throw new InvalidOperationException($"未找到 VM 类型：{fullName}");
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
