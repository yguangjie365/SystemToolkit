namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 一条传输历史记录。
/// <para>
/// 与 <see cref="TransferTask"/> 的区别：任务对象是内存中的实时状态（进度/速度会一直变），
/// 历史条目是任务结束时的**快照**，只增不改，用于跨会话回看「传过什么、成功没有」。
/// </para>
/// </summary>
public sealed class TransferHistoryEntry
{
    /// <summary>对应的任务 ID（可用于与日志对齐）。</summary>
    public string TaskId { get; init; } = string.Empty;

    /// <summary>文件名（不含路径）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long FileSize { get; init; }

    /// <summary>发送还是接收。</summary>
    public TransferDirection Direction { get; init; }

    /// <summary>对端地址（IP:端口）；对端是设备时还可结合 PeerDeviceId 识别。</summary>
    public string PeerEndpoint { get; init; } = string.Empty;

    /// <summary>对端设备 ID（可能为空，取决于传输通道）。</summary>
    public string PeerDeviceId { get; init; } = string.Empty;

    /// <summary>最终状态（成功 / 失败 / 已取消）。</summary>
    public TransferStatus Status { get; init; }

    /// <summary>开始时间。</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>结束时间。</summary>
    public DateTimeOffset FinishedAt { get; init; }

    /// <summary>实际传输字节数（失败时小于文件大小，用于判断中断位置）。</summary>
    public long TransferredBytes { get; init; }

    /// <summary>失败原因（成功时为空）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 机器可读原因码（见 <see cref="TransferReasonCodes"/>；成功时为空）。
    /// 历史是跨会话的，措辞会随版本变，所以判据必须落在码上。
    /// </summary>
    public string? ReasonCode { get; init; }

    /// <summary>方向的中文本地化文本（与 TransferTask 的速度/进度文本同理，供界面直接绑定）。</summary>
    public string DirectionText => Direction == TransferDirection.Send ? "发送" : "接收";

    /// <summary>状态的中文本地化文本。</summary>
    public string StatusText => Status switch
    {
        TransferStatus.Completed => "完成",
        TransferStatus.Failed => "失败",
        TransferStatus.Cancelled => "已取消",
        TransferStatus.Skipped => "已跳过",
        _ => Status.ToString(),
    };

    /// <summary>
    /// 原因的中文说明（失败/跳过时非空；成功时为空字符串）。
    /// UI 直接绑这一列即可，不必自己拼 <see cref="ErrorMessage"/> 与 <see cref="ReasonCode"/>。
    /// </summary>
    public string ReasonText => string.IsNullOrEmpty(ReasonCode)
        ? ErrorMessage ?? string.Empty
        : $"{TransferReasonCodes.Describe(ReasonCode)}（{ReasonCode}）";

    /// <summary>格式化的文件大小（界面直接绑定，免得再写一个转换器）。</summary>
    public string SizeText => SystemToolkit.Core.Utilities.FormatUtil.FormatSize(FileSize);
}
