using System.Text.Json.Serialization;

namespace SystemToolkit.Core.FileTransfer.Services.Protocol;

/// <summary>
/// 文件传输协议消息：通过 WatsonTcp 的 metadata 通道传递控制信息。
/// 二进制文件数据通过消息体传输。
/// </summary>
public sealed class TransferMessage
{
    /// <summary>消息类型。</summary>
    public TransferMessageType Type { get; init; }

    /// <summary>任务 ID。</summary>
    public string TaskId { get; init; } = string.Empty;

    /// <summary>文件名。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>文件总大小（字节）。</summary>
    public long FileSize { get; init; }

    /// <summary>文件 SHA-256 哈希（发送方在 Complete 消息中携带全文件哈希）。</summary>
    public string? FileHash { get; init; }

    /// <summary>分片大小。</summary>
    public int ChunkSize { get; init; }

    /// <summary>当前分片序号（0-based）。</summary>
    public int ChunkIndex { get; init; }

    /// <summary>总分片数。</summary>
    public int TotalChunks { get; init; }

    /// <summary>当前分片字节偏移（断点续传用）。</summary>
    public long Offset { get; init; }

    /// <summary>已接收字节数（接收方在握手时告知，用于断点续传）。</summary>
    public long ResumeFrom { get; init; }

    /// <summary>错误信息。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 文件最后修改时间（Unix 毫秒；0 = 未知）。发送方在握手时携带，
    /// 接收方在落定后还原——手机照片等原始时间属性跨设备保留（2026-09-06 协议扩展）。
    /// </summary>
    public long FileModifiedAt { get; init; }

    /// <summary>
    /// 一次性配对码（2026-09-06 批次二协议扩展）。接收端开启 RequirePairing 时握手必须携带
    /// 有效配对码；首个成功消费的发送方 IP 记入已配对列表，同批次后续文件免码。
    /// </summary>
    public string? PairCode { get; init; }
}

/// <summary>传输协议消息类型。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransferMessageType
{
    /// <summary>握手：发送方通知接收方即将传输文件。</summary>
    Handshake,

    /// <summary>握手确认：接收方同意接收，告知断点位置。</summary>
    HandshakeAck,

    /// <summary>文件数据分片。</summary>
    Chunk,

    /// <summary>分片确认：接收方确认已写入。</summary>
    ChunkAck,

    /// <summary>传输完成：发送方通知所有分片已发送。</summary>
    Complete,

    /// <summary>完成确认：接收方校验通过。</summary>
    CompleteAck,

    /// <summary>取消传输。</summary>
    Cancel,

    /// <summary>错误。</summary>
    Error,
}
