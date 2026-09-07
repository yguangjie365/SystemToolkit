using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SystemToolkit.Core.Overview.Models;
using SystemToolkit.Core.Overview.Services;

namespace SystemToolkit.Modules.Overview;

public partial class OverviewViewModel
{
    /// <summary>导出 Markdown 概览报告（危险操作四步之"记录"侧：成功失败均留痕）。</summary>
    private void ExportReport()
    {
        if (_busy || _data is null)
        {
            return;
        }

        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出概览报告",
                FileName = $"SystemToolkit-概览报告-{DateTime.Now:yyyyMMdd-HHmm}",
                Filter = "Markdown 文档 (*.md)|*.md|所有文件 (*.*)|*.*",
                DefaultExt = ".md",
            };
            if (dialog.ShowDialog() != true)
            {
                return; // 用户取消：零副作用
            }

            string markdown = OverviewReportBuilder.Build(_data, System.Environment.MachineName, DateTimeOffset.Now);
            System.IO.File.WriteAllText(dialog.FileName, markdown, System.Text.Encoding.UTF8);
            _logger.Info($"报告已导出：{dialog.FileName}");
            System.Windows.MessageBox.Show(
                "报告已导出到：\n" + dialog.FileName,
                "导出成功",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("导出报告失败", ex);
            System.Windows.MessageBox.Show(
                "导出失败：" + ex.Message,
                "导出报告",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void SetBadge(string label, string? text, int level)
    {
        StatCardVm? card = StatCards.FirstOrDefault(c => c.NameZh == label);
        if (card is not null)
        {
            card.BadgeLevel = level;
            card.BadgeText = text;
        }
    }

    private string BuildHeaderSubtitle()
    {
        OverviewItem? osPanel = SystemPanels.FirstOrDefault(p => p.Label == "操作系统");
        string? os = osPanel?.Rows.FirstOrDefault(r => r.Key == "操作系统")?.Value;
        string? uptime = osPanel?.Rows.FirstOrDefault(r => r.Key == "运行时长")?.Value;
        return string.Join(" · ", new[]
        {
            System.Environment.MachineName,
            os,
            uptime is null ? null : "运行 " + uptime,
            _showingSnapshot ? "快照 · 采集中…" : null,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (T item in source)
        {
            target.Add(item);
        }
    }

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
