using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemToolkit.Modules.Overview;

/// <summary>
/// 「网络与存储健康」卡里的一个面板（左：网络连接；右：磁盘健康）。
/// 与「实时占用进程」卡的面板同构，视觉保持一致。
/// </summary>
public sealed class HealthPanelVm : INotifyPropertyChanged
{
    /// <summary>初始化面板。</summary>
    /// <param name="icon">Segoe MDL2 字形。</param>
    /// <param name="label">面板标题。</param>
    public HealthPanelVm(string icon, string label)
    {
        Icon = icon;
        Label = label;
    }

    /// <summary>面板图标。</summary>
    public string Icon { get; }

    /// <summary>面板标题。</summary>
    public string Label { get; }

    private string _note = "等待采集…";

    /// <summary>
    /// 右上角说明（刷新节奏 / 端点总数 / **为何没有数据**）。
    /// 🔴 没有数据时必须写明原因（如"未检测到存储设备（需管理员权限）"），
    /// 空面板被读成"一切正常"就是状态欺骗。
    /// </summary>
    public string Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    /// <summary>行集合（固定行数，不足补空行——卡片高度恒定）。</summary>
    public ObservableCollection<HealthRowVm> Rows { get; } = new();

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>面板里的一行：序号 + 名称 + 说明。</summary>
public sealed class HealthRowVm
{
    /// <summary>初始化一行。</summary>
    /// <param name="rank">序号文本。</param>
    /// <param name="name">名称（进程名 / 磁盘型号）。</param>
    /// <param name="detail">说明（端点与监听 / 温度与 SMART 属性数）。</param>
    public HealthRowVm(string rank, string name, string detail)
    {
        Rank = rank;
        Name = name;
        Detail = detail;
    }

    /// <summary>序号。</summary>
    public string Rank { get; }

    /// <summary>名称。</summary>
    public string Name { get; }

    /// <summary>说明。</summary>
    public string Detail { get; }

    /// <summary>空行（维持面板高度）。</summary>
    public static HealthRowVm Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}
