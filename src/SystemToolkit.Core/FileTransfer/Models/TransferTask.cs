namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>传输方向。</summary>
public enum TransferDirection
{
    /// <summary>发送（本机 → 对端）。</summary>
    Send,

    /// <summary>接收（对端 → 本机）。</summary>
    Receive,
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

    /// <summary>已暂停（协议预留位，当前版本未实现恢复）。</summary>
    Paused,

    /// <summary>已完成（SHA-256 校验通过并落定）。</summary>
    Completed,

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
