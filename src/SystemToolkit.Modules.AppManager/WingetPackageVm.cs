using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SystemToolkit.Core.Software.Models;

namespace SystemToolkit.Modules.AppManager;

/// <summary>
/// winget 包行 VM（复用自旧工程 EnvManager 的 WingetPackageVm，原文迁移 +
/// 配色改用本项目令牌字典 + 新增批量勾选 IsSelected）。
/// </summary>
public partial class WingetPackageVm : ObservableObject
{
    private WingetPackageState _state;
    private string _versionText = "";
    private bool _isBusy;
    private bool _isSelected;

    public WingetPackageVm(WingetPackage model)
    {
        Model = model;
    }

    public WingetPackage Model { get; }

    public string Id => Model.Id;

    public string Name => Model.Name;

    public string Description => Model.Description;

    public string Category => Model.Category;

    /// <summary>按"字素簇"取首字符（Substring 遇 emoji/代理对会截出半个字符）。</summary>
    public string Initial
        => string.IsNullOrEmpty(Name) ? "?" : StringInfo.GetNextTextElement(Name).ToUpperInvariant();

    public bool IsQueryable => !string.IsNullOrWhiteSpace(Model.Id);

    public bool IsIdle => !IsBusy;

    public string VersionText
    {
        get => _versionText;
        set
        {
            if (SetField(ref _versionText, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public WingetPackageState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusKey));
                OnPropertyChanged(nameof(HasKnownState));
                OnPropertyChanged(nameof(IsActionEnabled));
                OnPropertyChanged(nameof(ActionHint));
                OnPropertyChanged(nameof(IsInstallVisible));
                OnPropertyChanged(nameof(IsUpgradeVisible));
                OnPropertyChanged(nameof(IsUninstallVisible));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsActionEnabled));
                OnPropertyChanged(nameof(ActionHint));
            }
        }
    }

    /// <summary>批量操作勾选（2026-09-04 新增：批量安装栏）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>状态徽章键：0=已安装(绿) 1=有更新(橙) 2=未安装(灰) 3=未知(灰)。XAML DataTrigger 用。</summary>
    public int StatusKey => State switch
    {
        WingetPackageState.Installed => 0,
        WingetPackageState.Updatable => 1,
        WingetPackageState.NotInstalled => 2,
        _ => 3,
    };

    public string StatusLabel => State switch
    {
        // 用户 2026-09-04：版本号已在"已装版本"列显示，状态徽章不再重复
        WingetPackageState.NotInstalled => "未安装",
        WingetPackageState.Installed => "已安装",
        WingetPackageState.Updatable => "有更新",
        _ => "状态未知",
    };

    public bool HasKnownState
        => State is WingetPackageState.Installed or WingetPackageState.NotInstalled or WingetPackageState.Updatable;

    public bool IsActionEnabled => IsIdle && HasKnownState;

    public string ActionHint => IsActionEnabled
        ? "操作"
        : HasKnownState ? "有 winget 操作进行中，请稍候" : "尚未刷新到安装状态，请先点「检测状态」";

    public bool IsInstallVisible
        => IsQueryable && State is WingetPackageState.NotInstalled or WingetPackageState.Unknown;

    public bool IsUpgradeVisible => IsQueryable && State == WingetPackageState.Updatable;

    public bool IsUninstallVisible
        => IsQueryable && State is WingetPackageState.Installed or WingetPackageState.Updatable or WingetPackageState.Unknown;

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name!);
        return true;
    }

    /// <summary>安装/升级成功后的局部状态更新：仅改本卡，不触发全量刷新。</summary>
    public void MarkInstalled()
    {
        State = WingetPackageState.Installed;
        VersionText = "";
    }

    /// <summary>卸载成功后的局部状态更新。</summary>
    public void MarkNotInstalled()
    {
        State = WingetPackageState.NotInstalled;
        VersionText = "";
    }

}
