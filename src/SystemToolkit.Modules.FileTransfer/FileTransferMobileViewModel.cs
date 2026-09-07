using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>
/// 「手机通道」Tab：启动 Kestrel Web 服务 → 二维码 + 一次性配对码 → 手机浏览共享目录 /
/// 上传 / 下载。配对码由统一 <see cref="PairingService"/> 提供（与桌面 TCP 通道同一实例）。
/// </summary>
public partial class FileTransferMobileViewModel : ObservableObject
{
    private readonly IFileWebServer _web;
    private readonly PairingService _pairing;
    private readonly Action<string> _log;

    public FileTransferMobileViewModel(IFileWebServer web, PairingService pairing, Action<string> log)
    {
        _web = web;
        _pairing = pairing;
        _log = log;
    }

    /// <summary>确认对话框回调（由组合根转接）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    [ObservableProperty]
    private bool _isWebRunning;

    [ObservableProperty]
    private string _shareDirectory = "";

    [ObservableProperty]
    private string _urlText = "";

    [ObservableProperty]
    private string _pairCodeText = "——————";

    [ObservableProperty]
    private string _pairExpiresText = "";

    [ObservableProperty]
    private ImageSource? _qrImage;

    private System.Windows.Threading.DispatcherTimer? _codeTimer;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task StartWebAsync()
    {
        if (string.IsNullOrWhiteSpace(ShareDirectory) || !Directory.Exists(ShareDirectory))
        {
            _log("[手机] ⚠️ 请先设置存在的共享目录");
            return;
        }

        _busy = true;
        RefreshToggleCanExecute();
        try
        {
            await _web.StartAsync(new TransferSettings(), ShareDirectory.Trim()).ConfigureAwait(true);
            IsWebRunning = true;
            UrlText = _web.LanUrl;
            QrImage = RenderQr(_web.LanUrl);
            RefreshPairingDisplay();
            EnsureCodeTimer();
            _log($"[手机] ✅ Web 服务已启动：{_web.Url}（HTTPS={false}），配对码 10 分钟轮换");
        }
        catch (Exception ex)
        {
            _log("[手机] ❌ Web 服务启动失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshToggleCanExecute();
        }
    }

    private bool _busy;

    private bool CanToggle => !_busy;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task StopWebAsync()
    {
        _busy = true;
        RefreshToggleCanExecute();
        try
        {
            await _web.StopAsync().ConfigureAwait(true);
            IsWebRunning = false;
            UrlText = "";
            QrImage = null;
            PairCodeText = "——————";
            PairExpiresText = "";
            _codeTimer?.Stop();
            _codeTimer = null;
            _log("[手机] Web 服务已停止");
        }
        finally
        {
            _busy = false;
            RefreshToggleCanExecute();
        }
    }

    private void RefreshToggleCanExecute()
    {
        StartWebCommand.NotifyCanExecuteChanged();
        StopWebCommand.NotifyCanExecuteChanged();
    }

    /// <summary>打开共享目录（资源管理器）。</summary>
    [RelayCommand]
    private void OpenShareDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ShareDirectory) && Directory.Exists(ShareDirectory))
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ShareDirectory}\"") { UseShellExecute = true });
        }
    }

    /// <summary>复制配对码到剪贴板。</summary>
    [RelayCommand]
    private void CopyPairCode()
    {
        if (!string.IsNullOrEmpty(PairCodeText) && PairCodeText != "——————")
        {
            System.Windows.Clipboard.SetText(PairCodeText);
            _log("[手机通道] 配对码已复制到剪贴板");
        }
    }

    /// <summary>每秒刷新配对码展示（轮换由 PairingService 惰性完成，这里只反映现状）。</summary>
    private void EnsureCodeTimer()
    {
        if (_codeTimer is not null)
        {
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshPairingDisplay();
        timer.Start();
        _codeTimer = timer;
    }

    private void RefreshPairingDisplay()
    {
        PairCodeText = _pairing.CurrentCode;
        TimeSpan remain = _pairing.ExpiresAt - DateTimeOffset.UtcNow;
        PairExpiresText = remain > TimeSpan.Zero ? $"{(int)remain.TotalMinutes:00}:{remain.Seconds:00} 后轮换" : "即将轮换";
    }

    /// <summary>bool[][] 二维码矩阵 → 白底黑码位图（含 2 模块静区）。</summary>
    private static ImageSource? RenderQr(string content)
    {
        bool[][] matrix;
        try
        {
            matrix = QrMatrix.Create(content);
        }
        catch (ArgumentException)
        {
            return null; // 内容超长等——保留空图，URL 仍可复制
        }

        const int scale = 8;
        const int quiet = 2;
        int modules = matrix.Length;
        int size = (modules + quiet * 2) * scale;
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            int my = y / scale - quiet;
            for (int x = 0; x < size; x++)
            {
                int mx = x / scale - quiet;
                bool dark = mx >= 0 && my >= 0 && mx < modules && my < modules && matrix[my][mx];
                int i = (y * size + x) * 4;
                byte v = dark ? (byte)0 : (byte)0xFF;
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 0xFF;
            }
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        return bitmap;
    }
}
