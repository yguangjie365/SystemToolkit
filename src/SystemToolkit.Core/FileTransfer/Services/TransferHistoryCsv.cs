using System.Text;
using SystemToolkit.Core.FileTransfer.Models;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 传输历史 → CSV（2026-09-13 批次 P3 ⑮）。
/// <para>
/// 为什么放在 Core 而不是 VM 里拼字符串：CSV 的转义规则（逗号 / 引号 / 换行）是**格式契约**，
/// 拼错了会静默产出错行的文件——那种错误在 Excel 里才被发现，而且看起来"像历史记录本来就乱"。
/// 放这里可以用纯函数单测钉住。
/// </para>
/// </summary>
public static class TransferHistoryCsv
{
    /// <summary>默认导出文件名前缀（VM 只在后面补时间戳）。</summary>
    public const string FileNamePrefix = "SystemToolkit-传输历史-";

    /// <summary>表头（列顺序与 <see cref="Build"/> 一致）。</summary>
    public const string Header = "时间,方向,文件名,对端,大小(字节),状态,原因码,原因说明";

    /// <summary>
    /// 生成 CSV 文本（不含 BOM；BOM 由写出方决定，Excel 需要它才认 UTF-8）。
    /// </summary>
    public static string Build(IEnumerable<TransferHistoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (TransferHistoryEntry entry in entries)
        {
            sb.Append(Escape(entry.FinishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Escape(entry.DirectionText)).Append(',')
              .Append(Escape(entry.FileName)).Append(',')
              .Append(Escape(entry.PeerEndpoint)).Append(',')
              .Append(entry.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(entry.StatusText)).Append(',')
              .Append(Escape(entry.ReasonCode ?? string.Empty)).Append(',')
              .Append(Escape(entry.ReasonText))
              .Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 单个字段的转义：含逗号 / 引号 / 换行时用双引号包裹，内部引号翻倍（RFC 4180）。
    /// <para>
    /// 🔴 不做这一步的后果很具体：文件名里一个逗号就会让整行错列，
    /// 用户导出的表格看着"像乱码"，却以为是软件坏了。
    /// </para>
    /// </summary>
    private static string Escape(string? value)
    {
        string text = value ?? string.Empty;
        bool needsQuote = text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r');
        return needsQuote ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }
}
