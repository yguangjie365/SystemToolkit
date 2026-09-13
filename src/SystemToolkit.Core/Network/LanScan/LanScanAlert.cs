namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// 局域网告警推送配置（落地计划 B3-③，源报告 P1-2「webhook 告警出口」）。
/// <para>
/// 🔴 默认**全关**：这是"往局域网外发数据"的能力，必须由用户显式开启并填 URL 才可能发生。
/// </para>
/// </summary>
/// <param name="Enabled">总开关（默认 false）。</param>
/// <param name="WebhookUrl">机器人 webhook 地址（钉钉 / 企业微信），存盘时经 DPAPI 加密。</param>
/// <param name="OnConflict">IP 冲突是否推送（默认 true —— 这是立项场景本身）。</param>
/// <param name="OnBindingChanged">IP↔MAC 绑定变更是否推送（默认 false）。</param>
/// <param name="OnNewDevice">新设备发现是否推送（默认 false）。</param>
public sealed record LanScanAlertConfig(
    bool Enabled = false,
    string? WebhookUrl = null,
    bool OnConflict = true,
    bool OnBindingChanged = false,
    bool OnNewDevice = false)
{
    /// <summary>出厂默认（全关）。</summary>
    public static LanScanAlertConfig Default { get; } = new();

    /// <summary>
    /// 是否**具备**外呼条件：已启用 且 URL 非空白。
    /// <para>
    /// 这是外呼路径的**唯一闸门**——未通过时 <see cref="LanScanAlertNotifier"/> 直接返回
    /// <see cref="LanAlertSendState.Skipped"/>，**不构造请求、不碰网络**。有单测钉这一点。
    /// </para>
    /// </summary>
    public bool CanSend => Enabled && !string.IsNullOrWhiteSpace(WebhookUrl);

    /// <summary>
    /// 校验 URL 是否为可用的 http/https 绝对地址。
    /// 用于区分"没配"与"配错了"——两者都不外呼，但要给用户不同的说法（后者是错误）。
    /// </summary>
    public bool HasValidUrl =>
        Uri.TryCreate(WebhookUrl?.Trim(), UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>启动时读不到配置就用它（全关），不抛。</summary>
    public static LanScanAlertConfig Normalize(LanScanAlertConfig? loaded) => loaded ?? Default;
}

/// <summary>
/// 告警配置的持久化契约。🟠 放在 Core：实现（DPAPI 加密落盘）在 Infrastructure，
/// 换实现（如将来的集中配置）不影响本域。
/// </summary>
public interface ILanScanAlertStore
{
    /// <summary>落盘路径（诊断/测试断言用）。</summary>
    string FilePath { get; }

    /// <summary>读取配置。文件不存在 / 解密失败 / JSON 损坏一律返回 <see cref="LanScanAlertConfig.Default"/> 且**不抛**。</summary>
    LanScanAlertConfig Load();

    /// <summary>写入配置（实现方负责加密与原子写）。</summary>
    void Save(LanScanAlertConfig config);
}

/// <summary>一次外呼的结局。</summary>
public enum LanAlertSendState
{
    /// <summary>未外呼（未启用 / 未配 URL / 本轮无匹配事件 / URL 非法）。这是**正常**结局，不是错误。</summary>
    Skipped = 0,

    /// <summary>已外呼且 HTTP 2xx。</summary>
    Success = 1,

    /// <summary>已外呼但失败（超时 / 网络错 / 非 2xx）。</summary>
    Failed = 2,
}

/// <summary>
/// 外呼结果。<see cref="Message"/> 一律可直接进操作日志——**不允许**出现"什么都没发生也没说为什么"。
/// </summary>
/// <param name="State">结局。</param>
/// <param name="Message">人读说明（含跳过原因 / 失败原因 / 事件条数）。</param>
public sealed record LanAlertSendResult(LanAlertSendState State, string Message)
{
    /// <summary>已外呼且成功。</summary>
    public bool Succeeded => State == LanAlertSendState.Success;

    /// <summary>本次是否真的发起了网络请求（诊断用；Skipped 必须为 false）。</summary>
    public bool Attempted => State != LanAlertSendState.Skipped;
}
