using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SystemToolkit.Core.Overview.Models;

/// <summary>详情卡内的一行键值（键名居左灰字，值居右主文字）。</summary>
/// <remarks>
/// 与 <see cref="OverviewItem"/> 一并被磁盘缓存序列化：构造参数名必须与属性名一一对应，
/// 否则旧缓存反序列化会抛异常并退化为冷启动采集。改名请看缓存契约测试。
/// </remarks>
public sealed class OverviewRow
{
    /// <summary>按键值初始化一行（缓存契约：参数名与属性名一一对应）。</summary>
    public OverviewRow(string key, string value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>键名（居左灰字，如"核心数"）。</summary>
    public string Key { get; }

    /// <summary>值（居右主文字）。</summary>
    public string Value { get; }
}

/// <summary>
/// 概览卡片条目。两种形态：
/// - 统计卡（顶部大数字）：Value 为大号百分数/容量，Percent 有值时带细进度条；
/// - 详情卡：Rows 为键值行集合（型号/厂商/频率等一行一条）。
/// </summary>
/// <remarks>
/// 本类型会被序列化进磁盘缓存（%LOCALAPPDATA%\SystemToolkit\overview-cache.json）。
/// 只读字段全走唯一的公共构造函数，System.Text.Json 走「参数化构造」还原：
/// 每个构造参数名必须能匹配到一个属性名（大小写不敏感），否则旧缓存反序列化直接
/// 抛异常 → 被降级为冷启动采集（慢但结果正确）。改动参数名 / 属性名 / 增删属性前，
/// 请先跑 OverviewViewModelTests 里的缓存契约用例。
/// 可写属性（Value/Sub/Percent/PercentLabel/SparkPoints）为实时刷新而设
/// （概览页 CPU/GPU 占用每 2 秒采样一次，直接改实例触发 INPC，无需重建卡片），
/// 反序列化仍由构造函数负责，setter 不参与缓存还原；SparkPoints 不进缓存。
/// </remarks>
public sealed class OverviewItem : INotifyPropertyChanged
{
    /// <summary>初始化卡片全部字段（缓存契约：参数名与属性名一一对应，见 remarks）。</summary>
    public OverviewItem(string icon, string label, string? value = null, string? sub = null,
        IReadOnlyList<OverviewRow>? rows = null, int? percent = null, string? percentLabel = null)
    {
        Icon = icon;
        Label = label;
        _value = value;
        _sub = sub;
        Rows = rows ?? Array.Empty<OverviewRow>();
        _percent = percent;
        _percentLabel = percentLabel;
    }

    /// <summary>卡片图标（Segoe MDL2 字形）。</summary>
    public string Icon { get; }

    /// <summary>字段名（如“处理器”“操作系统”）。</summary>
    public string Label { get; }

    private string? _value;

    /// <summary>主值（统计卡为大号文字；详情卡结构化信息放 Rows，此字段可空）。</summary>
    public string? Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    private string? _sub;

    /// <summary>副行/注解（统计卡显示在大数字旁，详情卡可空）。</summary>
    public string? Sub
    {
        get => _sub;
        set => SetField(ref _sub, value);
    }

    /// <summary>键值行明细（详情卡核心内容；统计卡为空）。</summary>
    public IReadOnlyList<OverviewRow> Rows { get; }

    private int? _percent;

    /// <summary>进度条百分比（0-100，可选）。非 null 时显示细进度条。</summary>
    public int? Percent
    {
        get => _percent;
        set => SetField(ref _percent, value);
    }

    private string? _percentLabel;

    /// <summary>进度条旁的说明文字（如“使用率 11%”）。与 Percent 同步出现。</summary>
    public string? PercentLabel
    {
        get => _percentLabel;
        set => SetField(ref _percentLabel, value);
    }

    private string? _badgeText;

    /// <summary>
    /// 卡片右上角徽章文本（概览卡显示温度等关键单值；null = 不显示）。
    /// 复用增量（2026-09-04）：概览页改版新增，旧工程无此需求。
    /// </summary>
    public string? BadgeText
    {
        get => _badgeText;
        set => SetField(ref _badgeText, value);
    }

    private IReadOnlyList<double>? _sparkPoints;

    /// <summary>
    /// 迷你趋势图样本（统计卡专用，实时占用历史）。不进磁盘缓存（趋势只是会话内窗口）。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<double>? SparkPoints
    {
        get => _sparkPoints;
        set => SetField(ref _sparkPoints, value);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
