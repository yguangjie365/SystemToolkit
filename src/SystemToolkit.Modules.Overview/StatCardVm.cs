using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemToolkit.Modules.Overview;

/// <summary>
/// 顶部实时资源卡视图模型（按用户确认的参考图布局：图标块 + 名称/英文 + 型号 +
/// 温度徽章（右上）+ 使用率行（左标签右数值）+ 分段条）。
/// </summary>
public sealed class StatCardVm : INotifyPropertyChanged
{
    private string? _model;
    private string? _badgeText;
    private int _badgeLevel;
    private int? _percent;
    private string? _usageOverride;

    public StatCardVm(string icon, string nameZh, string nameEn, string usageLabel)
    {
        Icon = icon;
        NameZh = nameZh;
        NameEn = nameEn;
        UsageLabel = usageLabel;
    }

    public string Icon { get; }
    public string NameZh { get; }
    public string NameEn { get; }
    public string UsageLabel { get; }


    public string? Model
    {
        get => _model;
        set => SetField(ref _model, value);
    }

    /// <summary>右上角徽章文本（温度 / 未检测；null = 不显示）。</summary>
    public string? BadgeText
    {
        get => _badgeText;
        set => SetField(ref _badgeText, value);
    }

    /// <summary>徽章级别：0=正常绿 1=警告橙 2=危险红 3=未检测灰（用户 2026-09-04：61°C 不应标红）。</summary>
    public int BadgeLevel
    {
        get => _badgeLevel;
        set
        {
            if (SetField(ref _badgeLevel, value))
            {
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    /// <summary>使用率百分比（驱动分段条与默认数值）。</summary>
    public int? Percent
    {
        get => _percent;
        set
        {
            if (SetField(ref _percent, value))
            {
                OnPropertyChanged(nameof(UsageText));
                OnPropertyChanged(nameof(SegLevel));
            }
        }
    }

    /// <summary>分段条分级（用户 2026-09-04：占用也要分级，7% 不应显红）——&lt;60 绿 / 60-85 橙 / ≥85 红。</summary>
    public int SegLevel => _percent switch
    {
        null or 0 => 0,
        < 60 => 0,
        < 85 => 1,
        _ => 2,
    };

    /// <summary>
    /// 使用区文本覆盖（如存储卡显示「348 GB / 931 GB」总占用）。
    /// null 时显示百分比默认值。
    /// </summary>
    public string? UsageOverride
    {
        get => _usageOverride;
        set
        {
            if (SetField(ref _usageOverride, value))
            {
                OnPropertyChanged(nameof(UsageText));
            }
        }
    }

    public string UsageText => _usageOverride ?? (_percent is null ? "—" : _percent + "%");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
