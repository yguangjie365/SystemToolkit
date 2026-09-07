using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// DI 注册行为守卫。
/// 来源事故（2026-09-04 三次）：概览模块日志被多轮全文件重写覆盖回 NullLogger，
/// 异常被静默吞掉 → "信息不加载""无窗口空转"两次事故均因此排查时间翻倍。
/// NullLogger 是合法的**显式选择**（如无副作用场景），但绝不允许作为模块的默认注册存在——
/// 模块诊断必须可观测。本守卫保证：任何模块注册的 ILogger 都必须是落盘可查的实现。
/// </summary>
public class DiRegistrationGuardTests
{
    [Fact]
    public void DiGuard_EveryRegisteredLogger_MustBeObservableImplementation_NotSilentLogger()
    {
        var offenders = new List<string>();

        foreach (IModule module in Shell.App.KnownModules())
        {
            var services = new ServiceCollection();
            module.RegisterServices(services);
            using ServiceProvider provider = services.BuildServiceProvider();

            foreach (ServiceDescriptor descriptor in services.Where(d => d.ServiceType == typeof(ILogger)))
            {
                // 审查 2026-09-04：非键 ILogger 是单槽，后注册模块会覆盖先注册模块（概览日志曾全串进 appmanager 日志）
                if (!descriptor.IsKeyedService)
                {
                    offenders.Add(module.Id + "(非键注册)");
                    continue;
                }

                ILogger logger = provider.GetRequiredKeyedService<ILogger>(descriptor.ServiceKey!);
                if (logger is NullLogger)
                {
                    offenders.Add(module.Id + "(NullLogger)");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "以下模块的 ILogger 注册不合规（非键注册会互相覆盖；NullLogger 会静默吞掉诊断输出，" +
            "历史事故：概览页信息不加载 / 无窗口空转各一次）。" +
            "请使用 AddKeyedSingleton<ILogger>(模块Id, new FileLogger(模块Id))。模块：" + string.Join(", ", offenders));
    }

    [Fact]
    public void DiGuard_OverviewModuleLogger_MustBeFileLogger()
    {
        // 专属断言：概览页是诊断重灾区（硬件采集边界 / 传感器缺失定位全靠它），必须落盘
        var services = new ServiceCollection();
        new SystemToolkit.Modules.Overview.OverviewModule().RegisterServices(services);
        using ServiceProvider provider = services.BuildServiceProvider();

        ILogger logger = provider.GetRequiredKeyedService<ILogger>("overview");
        Assert.IsType<FileLogger>(logger);
    }

    [Fact]
    public async Task DiGuard_EveryRegisteredService_MustBeResolvableFromContainer()
    {
        // M12 补齐（全面代码审查 2026-09-05）：原守卫只查 ILogger——删除任何服务注册
        // （如 DriverManager 的 DriverBackupService）测试依旧全绿，故障拖到运行期 GetRequiredService 才爆。
        var failures = new List<string>();

        foreach (IModule module in Shell.App.KnownModules())
        {
            var services = new ServiceCollection();
            module.RegisterServices(services);
            Shell.App.RegisterSharedInfrastructure(services); // 宿主组合根的共享注册同源（如 IFileWebServer）
            // 异步释放：IAsyncDisposable-only 服务（如 FileTransferService）在同步 Dispose 时
            // 会抛 "type only implements IAsyncDisposable" ——容器形态随服务契约演进
            await using ServiceProvider provider = services.BuildServiceProvider();

            foreach (ServiceDescriptor descriptor in services)
            {
                if (descriptor.ServiceType == typeof(ILogger))
                {
                    continue; // 键控 ILogger 合规性由上面两条专属守卫覆盖
                }

                string implName = descriptor.ImplementationType?.Name ?? descriptor.ServiceType.Name;
                if (implName.EndsWith("View"))
                {
                    continue; // WPF 视图构造依赖 Application 级资源字典，纯容器解析范围之外
                    // ⚠️ 必须用 EndsWith：Contains 会把 ViewModel 一并排除（"DriverManagerViewModel" 含 "View"），
                    //    守卫将失去全部牙齿——反向验证实测踩中（2026-09-05）
                }

                try
                {
                    provider.GetRequiredService(descriptor.ServiceType);
                }
                catch (Exception ex)
                {
                    failures.Add($"{module.Id}:{descriptor.ServiceType.Name}（{ex.GetBaseException().Message}）");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "以下服务注册无法从容器解析（缺注册/缺依赖会拖到运行期才暴露）：" + string.Join("; ", failures));
    }
}
