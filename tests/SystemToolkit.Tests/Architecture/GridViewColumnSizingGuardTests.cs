using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SystemToolkit.UI.Common.Controls;
using Xunit;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 守卫（2026-09-10）：GridView 末列自适应填充 —— 消除「表头比内容多一个空白列」。
/// <para>
/// 根因：WPF GridView 的列宽固定时，若各列总宽 &lt; 视口宽，GridView 会自动补一个**空白表头列**
/// 填充剩余空间（无法关闭），用户看到的就是「标题 6 列、内容 5 列」。
/// 修复：<see cref="GridViewColumnSizing.AutoFillLastColumn"/> 让末列吃掉剩余宽度。
/// </para>
/// <para>
/// 反向验证：把附加属性去掉（或改回固定宽），本测试断言「列总宽 ≥ 视口宽」即会变红。
/// </para>
/// </summary>
public class GridViewColumnSizingGuardTests
{
    private const double HostWidth = 900;
    // 末列（C，100）是自适应目标，不计入固定列；固定列 = A(200) + B(200)
    private const double FixedColumnsTotal = 400;

    [Fact]
    public void AutoFillLastColumn_MakesColumnsFillViewport_NoSpareHeaderColumn()
    {
        Exception? captured = null;
        double columnsTotal = 0;
        double lastColumnWidth = 0;

        var thread = new Thread(() =>
        {
            try
            {
                (ListView list, GridView grid) = BuildList();
                GridViewColumnSizing.SetAutoFillLastColumn(list, true);

                // 不用 Show()：Measure/Arrange 即会触发布局与 SizeChanged（测试环境无需真实窗口）
                list.Measure(new Size(HostWidth, 300));
                list.Arrange(new Rect(0, 0, HostWidth, 300));
                list.UpdateLayout();

                columnsTotal = 0;
                foreach (GridViewColumn c in grid.Columns)
                {
                    columnsTotal += c.ActualWidth;
                }

                lastColumnWidth = grid.Columns[grid.Columns.Count - 1].ActualWidth;
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(captured);

        // ① 列总宽吃满视口（减去垂直滚动条）——如此 GridView 才没有剩余空间去补空白表头列
        double expectedMin = HostWidth - SystemParameters.VerticalScrollBarWidth - 1;
        Assert.True(columnsTotal >= expectedMin,
            $"列总宽 {columnsTotal:F1} 未吃满视口（应 ≥ {expectedMin:F1}）——GridView 会因此补出空白表头列");

        // ② 末列确实被撑开（= 视口宽 − 固定列 − 滚动条），且不小于最小宽
        double expectedLast = HostWidth - FixedColumnsTotal - SystemParameters.VerticalScrollBarWidth;
        Assert.True(Math.Abs(lastColumnWidth - expectedLast) <= 1.5,
            $"末列宽 {lastColumnWidth:F1} 与期望 {expectedLast:F1} 不符");
        Assert.True(lastColumnWidth >= 100, "末列宽不应低于最小钳制值 100");
    }

    [Fact]
    public void WithoutAutoFill_ColumnsDoNotFillViewport_ReproducingRootCause()
    {
        Exception? captured = null;
        double columnsTotal = 0;

        var thread = new Thread(() =>
        {
            try
            {
                (ListView list, GridView grid) = BuildList();

                list.Measure(new Size(HostWidth, 300));
                list.Arrange(new Rect(0, 0, HostWidth, 300));
                list.UpdateLayout();

                foreach (GridViewColumn c in grid.Columns)
                {
                    columnsTotal += c.ActualWidth;
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(captured);

        // 不启用时总宽就是固定值（≈500）< 视口 —— 这正是"空表头列"出现的条件（根因留证）
        Assert.True(columnsTotal < HostWidth - 100,
            $"未启用时列总宽 {columnsTotal:F1} 竟然吃满了视口，根因假设需重新核实");
    }

    private static (ListView List, GridView Grid) BuildList()
    {
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "A", Width = 200 });
        grid.Columns.Add(new GridViewColumn { Header = "B", Width = 200 });
        grid.Columns.Add(new GridViewColumn { Header = "C", Width = 100 });

        var list = new ListView
        {
            View = grid,
            ItemsSource = new[] { "row" },
        };
        return (list, grid);
    }
}
