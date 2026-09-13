using SystemToolkit.Core.Network.Connections;
using SystemToolkit.Core.Overview.Services;
using SystemToolkit.Modules.Overview;

namespace SystemToolkit.Tests;

/// <summary>
/// 「网络与存储健康」卡的面板判据（B7b/B7c 的 UI 出口）。
/// <para>
/// 这些判据原先都是 <c>private</c>，只能靠人眼看着界面——本批把它们放宽为 <c>internal</c> 直接钉住
/// （判据 private + 测试里复制规则 = 假测试，本仓有明令）。
/// 重点钉两条：① **面板行数恒定**（不足补空行，卡片高度不跳）；
/// ② **没有数据时必须说明原因**，且文案里**不得出现"正常/健康"这类结论**（没有数据 ≠ 健康）。
/// </para>
/// </summary>
public class HealthPanelTests
{
    [Fact]
    public void FillHealthRows_EmptyInput_StillProducesFullRowCount()
    {
        var panel = new HealthPanelVm("x", "面板");

        OverviewViewModel.FillHealthRows(panel, Array.Empty<HealthRowVm>());

        Assert.Equal(5, panel.Rows.Count);
        Assert.All(panel.Rows, r => Assert.Equal(string.Empty, r.Name));
    }

    [Fact]
    public void FillHealthRows_FewerThanCapacity_PadsWithEmptyRows()
    {
        var panel = new HealthPanelVm("x", "面板");
        HealthRowVm[] rows = [new("1", "a", "d1"), new("2", "b", "d2")];

        OverviewViewModel.FillHealthRows(panel, rows);

        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal("a", panel.Rows[0].Name);
        Assert.Equal("b", panel.Rows[1].Name);
        Assert.Equal(string.Empty, panel.Rows[2].Name);
        Assert.Equal(string.Empty, panel.Rows[4].Name);
    }

    [Fact]
    public void FillHealthRows_MoreThanCapacity_KeepsFirstRowsOnly()
    {
        var panel = new HealthPanelVm("x", "面板");
        HealthRowVm[] rows = [.. Enumerable.Range(1, 9).Select(i => new HealthRowVm(i.ToString(), "n" + i, "d"))];

        OverviewViewModel.FillHealthRows(panel, rows);

        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal("n5", panel.Rows[4].Name);
    }

    [Fact]
    public void StorageNote_NoDevices_SaysWhyAndDoesNotClaimHealthy()
    {
        string note = OverviewViewModel.StorageNote(new StorageHealthSnapshot([]));

        Assert.Contains("管理员权限", note);
        // 🔴 没有数据时绝不能出现"结论性"措辞
        Assert.DoesNotContain("正常", note);
        Assert.DoesNotContain("健康", note);
    }

    [Fact]
    public void StorageNote_DevicesWithoutSmart_DoesNotClaimHealthy()
    {
        StorageHealthSnapshot snapshot = new([Device("Disk A", 35f, attributeCount: 0)]);

        string note = OverviewViewModel.StorageNote(snapshot);

        Assert.Contains("SMART", note);
        Assert.DoesNotContain("正常", note);
        Assert.DoesNotContain("健康", note);
    }

    [Fact]
    public void StorageNote_AnyDeviceWithSmart_ReportsPlainDeviceCount()
    {
        StorageHealthSnapshot snapshot = new([Device("Disk A", 35f, 3), Device("Disk B", null, 0)]);

        string note = OverviewViewModel.StorageNote(snapshot);

        // 只要有一块盘给出了属性，就不必在每次刷新的说明里重复"仅温度"——
        // 逐行已经各自标了"3 项" / "无 SMART"（见 BuildStorageRows 用例）
        Assert.Contains("2 块盘", note);
        Assert.DoesNotContain("正常", note);
        Assert.DoesNotContain("健康", note);
    }

    [Fact]
    public void NetworkNote_NoEndpoints_SaysNothingWasObtained()
    {
        string note = OverviewViewModel.NetworkNote(new OverviewViewModel.NetworkPanelData(0, []));

        Assert.Contains("未获取", note);
    }

    [Fact]
    public void NetworkNote_WithEndpoints_ReportsCountAndCadence()
    {
        string note = OverviewViewModel.NetworkNote(new OverviewViewModel.NetworkPanelData(42, []));

        Assert.Contains("42", note);
        Assert.Contains("2 秒", note);
    }

    [Fact]
    public void BuildNetworkRows_FormatsSummary_AndTruncatesLongPortLists()
    {
        ProcessConnectionSummary[] summaries =
        [
            new(1234, "chrome", Total: 12, Established: 9, Listening: 3, ListenPorts: [80, 443, 8080, 9000]),
            new(4321, "idle-app", Total: 1, Established: 0, Listening: 0, ListenPorts: []),
        ];

        IReadOnlyList<HealthRowVm> rows = OverviewViewModel.BuildNetworkRows(summaries);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0].Rank);
        Assert.Equal("chrome", rows[0].Name);
        Assert.Contains("12 端点", rows[0].Detail);
        Assert.Contains("监听 80/443", rows[0].Detail);
        Assert.EndsWith("…", rows[0].Detail); // 超过两个监听端口要收尾，不能无限长
        // 只有已连接、无监听：不出现"监听"字样
        Assert.Contains("1 端点", rows[1].Detail);
        Assert.DoesNotContain("监听", rows[1].Detail);
    }

    [Fact]
    public void BuildStorageRows_ReportsTemperatureOrUnknown_AndAttributeCount()
    {
        StorageHealthSnapshot snapshot = new(
        [
            Device("Disk A", 41.5f, attributeCount: 3),
            Device("Disk B", null, attributeCount: 0),
        ]);

        IReadOnlyList<HealthRowVm> rows = OverviewViewModel.BuildStorageRows(snapshot);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Disk A", rows[0].Name);
        Assert.Contains("41.5°C", rows[0].Detail);
        Assert.Contains("3 项", rows[0].Detail);
        // 没温度就说"未知"，没 SMART 就说"无 SMART"——都不许留空
        Assert.Contains("温度未知", rows[1].Detail);
        Assert.Contains("无 SMART", rows[1].Detail);
    }

    private static StorageDeviceHealth Device(string name, float? temperatureC, int attributeCount) =>
        new(
            name,
            temperatureC,
            Enumerable.Range(0, attributeCount)
                .Select(i => new SensorReading(name, "Storage", "属性" + i, "Data", i, string.Empty))
                .ToList());
}
