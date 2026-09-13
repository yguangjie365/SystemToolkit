namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 传输失败/拒绝的**机器可读原因码**（协议 §4.2「拒绝必须带原因码」）。
/// <para>
/// 为什么要有它：<see cref="TransferTask.ErrorMessage"/> 是给人看的散文，措辞随时会调整，
/// 拿它做判据的测试与 UI 分支必然漂移；原因码是稳定契约，供 UI 分支、历史筛选、测试断言使用。
/// </para>
/// <para>
/// 落点：随 <c>TransferMessage.ReasonCode</c> 在对端之间传递，并写入 <see cref="TransferTask.ReasonCode"/>
/// 与 <see cref="TransferHistoryEntry.ReasonCode"/>（历史跨会话保留）。
/// </para>
/// </summary>
public static class TransferReasonCodes
{
    /// <summary>来源不在设备发现在线列表（接收端白名单拦截）。</summary>
    public const string PeerNotDiscovered = "PEER_NOT_DISCOVERED";

    /// <summary>配对码缺失/无效/已过期。</summary>
    public const string PairingInvalid = "PAIRING_INVALID";

    /// <summary>接收端并发接收已达上限。</summary>
    public const string ConcurrencyLimit = "CONCURRENCY_LIMIT";

    /// <summary>分片大小与接收端配置不一致。</summary>
    public const string ChunkSizeMismatch = "CHUNK_SIZE_MISMATCH";

    /// <summary>声明的文件大小非法（负数）。</summary>
    public const string InvalidFileSize = "INVALID_FILE_SIZE";

    /// <summary>接收端磁盘空间不足（接收前预检）。</summary>
    public const string InsufficientDisk = "INSUFFICIENT_DISK";

    /// <summary>同名冲突策略 = 跳过（含策略=询问但用户选了跳过）。</summary>
    public const string ConflictSkip = "CONFLICT_SKIP";

    /// <summary>策略=询问但接收确认门未开启（无从询问），已按最安全的「自动改名」降级。</summary>
    public const string ConflictAskUnavailable = "CONFLICT_ASK_UNAVAILABLE";

    /// <summary>接收方用户拒绝。</summary>
    public const string UserReject = "USER_REJECT";

    /// <summary>接收确认门超时未响应，按拒绝处理。</summary>
    public const string ConfirmTimeout = "CONFIRM_TIMEOUT";

    /// <summary>落定前 SHA-256 校验不通过。</summary>
    public const string HashMismatch = "HASH_MISMATCH";

    /// <summary>任一端主动取消。</summary>
    public const string UserCancel = "USER_CANCEL";

    /// <summary>对端断开连接。</summary>
    public const string PeerDisconnected = "PEER_DISCONNECTED";

    /// <summary>暂停超过上限自动取消（见 <c>TransferSettings.PauseTimeoutMinutes</c>）。</summary>
    public const string PauseTimeout = "PAUSE_TIMEOUT";

    /// <summary>原因码的中文说明（UI/日志直接可用；未知码原样返回，绝不吞）。</summary>
    public static string Describe(string? code) => code switch
    {
        PeerNotDiscovered => "来源设备不在已发现在线列表",
        PairingInvalid => "配对码无效或已过期",
        ConcurrencyLimit => "接收方并发传输已达上限",
        ChunkSizeMismatch => "分片大小与接收端不一致",
        InvalidFileSize => "文件大小非法",
        InsufficientDisk => "接收方磁盘空间不足",
        ConflictSkip => "同名文件已存在，按策略跳过",
        ConflictAskUnavailable => "策略为询问但无法询问，已按自动改名处理",
        UserReject => "接收方用户拒绝",
        ConfirmTimeout => "接收方未在时限内确认",
        HashMismatch => "SHA-256 校验不通过",
        UserCancel => "已取消",
        PeerDisconnected => "对端断开连接",
        PauseTimeout => "暂停超时，已自动取消",
        null or "" => string.Empty,
        _ => code,
    };
}
