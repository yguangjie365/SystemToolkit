using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 文件传输服务：基于 WatsonTcp 实现 TCP 可靠分片传输，支持断点续传。
/// </summary>
public interface IFileTransferService : IAsyncDisposable
{
    /// <summary>当前活跃的传输任务。</summary>
    IReadOnlyList<TransferTask> ActiveTasks { get; }

    /// <summary>传输任务状态变化时触发。</summary>
    event EventHandler<TransferTask>? TaskUpdated;

    /// <summary>传输任务完成时触发。</summary>
    event EventHandler<TransferTask>? TaskCompleted;

    /// <summary>
    /// 收到需要本机确认的传输请求时触发（仅当 <see cref="TransferSettings.RequireReceiveConfirmation"/>
    /// 开启）。UI 必须响应——调用
    /// <see cref="RespondTransferAsync(string, bool)"/> 或
    /// <see cref="RespondTransferAsync(string, TransferDecision)"/>（带同名处理选择时用后者）；
    /// 超时未响应按拒绝处理。
    /// </summary>
    event EventHandler<TransferRequestEventArgs>? TransferRequested;

    /// <summary>
    /// 回复接收确认。<paramref name="accept"/> = true 放行传输；false 拒绝并通知对端。
    /// 任务不存在或已超时/已处理时为安全空操作（记日志）。
    /// </summary>
    Task RespondTransferAsync(string taskId, bool accept);

    /// <summary>
    /// 回复接收确认（**带冲突处理选择**，2026-09-13 批次 P1）。
    /// <para>
    /// 当策略为「询问」时，弹窗必须让用户逐次选择处理方式，选择结果经此回复；
    /// <see cref="TransferDecision.Conflict"/> 对**本次传输**生效，不改变全局策略设置。
    /// </para>
    /// </summary>
    Task RespondTransferAsync(string taskId, TransferDecision decision);

    /// <summary>
    /// 运行时切换同名冲突策略（协议 §4.4；与 <see cref="SetRequireKnownPeer"/> 同款热切换）。
    /// 仅影响之后到达的传输，进行中的落定按各自握手时解析的结果执行。
    /// </summary>
    void SetConflictPolicy(TransferConflictPolicy policy);

    /// <summary>
    /// 暂停任务（协议 §4.2，**双向**）。
    /// <para>
    /// 发送任务 → 挂起分片循环并通知对端；接收任务 → 通知对端停止发送并挂起本地状态。
    /// 返回是否受理（任务不存在/非活跃/已终态一律 false 并记日志，**不静默**）。
    /// </para>
    /// <para>
    /// 本机暂停超过 <see cref="TransferSettings.PauseTimeoutMinutes"/> 会自动取消（自动取消时
    /// 原因码为 <see cref="TransferReasonCodes.PauseTimeout"/>）。
    /// </para>
    /// </summary>
    Task<bool> PauseTaskAsync(string taskId);

    /// <summary>恢复被暂停的任务（本机暂停与对端暂停都能恢复；返回是否受理）。</summary>
    Task<bool> ResumeTaskAsync(string taskId);

    /// <summary>
    /// 启动传输服务端（监听 TCP 连接）。
    /// </summary>
    Task StartAsync(TransferSettings settings, CancellationToken ct = default);

    /// <summary>停止传输服务。</summary>
    Task StopAsync();

    /// <summary>
    /// 发送文件到指定设备。
    /// </summary>
    /// <param name="filePath">本地文件路径。</param>
    /// <param name="peerIp">对端 IP。</param>
    /// <param name="peerPort">对端 TCP 端口。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>传输任务对象。</returns>
    Task<TransferTask> SendFileAsync(string filePath, string peerIp, int peerPort, CancellationToken ct = default);

    /// <summary>
    /// 发送文件到指定设备（带一次性配对码，2026-09-06 批次二）。接收端开启 RequirePairing 时，
    /// 首个文件须携带从接收端获取的配对码；同批次后续文件免码（接收端已记忆发送方 IP）。
    /// </summary>
    Task<TransferTask> SendFileAsync(string filePath, string peerIp, int peerPort, string? pairCode, CancellationToken ct = default);

    /// <summary>
    /// 取消传输任务。
    /// </summary>
    Task CancelAsync(string taskId);

    /// <summary>
    /// 设置接收文件保存目录。
    /// </summary>
    void SetReceiveDirectory(string directory);

    /// <summary>
    /// 运行时切换「只接受已发现设备」的来源白名单。
    /// <para>
    /// 用途：UDP 广播在部分网络（企业 VLAN / AP 隔离）不通时，对端无法进入设备列表，
    /// 严格的白名单会让手动指定 IP 的直连被拒。关闭它即可接收来自任意 IP 的传输。
    /// </para>
    /// </summary>
    void SetRequireKnownPeer(bool require);
}

/// <summary>
/// 接收确认请求事件参数（接收端 UI 收到后必须调用 <see cref="IFileTransferService.RespondTransferAsync(string, bool)"/>）。
/// <para>
/// 位置参数保持最小集（既有调用方兼容）；**确认门弹窗需要的信息**以 init 属性补充——
/// 用户在 30 秒内要判断"接不接"，只给 IP 与文件名是不够的（协议 §4.2 TRANSFER_REQUEST 载荷）。
/// </para>
/// </summary>
public sealed record TransferRequestEventArgs(
    string TaskId, string FileName, long FileSize, string PeerEndpoint)
{
    /// <summary>接收目录（绝对路径）。</summary>
    public string ReceiveDirectory { get; init; } = string.Empty;

    /// <summary>接收目录所在卷的剩余空间（字节；null = 无法判定，例如 UNC 或卷未就绪）。</summary>
    public long? AvailableFreeBytes { get; init; }

    /// <summary>接收前磁盘预检结果（已含 <c>DiskSpaceUtil</c> 的安全余量判定）。</summary>
    public DiskSpaceCheck DiskSpace { get; init; } = DiskSpaceCheck.Unknown;

    /// <summary>目标目录是否已存在同名文件。</summary>
    public bool TargetExists { get; init; }

    /// <summary>按当前策略这一份**将会发生什么**（如实告知，不在落定时才反悔）。</summary>
    public ConflictResolution ConflictAction { get; init; } = ConflictResolution.Fresh;

    /// <summary>当前生效的冲突策略；为 <see cref="TransferConflictPolicy.Ask"/> 时弹窗需让用户选择。</summary>
    public TransferConflictPolicy ConflictPolicy { get; init; } = TransferConflictPolicy.Rename;

    /// <summary>发送方设备名（取自设备发现在线列表；查不到或对端不在列表时为空）。</summary>
    public string PeerDeviceName { get; init; } = string.Empty;

    /// <summary>
    /// 内容种类（B8a）。文件请求为 <see cref="TransferKind.File"/>（默认值，既有行为不变）；
    /// 文本请求为 <see cref="TransferKind.Text"/>，此时 <see cref="FileName"/> / <see cref="FileSize"/>
    /// 与磁盘预检 / 同名冲突诸字段**均无意义**，弹窗应按 <see cref="Kind"/> 整块隐藏它们。
    /// </summary>
    public TransferKind Kind { get; init; }

    /// <summary>
    /// 文本全文（仅 <see cref="Kind"/> = <see cref="TransferKind.Text"/> 时非空）。
    /// <para>
    /// 🔴 **不受历史预览长度限制**：用户要判断"接不接"必须看到全貌（方案 §5.2），
    /// 只给前 120 字等于让用户在信息不全的情况下点头。
    /// </para>
    /// </summary>
    public string? Text { get; init; }

    /// <summary>文本的字符数与 UTF-8 字节数（仅文本请求有意义；弹窗显示「137 字（UTF-8 412 B）」）。</summary>
    public int TextLength { get; init; }
}

/// <summary>
/// 接收确认的回复内容（2026-09-13 批次 P1）。
/// <para>
/// 为什么不是两个 bool：策略=「询问」时用户的选择有三个（改名/覆盖/跳过），而"接受/拒绝"只有两个；
/// 用 bool 表达就得让 UI 侧去改全局策略——那是把"本次选择"写成"永久设置"，属越权。
/// </para>
/// </summary>
/// <param name="Accept">是否接收。</param>
/// <param name="Conflict">本次的同名处理方式（仅在 <paramref name="Accept"/> 为 true 时有意义）。</param>
public sealed record TransferDecision(bool Accept, TransferConflictPolicy Conflict = TransferConflictPolicy.Rename)
{
    /// <summary>
    /// 拒绝时随 <c>Error</c> 回传给对端的原因码（B8a）。
    /// <para>
    /// 默认 null → 沿用 <see cref="TransferReasonCodes.UserReject"/>（**既有行为一字不变**）。
    /// 文本通道需要它是因为"拒绝"有两种截然不同的成因：用户不要（<c>USER_REJECT</c>），
    /// 与"接受了但剪贴板写不进去"（<c>CLIPBOARD_WRITE_FAILED</c>）—— 后者若也报"被拒绝"，
    /// 发送方会得出错误的处置结论（以为对面不收，实际是该重试）。
    /// </para>
    /// </summary>
    public string? ReasonCode { get; init; }

    /// <summary>拒绝接收。</summary>
    public static TransferDecision Reject { get; } = new(false);

    /// <summary>接受并按指定方式处理同名。</summary>
    public static TransferDecision AcceptWith(TransferConflictPolicy conflict) => new(true, conflict);

    /// <summary>拒绝并附带原因码（B8a；对端会原样收到这个码）。</summary>
    /// <param name="reasonCode">见 <see cref="TransferReasonCodes"/>；null / 空时等价于 <see cref="Reject"/>。</param>
    /// <returns>带原因码的拒绝决定。</returns>
    public static TransferDecision RejectWith(string reasonCode) => new(false) { ReasonCode = reasonCode };
}
