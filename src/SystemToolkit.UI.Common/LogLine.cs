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

    // 🟠 审查 2026-09-10（🟠-13）：**不得**用 static readonly 缓存 ThemeBrush.Find 的结果——
    // Find 返回的是当前主题字典里的 Brush 实例；ThemeManager 替换 MergedDictionaries[0] 之后
    // 旧实例已不在字典中，而 static 字段仍持有它（主题切换后日志行继续用旧主题配色，
    // 且 MainWindow 重建视图也不会让 static 构造函数重跑）。改为每次调用时查找
    // （TryFindResource 是字典查找，开销可忽略），与 ADR-005「全站跟随主题」一致。
    // fallback 硬编码值与主色板保持同步（M-UI-1）。
    private static Brush ColorFor(string category) => category switch
    {
        "Error" => ThemeBrush.Find("Brush_Danger", "#EF4444"),
        "Success" => ThemeBrush.Find("Brush_Success", "#10B981"),
        _ => ThemeBrush.Find("Brush_TextMuted", "#6B7280"),
    };

}
