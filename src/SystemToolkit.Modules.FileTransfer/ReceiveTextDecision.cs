using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>
/// 文本接收的决定判据（FT-3 / B8b）。
/// <para>
/// 🔴 规则只有一条，但它决定了「回执是否等于交付」：「接收一条文本」这个动作**就是**
/// 「写进本机剪贴板」。因此只有**剪贴板确实写入成功**时才允许回「接受」（对端才会收到
/// <c>TextAck</c>）；写入失败必须回**拒绝 + <see cref="TransferReasonCodes.ClipboardWriteFailed"/>**
/// —— 否则对端收到的是"已送达"，而用户剪贴板里什么都没有（状态欺骗）。
/// </para>
/// <para>
/// 提成纯函数的原因：这是本通道唯一的诚实性关口，**必须有自动化用例钉住**；
/// 而真正写剪贴板那一步依赖 WPF `Clipboard`（只能在 UI 线程用），无法单测 ——
/// 所以把"是否可以接受"的判断从"写剪贴板成功与否"这一**入参**上解耦出来。
/// </para>
/// </summary>
public static class ReceiveTextDecision
{
    /// <summary>
    /// 依据「用户是否接受」与「剪贴板是否写入成功」给出回给对端的决定。
    /// </summary>
    /// <param name="accepted">用户是否点了接收（超时 / 关窗 / Esc 均为 false）。</param>
    /// <param name="clipboardWritten">文本是否真的写进了本机剪贴板。</param>
    /// <returns>
    /// 未接受 → <see cref="TransferDecision.Reject"/>（原因码由服务端落为 <c>USER_REJECT</c> / <c>CONFIRM_TIMEOUT</c>）；
    /// 接受且写入成功 → 接受；接受但写入失败 → 拒绝并附 <c>CLIPBOARD_WRITE_FAILED</c>。
    /// </returns>
    public static TransferDecision Resolve(bool accepted, bool clipboardWritten)
    {
        if (!accepted)
        {
            return TransferDecision.Reject;
        }

        // 文本通道不涉及同名文件，Conflict 取默认值即可（服务端对文本忽略该字段）
        return clipboardWritten
            ? TransferDecision.AcceptWith(TransferConflictPolicy.Rename)
            : TransferDecision.RejectWith(TransferReasonCodes.ClipboardWriteFailed);
    }
}
