using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SystemToolkit.Modules.Settings;

/// <summary>
/// 设置页（2026-09-07 建立）：左分组 + 右内容。当前承载「备份」+「外观（主题）」两个分区
/// （与 <see cref="SettingsViewModel"/> 类注释保持一致；旧注释"仅备份可用"已过期）。
/// 目录选择经 PickFolder 回调注入（VM 不依赖 Win32 对话框）。
/// </summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel Vm => (SettingsViewModel)DataContext;

    private bool _loaded;

    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
        // 视图被宿主摘挂/主题切换重建时退订，避免 VM（单例）累积订阅。
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmSelectedThemeIdChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 先幂等退订再订阅：视图会被摘挂重挂，Loaded 可能反复触发。
        // 必须放在 _loaded 早退**之前**，否则重挂后不再订阅。
        Vm.PropertyChanged -= OnVmSelectedThemeIdChanged;
        Vm.PropertyChanged += OnVmSelectedThemeIdChanged;

        if (_loaded)
        {
            return;
        }

        _loaded = true;
        Vm.PickFolder = title =>
        {
            var dialog = new OpenFolderDialog { Title = title };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
        };

        // 主题下拉（ADR-005）：Label 展示、Tag=id；同步当前主题（VM 端对相同值自动短路）
        ThemeCombo.ItemsSource = SettingsViewModel.ThemeOptions
            .Select(t => (object)new ComboBoxItem { Content = t.Label, Tag = t.Id })
            .ToList();
        foreach (object item in ThemeCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string id } && id == SystemToolkit.UI.Common.ThemeManager.CurrentThemeId)
            {
                ThemeCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackupSection is null || AppearanceSection is null)
        {
            return;
        }

        string tag = (SectionList.SelectedItem as ListBoxItem)?.Tag as string ?? "backup";
        BackupSection.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThemeComboSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string id })
        {
            Vm.SelectedThemeId = id;
        }
    }

    /// <summary>
    /// VM → View 的反向通道：把 VM 对 <see cref="SettingsViewModel.SelectedThemeId"/> 的回写
    /// （含 🟡 F-7「apply 抛异常时回退到实际主题」）同步到下拉选中。
    /// 此前该属性全仓零 XAML 绑定，回退值到不了下拉 —— 下拉显示"新主题"、界面仍是旧主题。
    /// </summary>
    private void OnVmSelectedThemeIdChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.SelectedThemeId) || !IsLoaded)
        {
            return;
        }

        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string current } && current == Vm.SelectedThemeId)
        {
            return;
        }

        foreach (object item in ThemeCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string id } && id == Vm.SelectedThemeId)
            {
                ThemeCombo.SelectedItem = item;
                break;
            }
        }
    }
}
