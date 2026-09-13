using System.Net;
using System.Text.Json;
using SystemToolkit.Core.Network.LanScan;
using SystemToolkit.Infrastructure.Network;

namespace SystemToolkit.Tests.Network;

/// <summary>
/// 局域网告警（B3-②/③）测试。
/// <para>
/// 最要紧的一组是<b>"跳过时零外呼"</b>：这个功能本质是"往局域网外发数据"，
/// 用户没开、没配、配错了、本轮没事 —— 这四种情况下**连一个请求都不该发出去**。
/// 用记账型 <see cref="HttpMessageHandler"/> 把调用次数钉成断言，而不是靠读代码自我保证。
/// </para>
/// </summary>
public class LanScanAlertTests
{
    private static LanEvent Conflict(string ip = "192.168.1.7", string detail = "冲突详情") =>
        new(LanEventType.Conflict, ip, "3C:00:00:00:00:1F", "9A:00:00:00:00:04", detail,
            new DateTimeOffset(2026, 9, 13, 12, 3, 11, TimeSpan.Zero));

    // ═══════════════ 配置闸门 ═══════════════

    [Theory]
    [InlineData(false, "https://example.com/hook", false)] // 有 URL 但没启用
    [InlineData(true, null, false)]                        // 启用了但没 URL
    [InlineData(true, "   ", false)]                       // 启用了但 URL 是空白
    [InlineData(true, "https://example.com/hook", true)]
    public void Config_CanSend_RequiresBothEnabledAndNonBlankUrl(bool enabled, string? url, bool expected) =>
        Assert.Equal(expected, new LanScanAlertConfig(enabled, url).CanSend);

    [Theory]
    [InlineData("https://oapi.dingtalk.com/robot/send?access_token=x", true)]
    [InlineData("http://127.0.0.1:8080/hook", true)]
    [InlineData("ftp://example.com/x", false)]
    [InlineData("file:///C:/x", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Config_HasValidUrl_OnlyHttpAndHttps(string? url, bool expected) =>
        Assert.Equal(expected, new LanScanAlertConfig(true, url).HasValidUrl);

    // ═══════════════ 🔴 跳过路径：必须零外呼 ═══════════════

    [Theory]
    // 未启用 / 未配 URL / URL 非法 / 本轮无匹配事件 —— 四种都不得产生请求
    [InlineData(false, "https://example.com/hook", true, true)]   // 没启用
    [InlineData(true, null, true, true)]                          // 没配 URL
    [InlineData(true, "   ", true, true)]                         // URL 是空白
    [InlineData(true, "ftp://example.com/x", true, true)]         // URL 协议不对
    [InlineData(true, "https://example.com/hook", false, false)]  // URL 合法但没有任何开关命中
    public async Task Notifier_ShouldNotEgress_WhenGateNotPassed(
        bool enabled, string? url, bool onConflict, bool onOther)
    {
        var handler = new RecordingHandler();
        var notifier = new LanScanAlertNotifier(handler);
        var config = new LanScanAlertConfig(enabled, url, onConflict, onOther, onOther);

        LanAlertSendResult result = await notifier.SendAsync(config, new[] { Conflict() });

        Assert.Equal(LanAlertSendState.Skipped, result.State);
        Assert.False(result.Attempted);
        Assert.Equal(0, handler.Calls); // 🔴 红线：跳过 ≠ 只是不关心结果，而是**根本没发**
    }

    [Fact]
    public async Task Notifier_ShouldNotEgress_WhenNoEventsAtAll()
    {
        var handler = new RecordingHandler();
        var notifier = new LanScanAlertNotifier(handler);

        LanAlertSendResult result = await notifier.SendAsync(
            new LanScanAlertConfig(true, "https://example.com/hook"), Array.Empty<LanEvent>());

        Assert.Equal(LanAlertSendState.Skipped, result.State);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>DeviceGone 是软事件（关机一整片），任何配置都不该推——否则"关一次机刷一片"。</summary>
    [Fact]
    public async Task Notifier_NeverPushes_DeviceGone()
    {
        var handler = new RecordingHandler();
        var notifier = new LanScanAlertNotifier(handler);
        var gone = new LanEvent(LanEventType.DeviceGone, "192.168.1.9", null, null, "离线", DateTimeOffset.Now);

        LanAlertSendResult result = await notifier.SendAsync(
            new LanScanAlertConfig(true, "https://example.com/hook", true, true, true), new[] { gone });

        Assert.Equal(LanAlertSendState.Skipped, result.State);
        Assert.Equal(0, handler.Calls);
    }

    // ═══════════════ 外呼路径 ═══════════════

    [Fact]
    public async Task Notifier_PostsTextPayload_AndReportsCount()
    {
        var handler = new RecordingHandler();
        var notifier = new LanScanAlertNotifier(handler);
        var config = new LanScanAlertConfig(true, "https://example.com/hook");

        LanAlertSendResult result = await notifier.SendAsync(config, new[] { Conflict() });

        Assert.Equal(LanAlertSendState.Success, result.State);
        Assert.Equal(1, handler.Calls);

        // 钉协议形状：钉钉/企微都吃 {"msgtype":"text","text":{"content":…}}
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("text", doc.RootElement.GetProperty("msgtype").GetString());
        string content = doc.RootElement.GetProperty("text").GetProperty("content").GetString()!;
        Assert.Contains("局域网告警", content, StringComparison.Ordinal);
        Assert.Contains("192.168.1.7", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notifier_FiltersByTypeFlags()
    {
        var handler = new RecordingHandler();
        var notifier = new LanScanAlertNotifier(handler);
        var newDevice = new LanEvent(LanEventType.NewDevice, "192.168.1.31", null, "AA:BB:CC:DD:EE:FF", "新设备", DateTimeOffset.Now);

        // 只勾冲突 → 纯新设备事件应当不发（零外呼）
        LanAlertSendResult onlyConflict = await notifier.SendAsync(
            new LanScanAlertConfig(true, "https://example.com/hook", OnConflict: true, OnNewDevice: false),
            new[] { newDevice });
        Assert.Equal(LanAlertSendState.Skipped, onlyConflict.State);
        Assert.Equal(0, handler.Calls);

        // 勾上新设备 → 发
        LanAlertSendResult withNew = await notifier.SendAsync(
            new LanScanAlertConfig(true, "https://example.com/hook", OnConflict: true, OnNewDevice: true),
            new[] { newDevice });
        Assert.Equal(LanAlertSendState.Success, withNew.State);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Notifier_NonSuccessStatus_IsFailed_NotSuccess()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.InternalServerError };
        var notifier = new LanScanAlertNotifier(handler);

        LanAlertSendResult result = await notifier.SendAsync(
            new LanScanAlertConfig(true, "https://example.com/hook"), new[] { Conflict() });

        Assert.Equal(LanAlertSendState.Failed, result.State);
        Assert.Contains("500", result.Message, StringComparison.Ordinal);
    }

    /// <summary>外呼失败绝不能把扫描流程带下水 —— 本方法必须"返回失败结果"而不是抛。</summary>
    [Fact]
    public async Task Notifier_TransportException_ReturnsFailedWithoutThrowing()
    {
        var handler = new RecordingHandler { ThrowOnSend = new HttpRequestException("网络不可达") };
        var notifier = new LanScanAlertNotifier(handler);

        LanAlertSendResult result = await notifier.SendAsync(
            new LanScanAlertConfig(true, "https://example.com/hook"), new[] { Conflict() });

        Assert.Equal(LanAlertSendState.Failed, result.State);
        Assert.Contains("网络不可达", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Notifier_ComposeText_CapsEventList()
    {
        var many = Enumerable.Range(0, 25).Select(i => Conflict($"192.168.1.{i}")).ToList();

        string text = LanScanAlertNotifier.ComposeText(many);

        Assert.Contains("共 25 条", text, StringComparison.Ordinal);
        Assert.Contains("另有 15 条", text, StringComparison.Ordinal); // 25 - MaxEventsInMessage(10)
    }

    // ═══════════════ 事件 CSV ═══════════════

    [Fact]
    public void Csv_HeaderAndColumnOrder_AreStable()
    {
        string csv = LanEventCsv.Build(new[] { Conflict() });
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("时间,事件类型,IP,MAC,厂商,主机名,详情", lines[0]);
        Assert.Equal(LanEventCsv.Header, lines[0]);
        Assert.Equal(7, lines[1].Split(',').Length); // 无逗号详情时正好 7 列
    }

    [Fact]
    public void Csv_EnrichesVendorAndHostname_FromDeviceIndex()
    {
        var device = new LanDevice("192.168.1.7", "9A:00:00:00:00:04", "nas-home", "海康威视 Hikvision",
            DateTimeOffset.Now, DateTimeOffset.Now);
        var index = new Dictionary<string, LanDevice>(StringComparer.Ordinal) { ["192.168.1.7"] = device };

        string csv = LanEventCsv.Build(new[] { Conflict() }, index);
        string row = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Contains("192.168.1.7", row, StringComparison.Ordinal);
        Assert.Contains("海康威视 Hikvision", row, StringComparison.Ordinal);
        Assert.Contains("nas-home", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_WithoutIndex_LeavesVendorBlank_InsteadOfInventing()
    {
        string csv = LanEventCsv.Build(new[] { Conflict() });
        string row = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.DoesNotContain("海康", row, StringComparison.Ordinal);
        Assert.Contains("IP 冲突", row, StringComparison.Ordinal);
    }

    /// <summary>详情里一个逗号就能让整行错列——那是"导出看着像软件坏了"的经典来源。</summary>
    [Fact]
    public void Csv_EscapesCommaQuoteAndNewline()
    {
        string csv = LanEventCsv.Build(new[] { Conflict(detail: "含,逗号 \"引号\" 与\n换行") });

        Assert.Contains("\"含,逗号 \"\"引号\"\" 与\n换行\"", csv, StringComparison.Ordinal);
    }

    // ═══════════════ 配置存储（DPAPI） ═══════════════

    [Fact]
    public void AlertStore_MissingFile_ReturnsDisabledDefault()
    {
        string path = Path.Combine(NewTempDir(), "lan-scan-alert.json");

        LanScanAlertConfig config = new DpapiLanScanAlertStore(path).Load();

        Assert.False(config.Enabled);
        Assert.Null(config.WebhookUrl);
    }

    [Fact]
    public void AlertStore_RoundTrip_AndCiphertextIsNotPlaintext()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "lan-scan-alert.json");
        try
        {
            var store = new DpapiLanScanAlertStore(path);
            store.Save(new LanScanAlertConfig(true, "https://oapi.dingtalk.com/robot/send?access_token=SECRET-TOKEN-123", true, true, false));

            LanScanAlertConfig loaded = store.Load();
            Assert.True(loaded.Enabled);
            Assert.Equal("https://oapi.dingtalk.com/robot/send?access_token=SECRET-TOKEN-123", loaded.WebhookUrl);
            Assert.True(loaded.OnConflict);
            Assert.True(loaded.OnBindingChanged);
            Assert.False(loaded.OnNewDevice);

            // 🔴 红线：URL 里带机器人令牌，落盘不得出现明文
            Assert.DoesNotContain("SECRET-TOKEN-123", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AlertStore_CorruptedFile_FallsBackToDefaultWithoutThrowing()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "lan-scan-alert.json");
        try
        {
            File.WriteAllText(path, "这不是 base64，更不是密文");
            var store = new DpapiLanScanAlertStore(path);

            LanScanAlertConfig config = store.Load();
            Assert.False(config.Enabled); // 读不出来 = 全关，而不是"崩掉"

            store.Save(new LanScanAlertConfig(true, "https://example.com/hook")); // 坏文件不该让后续写入失败
            Assert.True(store.Load().Enabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stk-lan-alert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>记账型 handler：记调用次数与请求体，可设定状态码或直接抛异常。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastBody { get; private set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public Exception? ThrowOnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }

            return new HttpResponseMessage(Status);
        }
    }
}
