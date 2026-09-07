using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace SystemToolkit.UI.Common;

/// <summary>
/// 操作日志行（复用自旧工程 UI.Common，配色改用本项目令牌字典）。
/// 文本分类为纯函数（Classify），便于测试锁定行为。
/// </summary>
public sealed class LogLine
{
    // M-UI-1 落地（2026-09-05）：fallback 硬编码与新主色板同步
    private static readonly Brush ErrorBrush = ThemeBrush.Find("Brush_Danger", "#EF4444");
    private static readonly Brush SuccessBrush = ThemeBrush.Find("Brush_Success", "#10B981");
    private static readonly Brush InfoBrush = ThemeBrush.Find("Brush_TextMuted", "#6B7280");

    private static readonly Regex ZeroFailureCount = new("(?:校验)?失败\\s*0\\s*个?(?:文件?|条|项)?", RegexOptions.Compiled);

    public string Text { get; init; } = "";

    public Brush Color { get; init; } = Brushes.Gray;

    public static LogLine Create(string text)
        => new() { Text = text, Color = ColorFor(Classify(text)) };

    /// <summary>按日志文本判定行类别；"失败 0 个"这类计数为 0 的表述不算失败。</summary>
    public static string Classify(string text)
    {
        string stripped = ZeroFailureCount.Replace(text ?? "", "");
        bool isError = stripped.Contains("失败", StringComparison.Ordinal)
            || stripped.Contains("错误", StringComparison.Ordinal)
            || stripped.Contains("异常", StringComparison.Ordinal);
        bool isSuccess = stripped.Contains("成功", StringComparison.Ordinal)
            || stripped.Contains("完成", StringComparison.Ordinal)
            || stripped.Contains("校验通过", StringComparison.Ordinal);
        return isError ? "Error" : isSuccess ? "Success" : "Info";
    }

    private static Brush ColorFor(string category) => category switch
    {
        "Error" => ErrorBrush,
        "Success" => SuccessBrush,
        _ => InfoBrush,
    };

}
