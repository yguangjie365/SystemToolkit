using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemToolkit.Modules.Overview;

/// <summary>
/// 「实时占用进程」卡里的一个榜单面板（左：CPU 榜 / 右：内存榜）。
/// 形态与既有 <c>SystemPanels</c> 同构（图标 + 标题 + 若干行），只是行由 2s 采样驱动刷新。
/// </summary>
public sealed class ProcessPanelVm : INotifyPropertyChanged
{
    /// <summary>初始化面板（图标 / 标题 / 数值列表头）。</summary>
    /// <param name="icon">Segoe MDL2 字形。</param>
    /// <param name="label">中文标题。</param>
    /// <param name="valueHeader">数值列表头（如 "CPU" / "内存"）。</param>
    public ProcessPanelVm(string icon, string label, string valueHeader)
    {
        Icon = icon;
        Label = label;
        ValueHeader = valueHeader;
    }

    /// <summary>面板图标（Segoe MDL2 字形）。</summary>
    public string Icon { get; }

    /// <summary>面板标题。</summary>
    public string Label { get; }

    /// <summary>数值列表头。</summary>
    public string ValueHeader { get; }

    private string _note = "等待首次采样…";

    /// <summary>
    /// 面板右上角说明（"每 2 秒刷新" / "首次采样中…"）。
    /// 🔴 无 CPU 基线时必须**如实说明**，不得用 0% 或空白冒充"已测量"。
    /// </summary>
    public string Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    /// <summary>
    /// 榜单行。行数**恒定**（不足由空行补齐）——卡片高度不随排序变化而跳动。
    /// </summary>
    public ObservableCollection<ProcessRowVm> Rows { get; } = new();

    /// <inheritdoc />
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

/// <summary>榜单里的一行：序号 / 进程名 / PID / 数值。</summary>
/// <remarks>
/// 不可变（每拍整表重建）：行数与顺序都会随排序变化，逐字段更新反而要额外处理"这一行换人了"。
/// </remarks>
public sealed class ProcessRowVm
{
    /// <summary>空行（榜单不足 <c>TopRowCount</c> 时占位，保证卡片高度恒定）。</summary>
    public static readonly ProcessRowVm Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>初始化一行。</summary>
    /// <param name="rank">序号（1 起）。</param>
    /// <param name="name">进程名。</param>
    /// <param name="pid">进程 ID 文本。</param>
    /// <param name="value">数值文本（CPU 百分比 / 内存容量）。</param>
    public ProcessRowVm(string rank, string name, string pid, string value)
    {
        Rank = rank;
        Name = name;
        Pid = pid;
        Value = value;
    }

    /// <summary>序号文本。</summary>
    public string Rank { get; }

    /// <summary>进程名。</summary>
    public string Name { get; }

    /// <summary>进程 ID 文本。</summary>
    public string Pid { get; }

    /// <summary>数值文本。</summary>
    public string Value { get; }
}
