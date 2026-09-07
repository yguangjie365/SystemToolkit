using System.Windows;
using SystemToolkit.Core.Software.Models;

namespace SystemToolkit.Modules.AppManager;

/// <summary>编辑结果：Item=保存后的条目；Deleted=true 表示用户删除。</summary>
public sealed class SoftwareEditResult
{
    public object? Item { get; init; }

    public bool Deleted { get; init; }
}

/// <summary>
/// 软件编辑对话框（用户 2026-09-04：第三方列表编辑功能必须有）。
/// 双模式：WingetPackage（名称/ID/分类/描述）与 ManualSoftware（名称/分类/描述/下载链接/本地安装包）。
/// 空名称保存视为取消。
/// </summary>
public partial class SoftwareEditWindow : Window
{
    private bool _deleted;

    private SoftwareEditWindow()
    {
        InitializeComponent();
    }

    /// <summary>editItem：WingetPackage / ManualSoftware / null（新增手动软件）。返回 null = 取消。</summary>
    public static SoftwareEditResult? Show(Window? owner, object? editItem)
    {
        var win = new SoftwareEditWindow { Owner = owner };
        win.LoadFields(editItem);
        bool? dialogResult = win.ShowDialog();
        if (dialogResult != true)
        {
            return null;
        }

        if (win._deleted)
        {
            return new SoftwareEditResult { Deleted = true };
        }

        return new SoftwareEditResult { Item = win.BuildItem(editItem) };
    }

    private void LoadFields(object? editItem)
    {
        switch (editItem)
        {
            case WingetPackage pkg:
                Title = "编辑软件（winget）";
                NameBox.Text = pkg.Name;
                IdBox.Text = pkg.Id;
                CategoryBox.Text = pkg.Category;
                DescriptionBox.Text = pkg.Description;
                // winget 条目：隐藏手动字段
                UrlLabel.Visibility = Visibility.Collapsed;
                UrlBox.Visibility = Visibility.Collapsed;
                PathLabel.Visibility = Visibility.Collapsed;
                PathBox.Visibility = Visibility.Collapsed;
                break;

            case ManualSoftware manual:
                Title = "编辑手动软件";
                NameBox.Text = manual.Name;
                IdLabel.Visibility = Visibility.Collapsed;
                IdBox.Visibility = Visibility.Collapsed;
                CategoryBox.Text = manual.Category;
                DescriptionBox.Text = manual.Description;
                UrlBox.Text = manual.DownloadUrl;
                PathBox.Text = manual.LocalInstallerPath;
                break;

            default:
                Title = "添加手动软件";
                IdLabel.Visibility = Visibility.Collapsed;
                IdBox.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private object? BuildItem(object? original)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            System.Windows.MessageBox.Show("名称不能为空。", "保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null; // 视为未完成：窗口不关闭（此处简单返回 null 上层当作取消）
        }

        if (original is WingetPackage pkg)
        {
            pkg.Name = NameBox.Text.Trim();
            pkg.Id = IdBox.Text.Trim();
            pkg.Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? "其他" : CategoryBox.Text.Trim();
            pkg.Description = DescriptionBox.Text.Trim();
            return pkg;
        }

        ManualSoftware manual = original as ManualSoftware ?? new ManualSoftware();
        manual.Name = NameBox.Text.Trim();
        manual.Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? "其他" : CategoryBox.Text.Trim();
        manual.Description = DescriptionBox.Text.Trim();
        manual.DownloadUrl = UrlBox.Text.Trim();
        manual.LocalInstallerPath = PathBox.Text.Trim();
        return manual;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            System.Windows.MessageBox.Show("名称不能为空。", "保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 审查 2026-09-04（P2）：与导入校验同源——下载链接仅允许 http(s)，防 file:/自定义协议被 shell 执行
        //（导入路径走 EnvCatalogValidator，编辑路径此前零校验）
        string url = UrlBox.Text.Trim();
        if (url.Length > 0 && !EnvCatalogValidator.IsValidHttpUrl(url))
        {
            System.Windows.MessageBox.Show("下载链接必须是 http(s) 地址。", "保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定删除该条目吗？（仅从清单移除，不影响已安装软件）",
                "删除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _deleted = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // IsCancel=true 已处理关闭
    }
}
