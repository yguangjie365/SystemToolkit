using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Modules.Settings;

/// <summary>
/// 设置 模块（2026-09-07 落地页面骨架，主人裁定：备份设置并入本模块统一管理）。
/// 依赖只向下取 Core 服务（BackupConfigService），不引用任何业务模块。
/// </summary>
public sealed class SettingsModule : ModuleBase
{
    public override string Id => "settings";

    public override string DisplayName => "设置";

    public override int Order => 10;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<SettingsView>();

    public override void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<ILogger>("settings", new FileLogger("settings"));
        // 工厂注册：接通键控日志器（同 NetManager，反模式存量清理）
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<BackupConfigService>(),
            sp.GetRequiredKeyedService<ILogger>("settings")));
        // 🔴 视图必须 Transient：宿主在主题切换后经 CreateView 重建当前页，
        //    以重新解析 {StaticResource} 派生样式（Style.BasedOn 不支持 DynamicResource）。
        //    View 注册为单例时 CreateView 恒返回同一实例 → PageHost.Content 赋同一对象是 WPF 空操作
        //    → 视图停留在旧主题包的颜色上（2026-09-15 实机“浅色下白字压白底”事故）。
        //    视图是无状态壳（DataContext 由 VM 提供），重建只重置 UI 局部状态。
        services.AddTransient<SettingsView>();
    }
}
