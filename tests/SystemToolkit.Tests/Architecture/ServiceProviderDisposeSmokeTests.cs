using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Infrastructure.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// 服务容器同步释放冒烟（2026-09-06「关闭程序即崩溃」事故的直接复现测试）。
/// <para>
/// 事故：宿主 <c>App.OnExit</c> 调 <c>ServiceProvider.Dispose()</c>（同步）。
/// 若单例只实现 <see cref="IAsyncDisposable"/>，MS DI 抛
/// <c>InvalidOperationException: type only implements IAsyncDisposable</c>。
/// 该异常在退出路径无人接管 → 未处理 → 退出码 0xE0434352，且崩溃日志为空，极难定位。
/// </para>
/// <para>
/// 本测试用 <c>using</c> 触发同一条同步释放路径：<b>修复前必红，修复后绿</b>。
/// 比"扫描类声明是否写了 IDisposable"更可靠——后者漏得掉「只写了 Dispose 方法、没声明接口」这种假修复。
/// </para>
/// </summary>
public class ServiceProviderDisposeSmokeTests
{
    [Fact]
    public void ServiceProvider_SyncDispose_DoesNotThrow_ForAsyncDisposableSingletons()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileWebServer, FileWebServer>();
        services.AddSingleton<IFileTransferService, FileTransferService>();
        services.AddSingleton<IDeviceDiscoveryService, DeviceDiscoveryService>();

        ServiceProvider provider = services.BuildServiceProvider();

        // 必须真正解析出实例：容器只释放"已创建"的单例，未解析的服务不进释放列表
        _ = provider.GetRequiredService<IFileWebServer>();
        _ = provider.GetRequiredService<IFileTransferService>();
        _ = provider.GetRequiredService<IDeviceDiscoveryService>();

        // using → 同步 Dispose()：修复前这里抛 InvalidOperationException
        Exception? failure = null;
        try
        {
            provider.Dispose();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.Null(failure);
    }
}
