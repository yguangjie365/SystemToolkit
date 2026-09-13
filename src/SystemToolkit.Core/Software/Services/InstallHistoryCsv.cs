using System.Text;
using SystemToolkit.Core.Software.Models;

namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// 安装历史的 CSV 导出（纯函数，无 IO —— 落盘由调用方走 <c>AtomicFile</c>）。
/// 与文件互传的导出同一套约定：RFC4180 转义 + **UTF-8 带 BOM**（Excel 打开中文不乱码）。
/// </summary>
public static class InstallHistoryCsv
{
    /// <summary>导出文件默认名前缀（含中文，不带扩展名与时间戳）。</summary>
    public const string FileNamePrefix = "SystemToolkit-安装历史-";

    /// <summary>表头（列名固定，便于用户在 Excel 里做透视）。</summary>
    public const string Header = "时间,动作,软件,包ID,原版本,新版本,结果,说明";

    /// <summary>把记录集渲染为 CSV 文本（不含 BOM；BOM 由写入方加）。</summary>
    public static string Build(IEnumerable<InstallHistoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');

        foreach (InstallHistoryEntry entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            sb.Append(Escape(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Escape(InstallHistoryLabels.ActionText(entry.Action))).Append(',')
              .Append(Escape(entry.Name)).Append(',')
              .Append(Escape(entry.PackageId)).Append(',')
              .Append(Escape(entry.FromVersion)).Append(',')
              .Append(Escape(entry.ToVersion)).Append(',')
              .Append(Escape(InstallHistoryLabels.OutcomeText(entry.Outcome))).Append(',')
              .Append(Escape(entry.Detail)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>RFC4180 转义：含逗号/引号/换行的字段用双引号包裹，内部引号翻倍。</summary>
    internal static string Escape(string? value)
    {
        string text = value ?? "";
        bool needsQuote = text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        return needsQuote
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }
}
