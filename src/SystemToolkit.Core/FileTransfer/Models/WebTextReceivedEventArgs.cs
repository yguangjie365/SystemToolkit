namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 浏览器（手机端）经 <c>POST /api/text</c> 发来的一条文本（W2 / FT-3 网页侧）。
/// <para>
/// <b>为什么是事件而不是"落成 .txt 文件"</b>：文本通道的交付物是**剪贴板**（电脑端收到即可粘贴），
/// 不是共享目录里的一个文件。落成文件会让"我发了一段文字给电脑"变成"共享目录里多了个不知道叫什么的
/// .txt"，与用户在手机上按下发送时的意图不符。
/// </para>
/// <para>
/// 🔴 <b>本事件只负责"如实转达"，不负责写剪贴板</b>：是否弹确认窗、是否写剪贴板由订阅方（模块层）决定，
/// 与本仓「电脑↔电脑文本走确认门」的既有口径保持一致（交付＝覆盖用户剪贴板，故须经用户同意）。
/// </para>
/// <para>
/// 🔴 <b>订阅方抛出的异常会被服务端吞掉并留痕</b>：本事件在 HTTP 请求线程上触发，
/// 订阅方的失败绝不能把请求变成 500——发送方此刻已经拿到 200，再回 500 是状态自相矛盾。
/// </para>
/// </summary>
public sealed class WebTextReceivedEventArgs : EventArgs
{
    /// <summary>文本全文（未经截断；已通过 <see cref="TransferText.Validate"/> 校验）。</summary>
    public required string Text { get; init; }

    /// <summary>来源 IP（展示用，如「192.168.1.8」；取不到时为 <c>未知地址</c>）。</summary>
    public required string FromIp { get; init; }

    /// <summary>服务端收下的时刻（UTC）。</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>字符数（UTF-16 码元数）。</summary>
    public int CharCount { get; init; }

    /// <summary>UTF-8 字节数（≤ <see cref="TransferText.MaxBytes"/>）。</summary>
    public int ByteCount { get; init; }
}
