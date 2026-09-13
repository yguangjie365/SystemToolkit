using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Modules.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// FT-3（B8b）文本接收的**诚实性判据**：只有剪贴板真的写入成功才允许回「接受」。
/// <para>
/// 为什么值得单独钉：这条判据决定"回执是否等于交付"。「接收文本」这个动作就是「写剪贴板」，
/// 若写入失败却回了接受，对端会收到 <c>TextAck</c>（以为已送达），而用户剪贴板里什么都没有。
/// 这与本仓既有红线同型：**「跳过」不得报「完成」**、**没有证据不得写「通过」**。
/// </para>
/// </summary>
public class ReceiveTextDecisionTests
{
    [Fact]
    public void Resolve_UserDeclined_IsPlainRejectWithoutReasonCode()
    {
        TransferDecision decision = ReceiveTextDecision.Resolve(accepted: false, clipboardWritten: false);

        Assert.False(decision.Accept);
        // 原因码留空 → 服务端按既有语义落为 USER_REJECT / CONFIRM_TIMEOUT（本层不越权替它指定）
        Assert.Null(decision.ReasonCode);
    }

    [Fact]
    public void Resolve_UserDeclined_StaysRejectEvenIfClipboardWasWritten()
    {
        // 防御性：用户拒绝时写入结果无意义，不得因此变成"接受"
        TransferDecision decision = ReceiveTextDecision.Resolve(accepted: false, clipboardWritten: true);

        Assert.False(decision.Accept);
        Assert.Null(decision.ReasonCode);
    }

    [Fact]
    public void Resolve_AcceptedAndClipboardWritten_IsAccepted()
    {
        TransferDecision decision = ReceiveTextDecision.Resolve(accepted: true, clipboardWritten: true);

        Assert.True(decision.Accept);
        Assert.Null(decision.ReasonCode);
    }

    [Fact]
    public void Resolve_AcceptedButClipboardWriteFailed_RejectsWithClipboardReasonCode()
    {
        TransferDecision decision = ReceiveTextDecision.Resolve(accepted: true, clipboardWritten: false);

        // 🔴 本批核心：用户点了接收，但没交付成功 → **绝不能回接受**
        Assert.False(decision.Accept);
        Assert.Equal(TransferReasonCodes.ClipboardWriteFailed, decision.ReasonCode);
    }

    [Fact]
    public void Resolve_ClipboardFailure_IsNotCollapsedIntoUserReject()
    {
        // "对面要了但没接住"与"对面不要"必须给出**不同**的码：
        // 前者处置是让对端关掉占用剪贴板的程序再重发，后者是别再发了。
        TransferDecision clipboardFailure = ReceiveTextDecision.Resolve(accepted: true, clipboardWritten: false);
        TransferDecision userReject = ReceiveTextDecision.Resolve(accepted: false, clipboardWritten: false);

        Assert.NotEqual(userReject.ReasonCode, clipboardFailure.ReasonCode);
        Assert.NotEqual(TransferReasonCodes.UserReject, clipboardFailure.ReasonCode);
    }
}
