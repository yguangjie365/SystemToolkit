using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Modules.DriverManager;

/// <summary>
/// 驱动管理 模块。V0.3-A：扫描枚举 + 分组 + 筛选 + 搜索（只读）；
/// 特权操作（删除/安装/添加/导出）随 V0.3-B 经 Elevated Helper 通道接线。
/// 复用说明：旧工程无真实驱动能力（仅手动安装包清单），pnputil 封装为新增功能层。
/// </summary>
public sealed class DriverManagerModule : ModuleBase
{
    public override string Id => "drivermanager";

    public override string DisplayName => "驱动管理";

    public override int Order => 3;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<DriverManagerView>();

    public override void RegisterServices(IServiceCollection services)
    {
        // 可观测日志（NullLogger 吞异常教训）：键控注册（审查 2026-09-04：非键 ILogger 单槽会被覆盖）
        services.AddKeyedSingleton<ILogger>("drivermanager", new FileLogger("drivermanager"));
        // 枚举（只读）直连 pnputil；写操作 + 类别翻译经 Elevated Helper 单次 UAC
        services.AddSingleton<PnpUtilService>();
        services.AddSingleton<ElevatedPnpUtilClient>();
        services.AddSingleton<IPnpUtilClient>(sp => sp.GetRequiredService<ElevatedPnpUtilClient>());
        // 方案甲：把提权翻译委托注入 DriverScanner（扫描时批量 GUID→中文类名）
        services.AddSingleton(sp =>
        {
            ElevatedPnpUtilClient elevated = sp.GetRequiredService<ElevatedPnpUtilClient>();
            return new DriverScanner(
                sp.GetRequiredService<IPnpUtilClient>(),
                elevated.QueryClassNamesAsync);
        });
        services.AddSingleton<DriverBackupService>();
        services.AddSingleton(sp => new DriverManagerViewModel(
            sp.GetRequiredService<IPnpUtilClient>(),
            sp.GetRequiredService<DriverScanner>(),
            sp.GetRequiredService<DriverBackupService>(),
            sp.GetRequiredKeyedService<ILogger>("drivermanager")));
        services.AddSingleton(sp => new DriverManagerView(
            sp.GetRequiredService<DriverManagerViewModel>(),
            sp.GetRequiredKeyedService<ILogger>("drivermanager")));
    }
}
