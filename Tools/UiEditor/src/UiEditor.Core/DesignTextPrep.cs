using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UiEditor.Core;

/// <summary>
/// 设计态文本预处理：把真实模块 View 的 <c>.xaml</c> 变成可被 <c>XamlReader.Parse</c> 独立解析的文本——
/// 剥离 <c>x:Class</c>（无 code-behind 分部类）与事件句柄属性（无宿主可挂）。
/// <para>
/// 事件名取自全仓模块 View 实际用到的有界集合（2026-09-11 grep 统计）。遇到未收录的事件属性，
/// 解析仍会抛 → 上层降级为"只读预览"并提示补列表，不崩。
/// </para>
/// </summary>
public static class DesignTextPrep
{
    private static readonly HashSet<string> EventAttributes = new(System.StringComparer.Ordinal)
    {
        "Click", "Checked", "Unchecked", "SelectionChanged", "MouseDoubleClick", "MouseLeftButtonDown",
        "MouseRightButtonDown", "PreviewMouseLeftButtonDown", "SizeChanged", "Drop", "DragOver", "DragEnter",
        "DragLeave", "Loaded", "Unloaded", "KeyDown", "KeyUp", "GotFocus", "LostFocus", "TextChanged",
        "MouseWheel", "MouseEnter", "MouseLeave", "ContextMenuOpening", "Expanded", "Collapsed",
        "ScrollChanged", "PasswordChanged", "ValueChanged", "Closing", "Opened", "Activated", "Deactivated",
        "RequestNavigate", "Completed", "LayoutUpdated",
    };

    /// <summary>返回剥离后的可解析文本 + 被剥掉的事件数（供诊断）。</summary>
    public static string Prep(string xamlText, out int strippedEventCount)
    {
        string text = Regex.Replace(xamlText, @"\s+x:Class=""[^""]*""", string.Empty);

        int count = 0;
        foreach (string ev in EventAttributes)
        {
            string pattern = @"\s+" + Regex.Escape(ev) + @"=""[^""]*""";
            text = Regex.Replace(text, pattern, m =>
            {
                count++;
                return string.Empty;
            });
        }

        strippedEventCount = count;
        return text;
    }
}
