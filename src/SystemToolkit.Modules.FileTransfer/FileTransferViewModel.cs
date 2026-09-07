using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>
/// 文件互传页组合根 VM：两个 Tab（电脑互传 / 手机通道占位）+ 模块级共享操作日志。
/// 批次一交付电脑互传全链路；手机通道（Kestrel + HTTPS + 配对码）为批次二。
/// </summary>
public partial class FileTransferViewModel : ObservableObject
{
    /// <summary>确认对话框回调（View 注入；GameManager 同款模式）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    public FileTransferDesktopViewModel Desktop { get; }

    /// <summary>手机通道（批次二）：Kestrel + 二维码 + 一次性配对码。</summary>
    public FileTransferMobileViewModel Mobile { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public ObservableCollection<LogLine> LogLines { get; } = new();

    public FileTransferViewModel(
        IDeviceDiscoveryService discovery,
        FileTransferService transfer,
        TransferHistoryService history,
        IFileWebServer webServer,
        PairingService pairing,
        ILogger? logger = null)
    {
        ILogger effectiveLogger = logger ?? NullLogger.Instance;
        Desktop = new FileTransferDesktopViewModel(discovery, transfer, history, Log, effectiveLogger);
        Mobile = new FileTransferMobileViewModel(webServer, pairing, Log);
    }

    public void AddLog(string message) => LogFeed.Append(LogLines, message, LogFeed.DefaultMaxLines);

    private void Log(string message) => AddLog(message);

    [RelayCommand]
    private void ClearLog() => LogFeed.Clear(LogLines);

    /// <summary>页面 Loaded：加载配置与历史（幂等；服务启停由用户显式操作）。</summary>
    public Task LoadAsync()
    {
        Desktop.Initialize();
        return Task.CompletedTask;
    }
}
