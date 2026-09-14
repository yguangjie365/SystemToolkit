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
    /// 机器可读原因码（见 <see cref="Models.TransferReasonCodes"/>，2026-09-13 批次 P1）。
    /// <para>
    /// 与 <see cref="Error"/> 的分工：<see cref="Error"/> 给人看（措辞可改），本字段给代码看
    /// （UI 分支 / 历史筛选 / 测试断言）。**两者必须同发**——只给码会让用户看不懂，
    /// 只给文案会让下游只能做字符串匹配。
    /// </para>
    /// </summary>
    public string? ReasonCode { get; init; }

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

    /// <summary>
    /// 文本内容（**仅 <see cref="TransferMessageType.Text"/> 携带**，2026-09-13 批次 B8a）。
    /// <para>
    /// 文件类消息恒为 null。文本走 metadata 通道、**没有消息体** —— 这是它与文件的本质差别：
    /// 不需要分片、不需要断点续传、不需要落盘（直接进剪贴板）。
    /// </para>
    /// </summary>
    public string? Text { get; init; }
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

    /// <summary>
    /// 分片确认：接收方确认已写入。
    /// <para>
    /// 🔴 <b>设计预留，当前协议**不采用**逐分片确认</b>（2026-09-14 v8 审查 🟡-12 核实结论）：
    /// 本成员属原始协议的 8 个消息类型之一（见 <c>Docs/30-模块设计/05-文件互传模块详细设计.md</c>
    /// 的"Protocol 2 文件（TransferMessage 8 消息类型）"），但实现侧选了另一套可靠性模型 ——
    /// <b>断点位置由 <see cref="HandshakeAck"/> 在握手时给出</b>、
    /// <b>完整性由 <see cref="CompleteAck"/> 携带全文件 SHA-256 校验</b>，
    /// 故逐分片回执既无生产者也无消费者（全 <c>src/</c> 零引用，实测 2026-09-14）。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意保留而非删除</b>：它是已对外声明的协议面（<c>JsonStringEnumConverter</c> 按名序列化，
    /// 删掉会让将来对端发来的该类型反序列化失败）。保留的同时用注释消除"静默丢弃"的歧义，
    /// 并由 <c>ProtocolMessageTypeCoverageGuardTests</c> 钉住"无引用必须在预留清单里"。
    /// </para>
    /// <para>若将来真要做逐分片确认（例如弱网大文件），改这里是**起点不是终点**：
    /// 需同时补发送侧处理分支、接收侧回执点，以及该守卫的预留清单移除。</para>
    /// </summary>
    ChunkAck,

    /// <summary>传输完成：发送方通知所有分片已发送。</summary>
    Complete,

    /// <summary>完成确认：接收方校验通过。</summary>
    CompleteAck,

    /// <summary>取消传输。</summary>
    Cancel,

    /// <summary>
    /// 暂停传输（协议 §4.2，**双向**，2026-09-13 批次 P1 落地）。
    /// <para>
    /// 语义：「**我这边**暂停了，你不要再发数据」——发送方收到即挂起分片循环，接收方收到即停收。
    /// 被通知方的任务状态同步置「已暂停」，避免界面显示"传输中"而实际零字节流动（状态欺骗）。
    /// </para>
    /// </summary>
    Pause,

    /// <summary>恢复传输（协议 §4.2，双向）。收到即解除挂起，双方状态回到「传输中」。</summary>
    Resume,

    /// <summary>
    /// 一条文本（FT-3，B8a）。发送方 → 接收方，**无消息体**，内容在
    /// <see cref="TransferMessage.Text"/>。
    /// <para>
    /// 复用文件路径的确认门（接收端开 <c>RequireReceiveConfirmation</c> 时同样弹窗），
    /// 因此超时/拒绝的原因码与文件完全一致。
    /// </para>
    /// </summary>
    Text,

    /// <summary>文本已交付（对应文件的 CompleteAck）。接收方在**成功写入剪贴板之后**才回它。</summary>
    TextAck,

    /// <summary>错误。</summary>
    Error,
}
