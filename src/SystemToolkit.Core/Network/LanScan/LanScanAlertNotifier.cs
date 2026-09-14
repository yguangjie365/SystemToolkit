using System.Text;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// 局域网告警外呼（落地计划 B3-③，源报告 P1-2）：把匹配的事件推到用户配置的
/// 机器人 webhook（钉钉 / 企业微信 —— 两者都接受 <c>{"msgtype":"text","text":{"content":…}}</c>）。
/// <para>
/// 🔴 <b>本类最重要的一条不是"怎么发"，而是"什么时候不发"</b>：未启用、未配 URL、
/// URL 非法、本轮无匹配事件 —— 这四种情况一律返回 <see cref="LanAlertSendState.Skipped"/>
/// 且**不构造请求、不碰网络**。有单测用一个会记账的 <see cref="HttpMessageHandler"/> 钉死
/// "跳过时调用次数为 0"。
/// </para>
/// <para>
/// 🟠 <b>绝不抛</b>：外呼失败不该把扫描流程带下水（那会把"值守提醒没发出去"升级成"巡检也跑不了"）。
/// 失败走 <see cref="LanAlertSendState.Failed"/> 并由调用方展示。
/// </para>
/// <para>
/// 消息里最多带 <see cref="MaxEventsInMessage"/> 条明细——机器人对文本长度有限制，
/// 一次把 200 条灌进去会被截断甚至拒收，那比少发几条更糟。
/// </para>
/// </summary>
public sealed class LanScanAlertNotifier : IDisposable
{
    /// <summary>单次外呼超时（机器人不可达时不该拖住扫描）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>消息正文最多列出的事件条数（其余只报总数）。</summary>
    public const int MaxEventsInMessage = 10;

    /// <summary>日志动作名（06 册八字段）。</summary>
    public const string ActionName = "LanAlertSend";

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>本实例创建的 <see cref="HttpClient"/> 是否由本类负责释放（注入 handler 时由调用方负责）。</summary>
    private readonly bool _ownsHttp;

    /// <summary>
    /// 缺省用真 HTTP；测试注入 <see cref="HttpMessageHandler"/> 以便断言"跳过时零调用"。
    /// <para>
    /// 🟡 审查 v8-🟡-9：默认 handler 改为 <see cref="SocketsHttpHandler"/> 并**显式**
    /// <c>AllowAutoRedirect = false</c>。自动跟随发生在 handler 内部、每一跳都不经调用方校验，
    /// 而这里的 URL 是用户自配的（无法建白名单）——一旦目标域上有开放 302，**带着告警正文的
    /// POST 就会被带去任意主机**。禁用后 3xx 按非 2xx 处理，如实报"推送失败：HTTP 3xx"。
    /// ⚠️ 刻意不复用 <see cref="SystemToolkit.Core.Utilities.RedirectFollowingHandler"/>：它要求域白名单，
    /// 且不复制请求体（本处是带 JSON body 的 POST）。
    /// </para>
    /// </summary>
    public LanScanAlertNotifier(
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        ILogger? logger = null)
    {
        if (handler is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);
            _ownsHttp = true;
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false);
        }

        _http.Timeout = timeout ?? DefaultTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 释放内部 <see cref="HttpClient"/>（🟡 审查 v8-🟡-9：原先类未实现 <see cref="IDisposable"/>，
    /// 自建的 handler 永不释放）。容器把本类注册为**单例**，关闭时会走这里。
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// 按配置筛选并推送事件。返回结果**总是**带人读说明（含跳过原因），可直接进操作日志。
    /// </summary>
    /// <param name="config">告警配置（外呼闸门就是它）。</param>
    /// <param name="events">本轮事件（新→旧）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<LanAlertSendResult> SendAsync(
        LanScanAlertConfig config,
        IReadOnlyList<LanEvent> events,
        CancellationToken ct = default)
    {
        var effective = LanScanAlertConfig.Normalize(config);

        if (!effective.CanSend)
        {
            // 正常结局：用户没开 / 没配。只说事实，不制造"出错了"的错觉。
            return new LanAlertSendResult(
                LanAlertSendState.Skipped,
                effective.Enabled ? "未配置 Webhook URL，未外呼" : "告警推送未启用，未外呼");
        }

        if (!effective.HasValidUrl)
        {
            // 🟠 配错了要**说出来**：这不是"正常跳过"，而是用户需要修的配置错误。
            _logger.Warn($"[{ActionName}] ⚠ Webhook URL 不是合法的 http/https 地址，本次未外呼");
            return new LanAlertSendResult(LanAlertSendState.Skipped, "Webhook URL 不是合法的 http/https 地址，未外呼");
        }

        List<LanEvent> matched = SelectAlerteable(events, effective);
        if (matched.Count == 0)
        {
            return new LanAlertSendResult(LanAlertSendState.Skipped, "本轮无匹配的推送事件，未外呼");
        }

        string content = ComposeText(matched);
        using LogTiming timing = _logger.Time(ActionName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, effective.WebhookUrl!.Trim())
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        msgtype = "text",
                        text = new { content },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string reason = $"HTTP {(int)response.StatusCode}";
                timing.Complete(LogResult.Failed, LogLevel.Warn, $"[{ActionName}] 告警外呼失败：{reason}");
                return new LanAlertSendResult(LanAlertSendState.Failed, $"推送失败：{reason}");
            }

            timing.Complete(LogResult.Success, LogLevel.Info, $"[{ActionName}] 告警已推送 {matched.Count} 条");
            return new LanAlertSendResult(LanAlertSendState.Success, $"已推送 {matched.Count} 条事件");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户取消：不是失败，属于"已按规范留痕"的中止
            timing.Complete(LogResult.Cancelled, LogLevel.Warn, $"[{ActionName}] 告警外呼被取消");
            return new LanAlertSendResult(LanAlertSendState.Failed, "推送已取消");
        }
        catch (Exception ex)
        {
            // 超时（TaskCanceledException 非用户取消）/ 网络不可达 / 被防火墙拦……
            timing.Complete(LogResult.Failed, LogLevel.Warn, $"[{ActionName}] 告警外呼异常：{ex.Message}", ex);
            return new LanAlertSendResult(LanAlertSendState.Failed, $"推送失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按配置挑出该推送的事件。<see cref="LanEventType.DeviceGone"/> 是软事件（只入历史流），
    /// **任何配置都不会推**——否则关一次机就刷一片"离线"。
    /// </summary>
    internal static List<LanEvent> SelectAlerteable(IReadOnlyList<LanEvent> events, LanScanAlertConfig config)
    {
        var matched = new List<LanEvent>();
        foreach (LanEvent evt in events)
        {
            bool hit = evt.Type switch
            {
                LanEventType.Conflict => config.OnConflict,
                LanEventType.BindingChanged => config.OnBindingChanged,
                LanEventType.NewDevice => config.OnNewDevice,
                _ => false,
            };

            if (hit)
            {
                matched.Add(evt);
            }
        }

        return matched;
    }

    /// <summary>组装机器人文本（钉钉/企微共用一套纯文本）。</summary>
    internal static string ComposeText(IReadOnlyList<LanEvent> matched)
    {
        var sb = new StringBuilder();
        sb.Append("【SystemToolkit · 局域网告警】")
          .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
          .Append('\n');

        foreach (LanEvent evt in matched.Take(MaxEventsInMessage))
        {
            // 🔴 IP 必须显式出现：告警的第一诉求是"哪台/哪个地址"，不能指望 Detail 文本里恰好写了它
            sb.Append("· ").Append(LanEventCsv.TypeText(evt.Type)).Append(' ').Append(evt.Ip)
              .Append(" —— ").Append(evt.Detail).Append('\n');
        }

        if (matched.Count > MaxEventsInMessage)
        {
            sb.Append($"…… 另有 {matched.Count - MaxEventsInMessage} 条，详见应用内事件流\n");
        }

        sb.Append($"共 {matched.Count} 条");
        return sb.ToString();
    }
}
