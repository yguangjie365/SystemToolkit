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
    // 🟡 v18-🟡-3（2026-09-16）：补文件日志通道——RunGuarded 原先只写 UI 面板（`_log`），
    // 异常堆栈不入文件日志，与桌面侧「AddLog + _logger 双通道」纪律不一致。
    // 可选参数（构造签名兼容既有调用点与测试）。
    private readonly SystemToolkit.Core.Contracts.ILogger? _logger;

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
        System.Windows.Threading.Dispatcher? dispatcher = null,
        SystemToolkit.Core.Contracts.ILogger? logger = null)
    {
        _web = web;
        _pairing = pairing;
        _log = log;
        _dispatcher = dispatcher;
        _logger = logger;

        // 会话的签发/撤销/过期都发生在服务端 → 订阅事件刷新列表（可能在任意线程，故走 RunOnUi）
        _web.SessionsChanged += (_, _) => RefreshSessions();
    }

    /// <summary>后台事件 → UI 线程编组（与 Desktop VM 同一模式，理由见其注释）。</summary>
    private void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? d = _dispatcher;
        // 🟡 X-2（两批审查，5 处同款，经评估**保持现状**）：`d is null` 时直执行是**有意**的——
        // ① 生产路径 `Application.Current?.Dispatcher` 在 App.OnStartup（UI 线程）内注入，非 null；
        // ② `SystemToolkit.Worker` 目前是 stub，不构造任何模块 VM ⇒ 生产上走不到本分支；
        // ③ 测试宿主**刻意**传 null（全仓 9 处）——拒绝执行会让 VM 状态永不更新、测试无从断言。
        // 🔴 未来若 worker 化落地（无 UI 线程构造 VM），本分支才真正危险：
        //    届时后台线程会**直接改 UI 集合**（ObservableCollection 跨线程）。改法是拒绝执行并落日志，
        //    但必须**同时**给测试宿主一条「有 Dispatcher」的通道（否则现有 9 处用例全红）。
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
            // 🟡 v18-🟡-3（2026-09-16）：与桌面侧同款「双通道」——UI 面板 + 文件日志。
            _log($"[手机] ⚠️ 界面更新异常：{ex.Message}");
            _logger?.Error("[手机] UI 事件处理器异常", ex);
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
    /// <summary>幂等守卫（🟠-5 审查 v10）：启动钩子与本页 Loaded 都会调 <see cref="Initialize"/>。</summary>
    private bool _initialized;

    /// <summary>
    /// 加载期（<see cref="Initialize"/>）抑制"端口回写 → 配置落盘"的连锁写。
    /// <para>
    /// 🔴 2026-09-16 核实补：桌面侧早已有本守卫（<c>FileTransferDesktopViewModel._loadingConfig</c>，
    /// 注释标 D-🟡-5），手机侧**漏了** —— 两侧 VM 结构对称，却只有桌面侧抑制。
    /// 后果：<see cref="Initialize"/> 给 <see cref="WebPortText"/> / <see cref="HttpsPortText"/> 赋值
    /// 会触发 <c>On*TextChanged</c> → <see cref="SaveAndValidatePorts"/> → 合法即 <see cref="SaveConfig"/>，
    /// 于是**每次进页面/主题重建都多写一次配置文件**（AtomicFile 原子写，无损坏风险，纯无谓 IO）。
    /// </para>
    /// </summary>
    private bool _loadingConfig;

    public void Initialize()
    {
        // 🟠-5 审查 v10：注释声称"幂等"，但原先无守卫 —— 第二次调用会重读配置、重设
        // ShareDirectory（触发 OnShareDirectoryChanged → SaveConfig 写盘一次），并把会话列表
        // 清空重建（已配对设备会闪一下）。此守卫让声明与实现一致。
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        _loadingConfig = true; // 与桌面侧同构：下方给端口赋值会触发 SaveAndValidatePorts ⇒ 加载期不该写盘
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
        finally
        {
            _loadingConfig = false;
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
        // 🟡 D-🟡-4（两批审查）：模块注释承诺“钩子内不抛异常”——这条承诺必须由**代码**保证，
        // 不能依赖“被调用方当前恰好都不抛”的隐式契约（未来任一方法改成会抛即打破）。
        try
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
        catch (Exception ex)
        {
            _log("[手机] ⚠️ 随应用启动失败：" + ex.Message);
        }
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

    partial void OnUseHttpsChanged(bool value)
    {
        SaveAndValidatePorts();
        // 勾选状态直接决定「证书指纹」行的可见性（未启用 HTTPS 时没有证书可核对）
        OnPropertyChanged(nameof(ShowCertFingerprint));
    }

    partial void OnAutoStartWithAppChanged(bool value)
    {
        // 🟠-2 核实 v20：本钩子**不走** SaveAndValidatePorts，故必须自带加载期守卫 ——
        // Initialize 会给 AutoStartWithApp 赋值，不挡的话"加载期不写盘"仍会漏一次。
        if (!_loadingConfig)
        {
            SaveConfig();
            _log($"[手机] 随应用启动：{(value ? "已开启（应用启动即拉起 Web 服务；关闭应用则服务停止）" : "已关闭")}");
        }
    }

    private void SaveAndValidatePorts()
    {
        if (_loadingConfig)
        {
            return; // 🟠-2 核实 v20：加载期不写盘（与桌面侧 D-🟡-5 同口径；原先手机侧漏了本守卫）
        }

        PortErrorText = PortValidator.Validate(
            ("Web 端口", WebPort), ("HTTPS 端口", HttpsPort)) ?? string.Empty;
        OnPropertyChanged(nameof(WebPort));
        OnPropertyChanged(nameof(HttpsPort));
        if (string.IsNullOrEmpty(PortErrorText))
        {
            SaveConfig();
        }
    }

    /// <summary>
    /// 端口生效时机提示。
    /// <para>
    /// 🔴 2026-09-13 实机反馈修正：与电脑互传页同样的缺陷——原文案恒为「已改，重启 Web 服务后生效」，
    /// 判据却是"服务是否在运行"，**没改过也会说"已改"**。改为只描述当前状态与下一步动作。
    /// </para>
    /// </summary>
    public string PortEffectHint => IsWebRunning
        ? "Web 服务运行中：改端口后需先「停止 Web 服务」再「启动」才生效"
        : "启动 Web 服务时生效";

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
        // 🟠-2 核实 v20：同 OnAutoStartWithAppChanged —— 本钩子不走 SaveAndValidatePorts，
        // 必须自带加载期守卫（Initialize 会给 ShareDirectory 赋值）。
        if (!_loadingConfig)
        {
            SaveConfig();
        }
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

    /// <summary>
    /// 是否显示「证书指纹」行。
    /// <para>
    /// 🔴 2026-09-13 实机反馈：「未启用 HTTPS」时这一行仍是 `证书指纹 — [复制]` ——
    /// 一个破折号配一个点了没用的复制按钮，看着像坏掉的控件。没有证书就整行不出现。
    /// </para>
    /// </summary>
    public bool ShowCertFingerprint => UseHttps && !string.IsNullOrEmpty(CertFingerprintFull);

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
        catch (OperationCanceledException)
        {
            // 🟡 v18-🟡-2（2026-09-16）：取消/超时不当成"业务失败"报——与同模块 StopWebAsync、
            // 以及桌面侧 StartTransferAsync（v5 B1 确立）同口径。原先统一落进下面的
            // catch(Exception) 打"启动失败"，措辞误导。
            _log("[手机] ⚠ 启动操作已取消或超时。");
        }
        catch (Exception ex)
        {
            _log("[手机] ❌ Web 服务启动失败：" + ex.Message);
            _logger?.Error("[手机] Web 服务启动失败", ex);
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
            RefreshShareDirectoryFreeSpace(); // 🟡-7：服务停了目录可能已被外部删掉，剩余空间要跟着变
        }
        catch (OperationCanceledException)
        {
            // 取消/超时不伪装成业务失败（与 StartWebAsync 同口径）
            _log("[手机] ⚠️ 停止 Web 服务已取消。");
        }
        catch (Exception ex)
        {
            // 🔴-1 审查 v10：原实现只有 try/finally —— `_web.StopAsync()` 抛异常会被
            // AsyncRelayCommand 吞掉，且 `IsWebRunning` 因赋值在其后而停在 true：
            // 用户点「停止」看不到任何反馈、服务可能仍在跑。与 `StartWebAsync` 同构补兜底。
            _log("[手机] ❌ 停止 Web 服务失败：" + ex.Message);
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
        // 🟠-3 审查 v10：同步命令没有 AsyncRelayCommand 的吞异常层，`Process.Start` 抛
        // Win32Exception（无 explorer / 被策略拦）会直冲 DispatcherUnhandledException 杀进程。
        try
        {
            if (!string.IsNullOrWhiteSpace(ShareDirectory) && Directory.Exists(ShareDirectory))
            {
                // 审查 v5（🟡-12）：ArgumentList 逐参传递，替代手工引号拼接（尾反斜杠会转义闭引号）
                var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add(ShareDirectory);
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            _log("[手机] ⚠️ 打开共享目录失败：" + ex.Message);
        }
    }

    /// <summary>复制配对码到剪贴板。</summary>
    [RelayCommand]
    private void CopyPairCode()
    {
        if (!string.IsNullOrEmpty(PairCodeText) && PairCodeText != "——————")
        {
            // 🟡 审查 v8-🟡-5：裸调 Clipboard 在剪贴板被别的进程占用时抛 ExternalException，
            // 会直冲 DispatchedUnhandledException（命令体虽被 AsyncRelayCommand 吞掉，
            // 但同步命令没有这一层）——同仓先例见 LanScanTabViewModel.CopyRow:664。
            try
            {
                System.Windows.Clipboard.SetText(PairCodeText);
                _log("[手机通道] 配对码已复制到剪贴板");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                _log("[手机通道] ⚠ 剪贴板占用中，配对码复制失败（可稍后重试）");
            }
            catch (Exception ex) // 🟡 核实 v20：同步命令无 AsyncRelayCommand 吞异常层，兜住其余异常
            {
                _log("[手机通道] ❌ 配对码复制失败：" + ex.Message);
                _logger?.Error("[手机通道] 配对码复制失败", ex);
            }
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

        // 🟠-3 审查 v10：同步命令需自兜底——契约未声明 `RevokeSession` 不抛，
        // 异常会直冲 DispatcherUnhandledException（与 CopyPairCode 的 ExternalException 同款处理）。
        try
        {
            if (_web.RevokeSession(session.Id))
            {
                _log($"[手机] 已踢出设备：{session.Label}（{session.IpText}）——它需要重新扫码配对");
            }
        }
        catch (Exception ex)
        {
            _log("[手机] ❌ 踢出设备失败：" + ex.Message);
        }

        RefreshSessions();
    }

    /// <summary>踢出全部已授权设备（本机预览会话保留，否则桌面「打开网页」会失效）。</summary>
    [RelayCommand]
    private void KickAllSessions()
    {
        // 🟠-3 审查 v10：同 KickSession，同步命令自兜底
        try
        {
            int count = _web.RevokeAllSessions();
            _log(count > 0
                ? $"[手机] 已踢出全部 {count} 台设备——它们都需要重新扫码配对"
                : "[手机] 当前没有已授权设备");
        }
        catch (Exception ex)
        {
            _log("[手机] ❌ 踢出全部设备失败：" + ex.Message);
        }

        RefreshSessions();
    }

    /// <summary>复制完整证书指纹（手机端提示「证书已变更」时用来人工核对）。</summary>
    [RelayCommand]
    private void CopyCertFingerprint()
    {
        if (!string.IsNullOrEmpty(CertFingerprintFull))
        {
            // 🟡 审查 v8-🟡-5：同 CopyPairCode —— 剪贴板被占用时不得让异常直冲 Dispatcher
            try
            {
                System.Windows.Clipboard.SetText(CertFingerprintFull);
                _log("[手机] 证书指纹已复制到剪贴板");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                _log("[手机] ⚠ 剪贴板占用中，证书指纹复制失败（可稍后重试）");
            }
            catch (Exception ex) // 🟡 核实 v20：同 CopyPairCode，兜住剪贴板占用以外的异常
            {
                _log("[手机] ❌ 证书指纹复制失败：" + ex.Message);
                _logger?.Error("[手机] 证书指纹复制失败", ex);
            }
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

    /// <summary>
    /// 停掉 1s 配对码节拍（🟡 审查 v8-🟡-6）。
    /// <para>
    /// 🔴 原先**只有** <see cref="StopWebAsync"/> 会停表，而本 VM 是 DI 单例、不随页面销毁——
    /// 切走页面后 1s 循环仍在跑（无谓的 UI 刷新与 Dispatcher 唤醒）。页面生命周期钩子
    /// （<c>FileTransferView.Unloaded</c>）与窗口失活都走这里。可重复调用。
    /// </para>
    /// </summary>
    public void PauseTimer()
    {
        _codeTimer?.Stop();
        _codeTimer = null;
    }

    /// <summary>
    /// 恢复 1s 配对码节拍（页面重新挂回时调用）。幂等：服务没在跑就没有节拍可言。
    /// </summary>
    public void ResumeTimer()
    {
        if (IsWebRunning)
        {
            EnsureCodeTimer();
        }
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
        // 指纹到货/消失都直接决定「证书指纹」那一行显不显示
        OnPropertyChanged(nameof(ShowCertFingerprint));
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
