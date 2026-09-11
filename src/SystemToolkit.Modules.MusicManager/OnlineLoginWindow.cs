using System.IO;
using System.Windows;
using System.Windows.Controls;
using SystemToolkit.Core.Music.Online;

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

    private OnlineLoginWindow(OnlineProvider provider)
    {
        _provider = provider;
        Title = provider == OnlineProvider.NetEase ? "网易云音乐登录" : "QQ 音乐登录";
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
        _ => throw new ArgumentOutOfRangeException(nameof(_provider)),
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
            System.Diagnostics.Trace.WriteLine($"[Music] 登录窗初始化失败：{ex.Message}");
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
            // 登录页就绪后开始轮询目标 Cookie
            if (_pollTimer is null)
            {
                _pollTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
                };
                _pollTimer.Tick += async (_, _) => await PollCookiesAsync(cookieUri, targetCookies);
                _pollTimer.Start();
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
        catch
        {
            // 轮询失败（导航中/WebView 未就绪）静默重试；窗口仍在，用户可手动关闭
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
        catch
        {
            // 清理尽力而为：超时/WebView 已销毁/Runtime 异常时直接关窗
        }

        try
        {
            Close();
        }
        catch
        {
            // 关闭过程中的布局回调异常无需处理（窗口已标记完成）
        }
    }
}
