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
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;

    /// <remarks>
    /// 🟡 审查 2026-09-10（🟡-13）曾记录：本 VM 只有 <c>DispatcherTimer</c>（Tick 本就在 UI 线程），
    /// 无需编组，故不接 <c>Dispatcher</c>。
    /// <para>
    /// **2026-09-13 起前提已变**：会话列表要订阅 <see cref="IFileWebServer.SessionsChanged"/>
    /// （由请求线程 / 后台线程触发），于是按那条约定补上「显式 Dispatcher 注入 + 死线程检测 +
    /// BeginInvoke」——约定触发时就照约定做，而不是抓 <c>Application.Current?.Dispatcher</c>
    /// （Application 为 null 时静默跳过）或用同步 <c>Invoke</c>（死锁风险）。测试传 null → 直执行。
    /// </para>
    /// </remarks>
    public FileTransferMobileViewModel(
        IFileWebServer web,
        PairingService pairing,
        Action<string> log,
        System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _web = web;
        _pairing = pairing;
        _log = log;
        _dispatcher = dispatcher;

        // 会话的签发/撤销/过期都发生在服务端 → 订阅事件刷新列表（可能在任意线程，故走 RunOnUi）
        _web.SessionsChanged += (_, _) => RefreshSessions();
    }

    /// <summary>后台事件 → UI 线程编组（与 Desktop VM 同一模式，理由见其注释）。</summary>
    private void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? d = _dispatcher;
        if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive)
        {
            RunGuarded(action);
        }
        else if (d.CheckAccess())
        {
            RunGuarded(action);
        }
        else
        {
            d.BeginInvoke(() => RunGuarded(action));
        }
    }

    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log($"[手机] ⚠️ 界面更新异常：{ex.Message}");
        }
    }

    private static readonly string MobileConfigPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "net", "filetransfer-mobile.json");

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// 手机通道配置（2026-09-13 批次 P3 扩容：共享目录 → 共享目录 + 四个端口相关项 + 随应用启动）。
    /// <para>
    /// 🔴 端口必须落盘：协议 §6.1 写明端口"待定"，用户改了端口却重启后回到默认值，等于每次都要重设。
    /// </para>
    /// </summary>
    private sealed record MobileConfig(string? ShareDirectory)
    {
        public int WebPort { get; init; } = 18890;

        public int HttpsPort { get; init; } = 18891;

        public bool UseHttps { get; init; }

        /// <summary>随应用启动：应用一开就拉起 Web 服务（不做托盘常驻——关掉应用服务即停）。</summary>
        public bool AutoStartWithApp { get; init; }
    }

    /// <summary>
    /// 页面 Loaded **或宿主启动钩子**都会调用（幂等）：恢复配置、刷新会话与证书指纹。
    /// </summary>
    public void Initialize()
    {
        try
        {
            if (File.Exists(MobileConfigPath))
            {
                MobileConfig? config = System.Text.Json.JsonSerializer.Deserialize<MobileConfig>(
                    File.ReadAllText(MobileConfigPath), JsonOpts);
                if (config is not null)
                {
                    if (!string.IsNullOrWhiteSpace(config.ShareDirectory))
                    {
                        ShareDirectory = config.ShareDirectory;
                    }
                    // 只在范围合法时采纳：配置文件被手改坏时退回默认端口，而不是拿一个非法值去绑定
                    if (PortValidator.IsInRange(config.WebPort))
                    {
                        WebPortText = config.WebPort.ToString();
                    }
                    if (PortValidator.IsInRange(config.HttpsPort))
                    {
                        HttpsPortText = config.HttpsPort.ToString();
                    }
                    UseHttps = config.UseHttps;
                    AutoStartWithApp = config.AutoStartWithApp;
                }
            }
        }
        catch (Exception ex)
        {
            _log("[手机] ⚠️ 配置读取失败（使用默认值）：" + ex.Message);
        }

        RefreshShareDirectoryFreeSpace();

        // 页面重入（切 Tab 回来）时把会话列表与证书指纹拉成当前状态
        RefreshSessions();
    }

    /// <summary>配置持久化（共享目录 + 端口 + 随应用启动；原子写）。</summary>
    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MobileConfigPath)!);
            AtomicFile.WriteAllText(MobileConfigPath, System.Text.Json.JsonSerializer.Serialize(
                new MobileConfig(ShareDirectory)
                {
                    WebPort = WebPort,
                    HttpsPort = HttpsPort,
                    UseHttps = UseHttps,
                    AutoStartWithApp = AutoStartWithApp,
                }, JsonOpts));
        }
        catch (Exception ex)
        {
            _log("[手机] ⚠️ 配置保存失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 宿主启动钩子调用入口（2026-09-13 批次 P3）：勾了「随应用启动」且共享目录可用才拉起 Web 服务。
    /// <para>
    /// 幂等：已在跑则什么都不做；共享目录不可用**不静默**——记一条日志说明为什么没起来
    /// （用户勾了开关却看不到服务，必须能从日志追到原因）。
    /// </para>
    /// </summary>
    public async Task TryAutoStartWithAppAsync()
    {
        Initialize();
        if (!AutoStartWithApp || IsWebRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ShareDirectory) || !Directory.Exists(ShareDirectory))
        {
            _log("[手机] ⚠️ 已勾选「随应用启动」，但共享目录不存在 → 本次未启动 Web 服务");
            return;
        }

        await StartWebAsync().ConfigureAwait(true);
    }

    /// <summary>确认对话框回调（由组合根转接）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortEffectHint))]
    private bool _isWebRunning;

    [ObservableProperty]
    private string _shareDirectory = "";

    /// <summary>
    /// 共享目录所在盘剩余空间文案（2026-09-13 批次 P3 ⑯）。上传会落在这里，
    /// 用户看到"还剩多少"才知道该不该清盘；无法判定时如实说"无法判定"，不用 0 冒充。
    /// </summary>
    [ObservableProperty]
    private string _shareDirectoryFreeText = "";

    // ── 端口与启动行为（2026-09-13 批次 P3 ⑰⑱） ──

    // 🔴 用字符串承载输入（同桌面 VM）：绑定 int 时 "" / "18" 这类中间态转换失败是静默的
    [ObservableProperty]
    private string _webPortText = "18890";

    [ObservableProperty]
    private string _httpsPortText = "18891";

    [ObservableProperty]
    private bool _useHttps;

    /// <summary>随应用启动（不做托盘常驻：关掉应用服务即停，手机端会断）。</summary>
    [ObservableProperty]
    private bool _autoStartWithApp;

    /// <summary>端口校验错误文案（空串 = 无错）。</summary>
    [ObservableProperty]
    private string _portErrorText = "";

    /// <summary>校验通过的 Web 端口（非法时为 0；调用方先看 <see cref="PortErrorText"/>）。</summary>
    public int WebPort => int.TryParse(WebPortText?.Trim(), out int port) ? port : 0;

    /// <summary>校验通过的 HTTPS 端口（非法时为 0）。</summary>
    public int HttpsPort => int.TryParse(HttpsPortText?.Trim(), out int port) ? port : 0;

    partial void OnWebPortTextChanged(string value) => SaveAndValidatePorts();

    partial void OnHttpsPortTextChanged(string value) => SaveAndValidatePorts();

    partial void OnUseHttpsChanged(bool value) => SaveAndValidatePorts();

    partial void OnAutoStartWithAppChanged(bool value)
    {
        SaveConfig();
        _log($"[手机] 随应用启动：{(value ? "已开启（应用启动即拉起 Web 服务；关闭应用则服务停止）" : "已关闭")}");
    }

    private void SaveAndValidatePorts()
    {
        PortErrorText = PortValidator.Validate(
            ("Web 端口", WebPort), ("HTTPS 端口", HttpsPort)) ?? string.Empty;
        OnPropertyChanged(nameof(WebPort));
        OnPropertyChanged(nameof(HttpsPort));
        if (string.IsNullOrEmpty(PortErrorText))
        {
            SaveConfig();
        }
    }

    /// <summary>服务在跑时改端口必须重启才生效——界面要明说，不能让用户以为已生效。</summary>
    public string PortEffectHint => IsWebRunning
        ? "已改，重启 Web 服务后生效"
        : "启动 Web 服务时生效";

    private void RefreshUrlPreview()
    {
        if (!IsWebRunning)
        {
            return;
        }

        UrlText = _web.LanUrl;
    }

    private void RefreshShareDirectoryFreeSpace()
    {
        string dir = ShareDirectory?.Trim() ?? string.Empty;
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            ShareDirectoryFreeText = string.Empty;
            return;
        }

        long? free = DiskSpaceUtil.TryGetAvailableFreeBytes(dir);
        ShareDirectoryFreeText = free is null
            ? "剩余空间无法判定（网络路径或卷未就绪）"
            : $"剩余 {FormatUtil.FormatSize(free.Value)}（手机上传会落在这里）";
    }

    partial void OnShareDirectoryChanged(string value)
    {
        RefreshShareDirectoryFreeSpace();
        SaveConfig();
    }

    [ObservableProperty]
    private string _urlText = "";

    [ObservableProperty]
    private string _pairCodeText = "——————";

    [ObservableProperty]
    private string _pairExpiresText = "";

    [ObservableProperty]
    private ImageSource? _qrImage;

    /// <summary>已授权设备（会话）列表：手机配对成功一台就多一行（协议 §5.3）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<WebSessionVm> Sessions { get; } = new();

    /// <summary>是否有已授权设备——空态文案与「全部踢出」的显隐依据。</summary>
    [ObservableProperty]
    private bool _hasSessions;

    /// <summary>会话计数文案（"已授权 2 台设备" / "暂无已授权设备"）。</summary>
    [ObservableProperty]
    private string _sessionsSummary = "暂无已授权设备";

    /// <summary>证书指纹短形式（前 8 组 hex），供人工与手机端显示的指纹逐段核对。</summary>
    [ObservableProperty]
    private string _certFingerprintText = "—";

    /// <summary>完整证书指纹（「复制」用；界面上显示完整 64 位会被截断得没法核对）。</summary>
    [ObservableProperty]
    private string _certFingerprintFull = "";

    private System.Windows.Threading.DispatcherTimer? _codeTimer;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task StartWebAsync()
    {
        if (string.IsNullOrWhiteSpace(ShareDirectory) || !Directory.Exists(ShareDirectory))
        {
            _log("[手机] ⚠️ 请先设置存在的共享目录");
            return;
        }

        if (!string.IsNullOrEmpty(PortErrorText))
        {
            _log($"[手机] ⚠️ 端口设置非法，未启动：{PortErrorText}");
            return;
        }

        _busy = true;
        RefreshToggleCanExecute();
        try
        {
            // 端口与 HTTPS 从设置传入（原实现传 `new TransferSettings()` = 全默认值，
            // 用户在界面改的端口会被无声忽略——那正是"界面在骗人"）
            var settings = new TransferSettings
            {
                WebPort = WebPort,
                HttpsPort = HttpsPort,
                UseHttps = UseHttps,
                ShareDirectory = ShareDirectory.Trim(),
            };
            await _web.StartAsync(settings, ShareDirectory.Trim()).ConfigureAwait(true);
            IsWebRunning = true;
            UrlText = _web.LanUrl;
            QrImage = RenderQr(_web.LanUrl);
            RefreshPairingDisplay();
            EnsureCodeTimer();
            SaveConfig();
            RefreshShareDirectoryFreeSpace();
            _log($"[手机] ✅ Web 服务已启动：{_web.LanUrl}（{(_web.IsHttps ? "HTTPS 加密" : "⚠️ HTTP 未加密")}），配对码 10 分钟轮换");
            RefreshSessions(); // 取当前证书指纹（会话列表此刻还是空的，配对后才会有）
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
            RefreshSessions(); // 服务停下 = 全部会话作废，列表与指纹一起归零（不能留着显示成"仍然有效"）
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
            // 审查 v5（🟡-12）：ArgumentList 逐参传递，替代手工引号拼接（尾反斜杠会转义闭引号）
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(ShareDirectory);
            System.Diagnostics.Process.Start(psi);
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

    /// <summary>踢出单个会话：该设备的令牌立即失效（需重新扫码），其它设备与本机预览不受影响。</summary>
    [RelayCommand]
    private void KickSession(WebSessionVm? session)
    {
        if (session is null)
        {
            return;
        }

        if (_web.RevokeSession(session.Id))
        {
            _log($"[手机] 已踢出设备：{session.Label}（{session.IpText}）——它需要重新扫码配对");
        }

        RefreshSessions();
    }

    /// <summary>踢出全部已授权设备（本机预览会话保留，否则桌面「打开网页」会失效）。</summary>
    [RelayCommand]
    private void KickAllSessions()
    {
        int count = _web.RevokeAllSessions();
        _log(count > 0
            ? $"[手机] 已踢出全部 {count} 台设备——它们都需要重新扫码配对"
            : "[手机] 当前没有已授权设备");
        RefreshSessions();
    }

    /// <summary>复制完整证书指纹（手机端提示「证书已变更」时用来人工核对）。</summary>
    [RelayCommand]
    private void CopyCertFingerprint()
    {
        if (!string.IsNullOrEmpty(CertFingerprintFull))
        {
            System.Windows.Clipboard.SetText(CertFingerprintFull);
            _log("[手机] 证书指纹已复制到剪贴板");
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

    /// <summary>
    /// 重建会话列表与证书指纹展示。任意线程可调用——内部编组到 UI 线程
    /// （会话事件由请求线程触发）。
    /// </summary>
    private void RefreshSessions() => RunOnUi(() =>
    {
        Sessions.Clear();
        foreach (WebSessionInfo info in _web.Sessions)
        {
            Sessions.Add(new WebSessionVm(info));
        }

        HasSessions = Sessions.Count > 0;
        SessionsSummary = Sessions.Count > 0 ? $"已授权 {Sessions.Count} 台设备" : "暂无已授权设备";

        string fingerprint = _web.CertFingerprint;
        CertFingerprintFull = fingerprint;
        CertFingerprintText = FormatFingerprintShort(fingerprint);
    });

    /// <summary>
    /// 指纹短形式：前 8 组（16 位 hex）+ 省略号。
    /// 完整 64 位铺在界面上反而没法逐段核对，「复制」按钮给完整值。
    /// </summary>
    private static string FormatFingerprintShort(string raw)
        => string.IsNullOrEmpty(raw)
            ? "—"
            : string.Join(':', raw[..Math.Min(16, raw.Length)].Chunk(2).Select(static c => new string(c))) + "…";

    /// <summary>已授权设备行（会话列表的一行）。</summary>
    public sealed class WebSessionVm
    {
        private readonly WebSessionInfo _info;

        public WebSessionVm(WebSessionInfo info) => _info = info;

        /// <summary>会话 Id（踢出时带回服务端）。</summary>
        public string Id => _info.Id;

        /// <summary>设备标签（由 User-Agent 归纳）。</summary>
        public string Label => string.IsNullOrWhiteSpace(_info.Label) ? "未知设备" : _info.Label;

        /// <summary>来源 IP。</summary>
        public string IpText => string.IsNullOrWhiteSpace(_info.Ip) ? "—" : _info.Ip;

        /// <summary>最近访问（相对时间）——判断"这台还在用吗"的唯一依据。</summary>
        public string LastSeenText => DescribeRelative(_info.LastSeenAt);

        /// <summary>悬停提示：签发时间（何时开始有权访问）。</summary>
        public string DetailTip => "签发于 " + _info.CreatedAt.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 授权性质文案（P3 ⑲）：免配对显示「已记住 · 剩余 N 天」，否则「本次会话 8 小时」。
        /// <para>
        /// 必须区分开：两者失效时机完全不同（一个重启后仍然有效、一个重启即失效），
        /// 混成一句话会让用户对"这台设备还能不能访问"判断错误。
        /// </para>
        /// </summary>
        public string TrustText
        {
            get
            {
                if (!_info.Trusted)
                {
                    return "本次会话 8 小时";
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                int days = _info.ExpiresAt <= now
                    ? 0
                    : (int)Math.Ceiling((_info.ExpiresAt - now).TotalDays);
                return days <= 0 ? "已记住（今日到期）" : $"已记住 · 剩余 {days} 天";
            }
        }

        /// <summary>是否免配对（XAML 用它在行上挂不同底色/徽章）。</summary>
        public bool IsTrusted => _info.Trusted;

        /// <summary>操作按钮文案：免配对是「撤销」，普通会话是「踢出」（撤销会连磁盘凭据一起删）。</summary>
        public string RevokeText => _info.Trusted ? "撤销" : "踢出";

        private static string DescribeRelative(DateTimeOffset when)
        {
            TimeSpan age = DateTimeOffset.UtcNow - when;
            if (age < TimeSpan.FromMinutes(1))
            {
                return "刚刚";
            }

            if (age < TimeSpan.FromHours(1))
            {
                return $"{(int)age.TotalMinutes} 分钟前";
            }

            if (age < TimeSpan.FromDays(1))
            {
                return $"{(int)age.TotalHours} 小时前";
            }

            return age < TimeSpan.FromDays(2) ? "昨天" : $"{(int)age.TotalDays} 天前";
        }
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
