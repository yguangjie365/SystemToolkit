using System;
using System.Windows;
using System.Windows.Controls;

namespace SystemToolkit.UI.Common.Controls;

/// <summary>
/// GridView 末列自适应填充（2026-09-10）。
/// <para>
/// 🔴 解决的问题：WPF <see cref="GridView"/> 的列宽固定（像素）时，若各列总宽小于视口宽，
/// GridView 会自动补一个**空白表头列**填充剩余空间，且无法关闭——现象是
/// 「表头比内容多一列」（实测于概览页已安装软件表）。让末列吃掉剩余宽度即可消除，
/// 同时窗口拉伸时末列随动，观感也更自然。
/// </para>
/// <para>
/// 用法（ListView 上加一处即可）：
/// <c>&lt;ListView controls:GridViewColumnSizing.AutoFillLastColumn="True" ... &gt;</c>
/// </para>
/// </summary>
public static class GridViewColumnSizing
{
    /// <summary>末列最小宽度（防窗口过窄时被压没）。</summary>
    private const double MinLastColumnWidth = 100;

    /// <summary>是否启用「末列自适应填充」。</summary>
    public static readonly DependencyProperty AutoFillLastColumnProperty =
        DependencyProperty.RegisterAttached(
            "AutoFillLastColumn",
            typeof(bool),
            typeof(GridViewColumnSizing),
            new PropertyMetadata(false, OnAutoFillLastColumnChanged));

    /// <summary>读取附加属性值。</summary>
    public static bool GetAutoFillLastColumn(DependencyObject element)
        => (bool)element.GetValue(AutoFillLastColumnProperty);

    /// <summary>设置附加属性值。</summary>
    public static void SetAutoFillLastColumn(DependencyObject element, bool value)
        => element.SetValue(AutoFillLastColumnProperty, value);

    private static void OnAutoFillLastColumnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        list.SizeChanged -= OnListSizeChanged; // 幂等：重复设置只保留一个订阅
        if (e.NewValue is true)
        {
            list.SizeChanged += OnListSizeChanged;
        }
    }

    private static void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListView list
            || list.View is not GridView grid
            || grid.Columns.Count == 0)
        {
            return;
        }

        double others = 0;
        for (int i = 0; i < grid.Columns.Count - 1; i++)
        {
            others += grid.Columns[i].ActualWidth;
        }

        double target = Math.Max(MinLastColumnWidth,
            list.ActualWidth - others - SystemParameters.VerticalScrollBarWidth);

        GridViewColumn last = grid.Columns[grid.Columns.Count - 1];
        // 差值 <1px 不重设：避免「设置宽度 → 再次 SizeChanged」的自激抖动
        if (Math.Abs(last.ActualWidth - target) > 1)
        {
            last.Width = target;
        }
    }
}
