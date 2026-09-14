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
        if (_busy)
        {
            // 🟠 V14-O4：与下方「暂无数据」分支同口径——导出是"点了必须有回应"的操作，
            // 采集在途时原先是静默 return（用户侧=点了没反应）。
            NotifyUser?.Invoke("数据采集中，请稍候再导出。", "导出概览报告");
            return;
        }

        if (_data is null)
        {
            // 审查 🟠-2：无数据可导出时显式告知，禁止「点了没反应」
            NotifyUser?.Invoke("暂无数据可导出（请等待首次采集完成）。", "导出概览报告");
            return;
        }

        try
        {
            string? path = PickSavePath?.Invoke();
            if (string.IsNullOrEmpty(path))
            {
                return; // 用户取消：零副作用
            }

            string markdown = OverviewReportBuilder.Build(_data, System.Environment.MachineName, DateTimeOffset.Now);
            // 审查 🔴-1（2026-09-10）：改走原子写——裸 File.WriteAllText 断电/被杀会留半截文件
            SystemToolkit.Core.Utilities.AtomicFile.WriteAllText(path, markdown);
            _logger.Info($"报告已导出：{path}");
            NotifyUser?.Invoke("报告已导出到：\n" + path, "导出成功");
        }
        catch (Exception ex)
        {
            _logger.Error("导出报告失败", ex);
            NotifyUser?.Invoke("导出失败：" + ex.Message, "导出报告");
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
        OverviewItem? osPanel = SystemPanels.FirstOrDefault(p => p.Label == OverviewLabels.OperatingSystem);
        // 🟠 V14-O5：文案一律取 OverviewLabels（与采集侧 OverviewService 共用常量）——
        // 此前是裸字面量，上游改文案后这里会静默失配（副标题里 OS/运行时长整段消失，无守卫可抓）。
        string? os = osPanel?.Rows.FirstOrDefault(r => r.Key == OverviewLabels.OperatingSystem)?.Value;
        string? uptime = osPanel?.Rows.FirstOrDefault(r => r.Key == OverviewLabels.Uptime)?.Value;
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
