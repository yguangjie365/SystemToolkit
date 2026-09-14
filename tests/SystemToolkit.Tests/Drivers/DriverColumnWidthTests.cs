using SystemToolkit.Modules.DriverManager;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 「驱动列表列宽不溢出」行为锁（B-🟡-2，2026-09-15 实机反馈后建立）。
/// <para>
/// 缺陷类：<c>OnListSizeChanged</c> 里 `surplus = viewport - checkboxCol - dateCol - nameWidth`
/// **一分钱最小值都没扣**，而 nameWidth 又按「整个剩余宽度」取到上限 280
/// ⇒ 六列总宽恒 `= viewport + 320`（80 + 90 + 150 三列最小值被重复计入）。
/// 叠加 XAML 的 <c>ScrollViewer.HorizontalScrollBarVisibility="Disabled"</c>
/// ⇒ 末列右侧被裁且**无法横向滚动**；实机症状是窄窗下「设备/状态」列只剩一个徽章、设备名看不见。
/// </para>
/// <para>
/// 🔴 为什么既有守卫抓不到：这是**纯几何算式**，不涉及令牌/绑定/命令，
/// <c>UiTokenRatchetTests</c>（裸数字）、<c>TokenCoverage</c>、<c>ViewLoadSmoke</c> 全都不覆盖；
/// 缺陷只在"宽度算错"这一层，且**视觉静默**（不报错、不崩、测试全绿）。
/// </para>
/// <para>
/// 判据：① **不变量** —— viewport 足够时 `36 + Name + Provider + Version + 72 + Device == viewport`
/// （既不溢出也不留白）；② 窄于物理下限（578）时总宽恒为 578（已到极限，不再假装能塞下）；
/// ③ 名称列 ∈ [150, 280]（方案 B 的下限，窄窗时先收窄把空间让给设备列）；
/// ④ 三列各自不低于最小值；⑤ 两档实机宽度（664 / 787）的**快照值**，防后续改动把比例漂回去。
/// </para>
/// </summary>
public class DriverColumnWidthTests
{
    private const double CheckboxCol = 36;
    private const double DateCol = 72;
    private static readonly double[] Minimums = { 150, 80, 90, 150 };   // 名称 / 提供商 / 版本 / 设备

    [Fact]
    public void ColumnWidths_MustExactlyFillViewport()
    {
        var violations = new List<string>();
        for (double viewport = 300; viewport <= 2000; viewport += 1)
        {
            (double name, double provider, double version, double device) =
                DriverManagerView.ComputeColumnWidths(viewport);
            double total = CheckboxCol + name + provider + version + DateCol + device;

            // ① viewport ≥ 物理下限时：总宽必须恰好 = viewport
            // ② viewport < 物理下限时：总宽恒为下限（挤不下是物理事实，不得靠"少算一列"来假装）
            double expected = Math.Max(viewport, 578);
            if (Math.Abs(total - expected) > 0.001)
            {
                violations.Add($"viewport={viewport}: 总宽={total:F1}，应={expected:F1}（{total - expected:+0.0;-0.0}）");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ColumnWidths_MustRespectMinimumsAndNameElasticity()
    {
        var violations = new List<string>();
        for (double viewport = 300; viewport <= 2000; viewport += 1)
        {
            (double name, double provider, double version, double device) =
                DriverManagerView.ComputeColumnWidths(viewport);

            if (name < 150 - 0.001 || name > 280 + 0.001)
            {
                violations.Add($"viewport={viewport}: 名称列={name:F1} 越出 [150,280]");
            }

            double[] actual = { name, provider, version, device };
            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] < Minimums[i] - 0.001)
                {
                    violations.Add($"viewport={viewport}: 第{i}列={actual[i]:F1} 低于最小值 {Minimums[i]}");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// 两档**实机宽度**的快照（截图量得的内容宽 688 / 811，减滚动条占位 24）。
    /// 钉住具体数值：这三列比例（0.20 / 0.15 / 0.65）与名称列下限 150 是在真机上确认过的口径。
    /// </summary>
    [Theory]
    [InlineData(688, 236, 80, 90, 150)]     // 初始窗口：名称列已收到 236，设备列保住 150
    [InlineData(811, 280, 95.8, 101.85, 201.35)] // 最大化：名称列到上限 280，设备列 201
    public void ColumnWidths_RealWindowSnapshots(
        double listWidth, double expName, double expProvider, double expVersion, double expDevice)
    {
        (double name, double provider, double version, double device) =
            DriverManagerView.ComputeColumnWidths(listWidth - 24);

        Assert.Equal(expName, name, 2);
        Assert.Equal(expProvider, provider, 2);
        Assert.Equal(expVersion, version, 2);
        Assert.Equal(expDevice, device, 2);
    }

    /// <summary>方案 B 的**核心承诺**：窄窗把空间让给设备列（修复前窄窗末列只剩 100px）。</summary>
    [Fact]
    public void NarrowWindow_MustStillGiveDeviceColumnItsMinimum()
    {
        (_, _, _, double device) = DriverManagerView.ComputeColumnWidths(688 - 24);
        Assert.True(device >= 150, $"窄窗设备列仅 {device:F1}px —— 方案 B 承诺不低于 150");
    }
}
