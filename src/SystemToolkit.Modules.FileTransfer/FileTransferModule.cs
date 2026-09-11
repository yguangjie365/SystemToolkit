using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Services;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>
/// 文件互传 模块（核心模块，不可禁用）。
/// 批次一（V0.5）：电脑↔电脑——UDP 发现 + IP 直连 + WatsonTcp 分片传输（断点续传 +
/// SHA-256 校验 + mtime 还原）+ 接收确认门 + 传输历史。批次二：手机通道（Kestrel +
/// HTTPS + 配对码）与跨通道 PairingService。
/// </summary>
public sealed class FileTransferModule : ModuleBase
{
    public override string Id => "filetransfer";

    public override string DisplayName => "文件互传";

    public override int Order => 5;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<FileTransferView>();

    public override void RegisterServices(IServiceCollection services)
    {
        // 可观测日志（键控注册，审查 2026-09-04 纪律）
        services.AddKeyedSingleton<ILogger>("filetransfer", new FileLogger("filetransfer"));

        services.AddSingleton<IDeviceDiscoveryService, DeviceDiscoveryService>();
        // LOG-3：工厂显式喂键控日志器——原纯类型注册下 ILogger? 可选参数拿 NullLogger，
        // RaiseCompleted 的任务级三字段在生产不会落盘
        services.AddSingleton(sp => new FileTransferService(
            sp.GetRequiredService<IDeviceDiscoveryService>(),
            sp.GetRequiredKeyedService<ILogger>("filetransfer")));
        services.AddSingleton<IFileTransferService>(sp => sp.GetRequiredService<FileTransferService>());
        services.AddSingleton<TransferHistoryService>();
        // 统一配对模型：Web 通道与桌面 TCP 通道共用同一 PairingService 实例（批次二）
        services.AddSingleton<PairingService>();

        // 工厂注册：显式传 UI Dispatcher（后台事件编组）+ 键控日志器（原纯类型注册下
        // ILogger? 可选参数拿到的是 null → NullLogger，键控 "filetransfer" 日志器从未被用上）
        services.AddSingleton(sp => new FileTransferViewModel(
            sp.GetRequiredService<IDeviceDiscoveryService>(),
            sp.GetRequiredService<FileTransferService>(),
            sp.GetRequiredService<TransferHistoryService>(),
            sp.GetRequiredService<IFileWebServer>(),
            sp.GetRequiredService<PairingService>(),
            sp.GetRequiredKeyedService<ILogger>("filetransfer"),
            System.Windows.Application.Current?.Dispatcher));
        services.AddSingleton<FileTransferView>();
    }
}
