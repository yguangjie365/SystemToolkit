namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>传输方向。</summary>
public enum TransferDirection
{
    /// <summary>发送（本机 → 对端）。</summary>
    Send,

    /// <summary>接收（对端 → 本机）。</summary>
    Receive,
}

/// <summary>
/// 传输内容的**种类**（2026-09-13 批次 B8a 新增）。
/// <para>
/// 与 <see cref="TransferDirection"/> 正交：方向回答"谁发给谁"，种类回答"发的是什么"。
/// 引入它的原因：文本既没有文件路径也没有落盘动作，若继续沿用文件那套字段，
/// 历史列表里就会出现「文件名为一段文本、大小为 412 B」的语义错乱。
/// </para>
/// <para>
/// 🔴 **成员顺序即磁盘契约**：本枚举写入历史 JSON 时按**数值**序列化
/// （与 <see cref="TransferStatus"/> / <see cref="TransferDirection"/> 同款，
/// <c>TransferHistoryService</c> 的 JsonOpts 未挂字符串枚举转换器）→
/// **不得重排、不得删除既有成员**，新增只能追加。
/// </para>
/// </summary>
public enum TransferKind
{
    /// <summary>文件。**默认值 0** —— 旧版本历史 JSON 没有这个字段，反序列化即落到此值，语义正确。</summary>
    File,

    /// <summary>文本 / 剪贴板。</summary>
    Text,
}

/// <summary>传输任务状态。</summary>
public enum TransferStatus
{
    /// <summary>排队中（已创建未发起）。</summary>
    Pending,

    /// <summary>握手协商中（含等待接收端确认）。</summary>
    Negotiating,

    /// <summary>传输中。</summary>
    Transferring,

    /// <summary>已暂停（2026-09-13 批次 P1 落地：双向 PAUSE/RESUME）。</summary>
    Paused,

    /// <summary>
    /// 已完成（SHA-256 校验通过并落定）。
    /// </summary>
    Completed,

    /// <summary>
    /// 已跳过（数据已收到，但按同名冲突策略**未写入**目标目录；2026-09-13 批次 P1）。
    /// <para>
    /// 🔴 与 <see cref="Completed"/> 严格区分：跳过时目标目录里**没有**新文件，
    /// 若报「已完成」就是状态欺骗。它也不是失败——传输本身没出错。
    /// </para>
    /// </summary>
    Skipped,

    /// <summary>失败（含校验不通过、对端拒绝、网络错误）。</summary>
    Failed,

    /// <summary>已取消（任一端主动取消）。</summary>
    Cancelled,
}

/// <summary>
/// 一次文件传输任务的完整描述。
/// </summary>
public sealed class TransferTask
{
    /// <summary>任务唯一 ID。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 内容种类（文件 / 文本；B8a）。
    /// <para>
    /// 文本任务**没有文件**：<see cref="FilePath"/> 恒为空、不需要分片与断点续传，
    /// <see cref="FileName"/> 存的是 <see cref="TransferText.PreviewLength"/> 字预览而非文件名。
    /// 取用方（任务列表、历史写入、日志）必须先看本字段，否则会把预览当成文件名展示。
    /// </para>
    /// </summary>
    public TransferKind Kind { get; init; }

    /// <summary>文件名（不含路径）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>文件完整路径（发送方为源路径；接收方在校验通过改名后更新为最终落定路径）。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>文件总大小（字节）。</summary>
    public long FileSize { get; init; }

    /// <summary>传输方向。</summary>
    public TransferDirection Direction { get; init; }

    /// <summary>对端设备 ID。</summary>
    public string PeerDeviceId { get; init; } = string.Empty;

    /// <summary>对端 IP:Port。</summary>
    public string PeerEndpoint { get; init; } = string.Empty;

    /// <summary>当前状态。</summary>
    public TransferStatus Status { get; set; } = TransferStatus.Pending;

    /// <summary>已传输字节数。</summary>
    public long TransferredBytes { get; set; }

    /// <summary>分片大小。</summary>
    public int ChunkSize { get; init; } = 2 * 1024 * 1024; // 2 MB

    /// <summary>文件哈希（SHA-256，发送方在 Complete 消息中携带，接收方比对）。</summary>
    public string? FileHash { get; set; }

    /// <summary>开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>结束时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>错误信息（失败时）。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 机器可读原因码（见 <see cref="TransferReasonCodes"/>；失败/拒绝/跳过时填入，成功为空）。
    /// UI 断言与分支应认它，不要匹配 <see cref="ErrorMessage"/> 的措辞。
    /// </summary>
    public string? ReasonCode { get; set; }

    /// <summary>本机用户发起暂停的时刻（仅**本机**暂停时记录；对端暂停不设，因为它决定何时恢复）。</summary>
    public DateTimeOffset? PausedAt { get; set; }

    /// <summary>是否因**对端**要求而挂起（用于界面区分"我暂停的"与"对面暂停的"）。</summary>
    public bool PausedByPeer { get; set; }

    /// <summary>传输进度百分比（0-100）。</summary>
    public double Progress => FileSize > 0 ? Math.Round(TransferredBytes * 100.0 / FileSize, 1) : 0;

    /// <summary>传输速度（字节/秒）。</summary>
    public long SpeedBytesPerSec { get; set; }

    /// <summary>格式化速度显示（如 "1.2 MB/s"；无速度时为 "—"，供 WPF 绑定直接使用）。</summary>
    public string SpeedText => FormatRate(SpeedBytesPerSec);

    /// <summary>预计剩余时间。</summary>
    public TimeSpan? EstimatedRemaining =>
        SpeedBytesPerSec > 0 && FileSize > TransferredBytes
            ? TimeSpan.FromSeconds((FileSize - TransferredBytes) / (double)SpeedBytesPerSec)
            : null;

    private static string FormatRate(long bytesPerSec) => bytesPerSec switch
    {
        <= 0 => "—",
        < 1024 => $"{bytesPerSec} B/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024.0:F1} KB/s",
        < 1024L * 1024 * 1024 => $"{bytesPerSec / 1048576.0:F1} MB/s",
        _ => $"{bytesPerSec / 1073741824.0:F2} GB/s",
    };
}
