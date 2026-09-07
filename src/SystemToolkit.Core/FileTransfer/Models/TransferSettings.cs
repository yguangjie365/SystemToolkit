namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 文件互传模块的运行时配置。
/// </summary>
public sealed class TransferSettings
{
    /// <summary>UDP 设备发现广播端口。</summary>
    public int DiscoveryPort { get; set; } = 18888;

    /// <summary>TCP 文件传输监听端口。</summary>
    public int TransferPort { get; set; } = 18889;

    /// <summary>Web 服务 HTTP 端口（手机浏览器访问）。</summary>
    public int WebPort { get; set; } = 18890;

    /// <summary>心跳广播间隔。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>设备离线判定阈值。</summary>
    public TimeSpan OfflineTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>分片大小（字节）。</summary>
    public int ChunkSize { get; set; } = 2 * 1024 * 1024; // 2 MB

    /// <summary>最大并发传输数。</summary>
    public int MaxConcurrentTransfers { get; set; } = 3;

    /// <summary>
    /// 最大并发接收连接数（防止任意主机反复握手填充磁盘的 DoS 面上限）。
    /// </summary>
    public int MaxConcurrentReceives { get; set; } = 8;

    /// <summary>
    /// 是否只接受「已通过设备发现在线」的对端连接（来源白名单）。
    /// 默认开启；单机测试两个独立实例时需显式关闭。
    /// </summary>
    public bool RequireKnownPeer { get; set; } = true;

    /// <summary>
    /// 是否启用「接收端确认门」：握手到达后挂起等待本机用户确认（UI 弹窗），
    /// 拒绝或超时则回错误拒绝传输（2026-09-06 用户裁定批次一安全模型 = 白名单 + 接收确认）。
    /// 默认关闭（兼容旧测试语义）；应用 UI 默认打开。
    /// </summary>
    public bool RequireReceiveConfirmation { get; set; }

    /// <summary>接收端确认等待时长（秒）；超时未响应按拒绝处理。默认 30 秒。</summary>
    public int ReceiveConfirmTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 是否要求桌面通道握手携带有效一次性配对码（2026-09-06 批次二）。
    /// 首个成功消费配对码的发送方 IP 在服务运行期间记入已配对列表，后续传输免码。
    /// 🔴 REVIEW-3 G-7：默认改为<b>开启</b>——旧默认 false 时同网段任意主机广播一个合法
    /// JSON 即入发现白名单。配对流程（/api/pair + 发送端配对码握手）独立于本开关，开启不会
    /// 死锁首次配对；已有设置文件中显式 false 的用户不受影响（仅缺省字段取新默认）。
    /// </summary>
    public bool RequirePairing { get; set; } = true;

    /// <summary>共享目录路径（空则使用当前用户的「下载」文件夹）。</summary>
    public string? ShareDirectory { get; set; }

    /// <summary>接收文件保存目录（空则使用「下载\Received」；2026-09-02 起不再是桌面）。</summary>
    public string? ReceiveDirectory { get; set; }

    /// <summary>设备显示名称（空则使用机器名）。</summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Web 服务是否启用 HTTPS（默认关闭）。
    /// <para>
    /// 用**自签证书**，因此手机首次访问必须先手动信任一次，否则浏览器会拦截。
    /// 默认关闭的原因正是这点：家庭网络里这点体验代价通常大于收益，
    /// 但在访客 Wi-Fi / 办公网等不可信网络里，明文 HTTP 的文件与令牌都能被同网段嗅探。
    /// </para>
    /// </summary>
    public bool UseHttps { get; set; }

    /// <summary>HTTPS 监听端口（仅在 <see cref="UseHttps"/> 打开时使用）。</summary>
    public int HttpsPort { get; set; } = 18891;
}
