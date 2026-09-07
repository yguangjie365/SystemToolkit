using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Modules.FileBackup;
using SystemToolkit.Shell;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 组合根接线契约守卫（REVIEW-3 C-1 防回归，2026-09-06）。
/// <para>
/// <b>事故</b>：FileBackupModule 的定时补做曾整体失效——模块以实例字段持有
/// <c>IServiceScopeFactory</c> 并由 <c>AttachScopeFactory</c> 注入，但该方法全仓<b>零调用</b>，
/// 且 Shell 补做路径经 <c>KnownModules()</c> 新建影子实例 → <c>_scopeFactory!</c> 必抛 NRE，
/// 被外层 catch 压成一条 CrashLog 静默失败。
/// </para>
/// <para>
/// <b>教训</b>：架构守卫只能验证引用方向，拦不住「承诺了但没人调用」的接线断裂。
/// 本守卫用反射固化两类约束：
/// ① 模块类型<b>不得持有</b> <see cref="IServiceScopeFactory"/> 等宿主服务字段（宿主依赖一律参数注入）；
/// ② <c>KnownModules()</c> 必须返回<b>静态缓存</b>（同进程多次调用同一批实例，杜绝影子实例）。
/// </para>
/// </summary>
public class ModulesWiringGuardTests
{
    [Fact]
    public void Module_Types_MustNotHoldHostServiceFields()
    {
        // 宿主服务类型：模块实例不得持久持有（应经方法参数注入）
        Type[] hostServiceTypes =
        [
            typeof(IServiceScopeFactory),
            typeof(IServiceProvider),
        ];

        var offenders = new List<string>();
        foreach (Type moduleType in App.KnownModules().Select(m => m.GetType()))
        {
            // 含继承链（ModuleBase 及子类）
            for (Type? t = moduleType; t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (hostServiceTypes.Any(h => h.IsAssignableFrom(f.FieldType)))
                    {
                        offenders.Add($"{moduleType.Name}.{f.Name} ({f.FieldType.Name})");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "模块类型持有宿主服务字段（IServiceScopeFactory/IServiceProvider）——这正是定时补做"
            + "接线断裂（REVIEW-3 C-1）的结构性根因。宿主依赖请改为方法参数注入：\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void KnownModules_ReturnsSameInstances_AcrossCalls()
    {
        IReadOnlyList<IModule> first = App.KnownModules();
        IReadOnlyList<IModule> second = App.KnownModules();

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.True(ReferenceEquals(first[i], second[i]),
                $"KnownModules()[{i}]（{first[i].GetType().Name}）跨调用返回了不同实例——影子实例会让"
                + " DI 注册单例与外部取用实例分道扬镳（REVIEW-3 C-1 根因之一）。KnownModules 必须静态缓存。");
        }
    }

    [Fact]
    public void RunDueScheduledBackups_TakesScopeFactory_AsParameter()
    {
        MethodInfo? m = typeof(FileBackupModule).GetMethod(
            "RunDueScheduledBackupsAsync",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(m);

        ParameterInfo[] ps = m.GetParameters();
        Assert.True(ps.Length >= 1 && ps[0].ParameterType == typeof(IServiceScopeFactory),
            "FileBackupModule.RunDueScheduledBackupsAsync 的第一个参数必须是 IServiceScopeFactory"
            + "（REVIEW-3 C-1 修复后的契约：宿主依赖参数注入，禁止回到实例字段模式）。");
    }

    /// <summary>
    /// 2026-09-08（审查 S-4）：DI 解析出的模块必须与 <c>KnownModules()</c> 同一引用。
    /// 防止将来有人在组合根用 <c>KnownModules().OfType&lt;T&gt;()</c> 另起炉灶，
    /// 形成「DI 一条链 / KnownModules 一条链」的实例分裂。
    /// </summary>
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根");
    }

    [Fact]
    public void DiResolvedModule_IsSameInstanceAsKnownModules()
    {
        // 组合根（App.xaml.cs）必须同时按具体类型注册：只注册 IModule 会让
        // GetRequiredService<FileBackupModule>() 解析失败 → 定时补做静默失效。
        string appSource = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "SystemToolkit.Shell", "App.xaml.cs"));
        Assert.Contains("AddSingleton(module.GetType(), module)", appSource);

        var services = new ServiceCollection();
        foreach (IModule module in App.KnownModules())
        {
            module.RegisterServices(services);
            services.AddSingleton(module);
            services.AddSingleton(module.GetType(), module);
        }

        App.RegisterSharedInfrastructure(services);
        using ServiceProvider provider = services.BuildServiceProvider();

        foreach (IModule known in App.KnownModules())
        {
            object resolved = provider.GetRequiredService(known.GetType());
            Assert.True(ReferenceEquals(known, resolved),
                $"{known.GetType().Name}：DI 解析实例与 KnownModules() 不是同一引用——"
                + "组合根只能有一个实例来源，否则模块状态会分裂。");
        }
    }
}
