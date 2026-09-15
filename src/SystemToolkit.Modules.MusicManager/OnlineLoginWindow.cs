using System.IO;
using System.Windows;
using System.Windows.Controls;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 在线音乐平台登录窗（WebView2 承载官方登录页，审查 O3/O4 三方式之一）。
/// 轮询平台 Cookie（网易云=MUSIC_U；QQ=uin/qqmusic_key），捕获成功即回传并关窗。
/// </summary>
/// <remarks>
/// 与 NexBox music_open_login_window 同思路：独立 WebView 窗口、用户数据目录隔离、
/// 禁用 Chromium 媒体会话（避免与本项目 SMTC 冲突）。Cookie 经 <see cref="IOnlineCredentialStore"/>
/// 由调用方加密存储（本窗口只负责捕获与回传）。
/// </remarks>
public sealed class OnlineLoginWindow : Window
{
    private const int PollIntervalMs = 1500;

    private readonly OnlineProvider _provider;
    private readonly TaskCompletionSource<string?> _cookieTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private System.Windows.Threading.DispatcherTimer? _pollTimer;
    private Microsoft.Web.WebView2.Wpf.WebView2? _webView;
    private bool _completed;

    /// <summary>Cookie 轮询连续失败计数（仅用于"稳定失败"留痕一次，见 PollCookiesAsync）。</summary>
    private int _pollFailures;

    private OnlineLoginWindow(OnlineProvider provider)
    {
        _provider = provider;
        Title = provider == OnlineProvider.NetEase ? "网易云音乐登录" : "QQ 音乐登录";
        // 🟡 V16-1（2026-09-15）：深色主题下本窗标题栏仍是系统浅色 —— 全仓 10 个窗口里**唯一漏接线**的一处
        // （其余 9 个已接）。契约见 TitleBarThemeWiring：窗口**构造期**调用一次即可，句柄无需就绪。
        TitleBarThemeWiring.Attach(this);
        Width = 960;
        Height = 760;
        MinWidth = 780;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TextBlock
        {
            Text = "正在加载登录页…",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Loaded += OnLoaded;
        Closed += (_, _) => Complete(null);
    }

    /// <summary>打开登录窗并等待用户完成登录；关闭窗口/取消返回 null。</summary>
    public static Task<string?> ShowAsync(Window owner, OnlineProvider provider)
    {
        var window = new OnlineLoginWindow(provider)
        {
            Owner = owner,
        };
        window.Show();
        return window._cookieTcs.Task;
    }

    private (string LoginUrl, string CookieUri, string[] TargetCookies) Profile() => _provider switch
    {
        // 登录入口 URL 与目标 Cookie 对照 NexBox music_open_login_window / qr 捕获口径
        OnlineProvider.NetEase => ("https://music.163.com/#/login", "https://music.163.com", ["MUSIC_U"]),
        OnlineProvider.QQMusic => ("https://y.qq.com/n/ryqq/profile", "https://y.qq.com", ["uin", "qqmusic_key"]),
        // 🟠 V13-M5（2026-09-14 审查）：原写法 `new ArgumentOutOfRangeException(nameof(_provider))`
        // 只传参数名、**丢掉实际值**（诊断时看不出是哪个平台漏配），且无说明文案。
        _ => throw new ArgumentOutOfRangeException(
            nameof(_provider), _provider, "未预置该平台的登录入口 URL/Cookie 名单"),
    };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 审查 Y3（2026-09-10）：async void 异常直冲 Dispatcher（WebView2 Runtime 缺席、
        // 用户在 await 期间关窗都会抛）——async void 必须整体兜底，失败落错误提示并安全关闭
        try
        {
            await InitLoginWebViewAsync();
        }
        catch (Exception ex)
        {
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Error, "music",
                "登录窗初始化失败：" + ex.Message, ex,
                action: "OnlineLogin", outcome: SystemToolkit.Core.Logging.LogResult.Failed));
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "登录组件初始化失败：\n" + ex.Message + "\n\n（请确认已安装 Microsoft Edge WebView2 Runtime）",
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
            };
            _cookieTcs.TrySetResult(null);
        }
    }

    private async Task InitLoginWebViewAsync()
    {
        (string loginUrl, string cookieUri, string[] targetCookies) = Profile();
        var webView = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            Margin = new Thickness(0),
        };
        Content = webView;
        _webView = webView;

        string userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "webview2", _provider == OnlineProvider.NetEase ? "netease-login" : "qq-login");

        var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
        {
            // 与 NexBox 一致：禁用 Chromium 媒体会话（避免与本项目 SMTC 冲突）
            AdditionalBrowserArguments = "--disable-features=MediaSessionService,HardwareMediaKeyHandling",
        };
        Microsoft.Web.WebView2.Core.CoreWebView2Environment environment =
            await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataDir, options: options);

        await webView.EnsureCoreWebView2Async(environment);

        // 与 NexBox 一致：每次打开先清浏览数据（旧 Cookie 不残留、避免脏登录态）
        await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
        await webView.CoreWebView2.Profile.ClearBrowsingDataAsync();

        webView.NavigationCompleted += async (_, _) =>
        {
            // 🔴 V13-M1：本 lambda 是 async void 且**由 WebView2 触发** —— 它的异常不会被 OnLoaded 的
            // try 接住（那只覆盖 `await InitLoginWebViewAsync()`），会直冲 DispatcherUnhandledException。
            // 整体兜底：失败按"未取到 Cookie"收窗（不让窗口悬停在"正在加载…"/永不捕获 Cookie），并留痕。
            try
            {
                // 登录页就绪后开始轮询目标 Cookie
                if (_pollTimer is null)
                {
                    _pollTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
                    };
                    _pollTimer.Tick += async (_, _) =>
                    {
                        // 🟡 v10-1（§7.6 async void lambda 整体兜底）：PollCookiesAsync 内部已 catch，
                        // 但 lambda 调度层再包一层——异常走本窗可见路径而非 Dispatcher 全局兜底
                        try
                        {
                            await PollCookiesAsync(cookieUri, targetCookies);
                        }
                        catch (Exception ex)
                        {
                            Complete(null); // 轮询链彻底崩坏：按"未取到 Cookie"收窗，不让窗口悬死
                            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                                SystemToolkit.Core.Logging.LogLevel.Error, "music",
                                "登录窗轮询异常终止：" + ex.Message, ex,
                                action: "OnlineLogin", outcome: SystemToolkit.Core.Logging.LogResult.Failed));
                        }
                    };
                    _pollTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Complete(null);
                SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                    SystemToolkit.Core.Logging.LogLevel.Warn, "music",
                    "登录窗导航完成回调异常，按未取到 Cookie 收窗：" + ex.Message, ex,
                    action: "OnlineLogin", outcome: SystemToolkit.Core.Logging.LogResult.Failed));
            }
        };

        _webView.Source = new Uri(loginUrl);
    }

    private async Task PollCookiesAsync(string cookieUri, string[] targetCookies)
    {
        if (_webView?.CoreWebView2 is null || _completed)
        {
            return;
        }

        try
        {
            System.Collections.Generic.IReadOnlyList<Microsoft.Web.WebView2.Core.CoreWebView2Cookie> cookies =
                await _webView.CoreWebView2.CookieManager.GetCookiesAsync(cookieUri);
            List<string> matched = [];
            foreach (Microsoft.Web.WebView2.Core.CoreWebView2Cookie cookie in cookies)
            {
                if (targetCookies.Contains(cookie.Name)
                    && !string.IsNullOrWhiteSpace(cookie.Value))
                {
                    matched.Add($"{cookie.Name}={cookie.Value}");
                }
            }

            // 目标 Cookie 全部就位才算登录成功（网易 1 项；QQ 需 uin + qqmusic_key）
            if (matched.Count >= targetCookies.Length)
            {
                Complete(string.Join("; ", matched));
            }
        }
        catch (Exception ex)
        {
            // 轮询失败（导航中 / WebView 未就绪）属预期，静默重试；窗口仍在，用户可手动关闭。
            // 🟠 v11~v14 后续批次：单次失败可以静默，但**稳定失败**（cookie 站点被 WAF 拦、
            // CookieManager 异常）会让窗口永远停在"正在加载…"且零留痕。第 10 次
            //（≈15s，PollIntervalMs=1500）落一条 Warn，但**不关窗**（不阻断用户手动操作）。
            _pollFailures++;
            if (_pollFailures == 10)
            {
                SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                    SystemToolkit.Core.Logging.LogLevel.Warn, "music",
                    $"登录窗 Cookie 轮询连续失败 {_pollFailures} 次（窗口仍在等待，用户可手动关闭）：{ex.Message}", ex,
                    action: "OnlineLogin", outcome: SystemToolkit.Core.Logging.LogResult.Failed));
            }
        }
    }

    private void Complete(string? cookie)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _pollTimer?.Stop();
        _cookieTcs.TrySetResult(cookie);
        _ = CloseWithCleanupAsync();
    }

    /// <summary>
    /// 审查 v5（🟡-5）：关窗前清浏览数据——登录成功捕获 Cookie 后，Chromium profile
    /// 内仍留存已登录会话副本（虽有 Chromium 自加密，但属本可即时清除的落盘凭据）。
    /// 与打开时的清理对称；清理失败不阻断关窗。
    /// </summary>
    private async Task CloseWithCleanupAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 is not null)
            {
                // 审查 v6（O-1d）：清理必须带超时上界——Chromium 卡住时不能让窗口悬留不关
                await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // 清理尽力而为：超时/WebView 已销毁/Runtime 异常时直接关窗。
            // 但"失败不阻断"≠"可以不记录"（本仓 P0-B 口径）——浏览数据没清干净会让
            // 下次登录复用旧 Cookie，症状是"换了账号仍显示旧登录态"。
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Warn, "music",
                "登录窗清理浏览数据失败（不影响关闭）：" + ex.Message, ex));
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            // 关闭过程中的布局回调异常无需处理（窗口已标记完成）——但同样留痕：
            // 本方法由 `_ = CloseWithCleanupAsync()` fire-and-forget 触发，不留痕即完全静默。
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Warn, "music",
                "登录窗关闭回调异常（窗口可能未关闭）：" + ex.Message, ex));
        }
    }
}
