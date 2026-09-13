using System.Text;

namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 文本 / 剪贴板通道的**本地判据**（FT-3 方案 §3.3）。
/// <para>
/// 为什么把判据放在 Core 的纯函数里，而不是散在发送按钮与接收分支里：
/// 本仓已经吃过「同一判据两处各写」的亏（改了一处忘了另一处）。长度校验与预览截断
/// 在发送端、接收端、历史写入三处都要用，必须只有一份实现，且要能被离线断言。
/// </para>
/// <para>
/// 🔴 长度上限按 **UTF-8 字节**而非字符：中文一个字符占 3 字节，按字符限长会让
/// 中文文本轻易超出协议层实际能承受的体积（方案 §3.3 特意点明）。
/// </para>
/// </summary>
public static class TransferText
{
    /// <summary>单条文本上限（UTF-8 字节）。</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>历史预览长度（**字符**数，非字节）。</summary>
    public const int PreviewLength = 120;

    /// <summary>文本的 UTF-8 字节数（null / 空 = 0）。</summary>
    public static int GetByteCount(string? text)
        => string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>文本的字符数（UTF-16 码元数，与 <see cref="PreviewLength"/> 同一口径）。</summary>
    public static int GetCharCount(string? text) => text?.Length ?? 0;

    /// <summary>
    /// 校验一条待发送/待接收的文本。
    /// <para>
    /// 越界行为是「拒绝」，**绝不静默截断后当成功** —— 截断会把一条长链接变成失效链接，
    /// 而用户看到的却是「发送成功」（状态欺骗）。
    /// </para>
    /// </summary>
    /// <param name="text">待校验文本（可为 null）。</param>
    /// <returns>校验结果（含字符数 / 字节数与给人看的原因）。</returns>
    public static TextValidation Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextValidation(TextValidationKind.Empty, 0, 0);
        }

        int bytes = Encoding.UTF8.GetByteCount(text);
        return bytes > MaxBytes
            ? new TextValidation(TextValidationKind.TooLong, text.Length, bytes)
            : new TextValidation(TextValidationKind.Valid, text.Length, bytes);
    }

    /// <summary>
    /// 生成历史预览：按**字符**截断至 <see cref="PreviewLength"/>，且**不切断代理对**。
    /// <para>
    /// 若第 120 个码元恰是高位代理（high surrogate），说明 emoji 等增补平面字符正好跨在截断点上；
    /// 此时回退一位。只按码元硬切会让历史里出现半个代理对，落盘/显示为乱码。
    /// </para>
    /// </summary>
    /// <param name="text">全文（可为 null）。</param>
    /// <returns>不超过 <see cref="PreviewLength"/> 个码元的预览串。</returns>
    public static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= PreviewLength)
        {
            return text ?? string.Empty;
        }

        int end = PreviewLength;
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }
}

/// <summary>文本校验的结论。</summary>
public enum TextValidationKind
{
    /// <summary>通过。</summary>
    Valid,

    /// <summary>null / 空 / 纯空白——不发起传输（发一条空白没有任何意义）。</summary>
    Empty,

    /// <summary>超出 <see cref="TransferText.MaxBytes"/>。</summary>
    TooLong,
}

/// <summary>
/// 文本校验结果（<see cref="TransferText.Validate"/> 的返回）。
/// <para>
/// 同时带字符数与字节数：界面要显示「137 字（UTF-8 412 B）」这类信息（方案 §5.2），
/// 让用户直观理解"为什么中文比英文更早触顶"。
/// </para>
/// </summary>
/// <param name="Kind">校验结论。</param>
/// <param name="CharCount">字符数（校验为 <see cref="TextValidationKind.Empty"/> 时为 0）。</param>
/// <param name="ByteCount">UTF-8 字节数（校验为 <see cref="TextValidationKind.Empty"/> 时为 0）。</param>
public readonly record struct TextValidation(TextValidationKind Kind, int CharCount, int ByteCount)
{
    /// <summary>是否通过校验。</summary>
    public bool IsValid => Kind == TextValidationKind.Valid;

    /// <summary>不通过时给人看的原因（通过时为空串）。</summary>
    public string ErrorText => Kind switch
    {
        TextValidationKind.Empty => "文本为空，没有可发送的内容。",
        TextValidationKind.TooLong => $"文本过大（{ByteCount / 1024} KB），单条上限 {TransferText.MaxBytes / 1024} KB。",
        _ => string.Empty,
    };

    /// <summary>供界面显示的大小文本，例如 <c>137 字（UTF-8 412 B）</c>。</summary>
    public string SizeText
        => $"{CharCount} 字（UTF-8 {(ByteCount >= 1024 ? $"{ByteCount / 1024.0:F1} KB" : $"{ByteCount} B")}）";
}
