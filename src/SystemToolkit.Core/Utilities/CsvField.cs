namespace SystemToolkit.Core.Utilities;

/// <summary>
/// CSV 字段转义唯一口径（RFC 4180 引号 + 公式注入前缀处置）。
/// <para>
/// 🔴 为什么必须有公式处置（v10 审查 🟠-2）：导出件的字段含<b>对端可控输入</b>
/// （NBSTAT/DNS 反查的主机名、接收文件名、厂商串）——Excel/WPS 把以
/// <c>= + - @</c>（及制表符/回车）开头的单元格当<b>公式执行</b>，
/// 恶意设备自报 <c>=cmd|…</c> 主机名即可在"用户打开自己导出的事件表"时触发。
/// 前置单引号是 OWASP 推荐处置：人读几乎无损，公式不再解析。
/// </para>
/// </summary>
public static class CsvField
{
    /// <summary>转义单个字段：先做公式前缀处置，再按 RFC 4180 决定是否加引号（内部引号翻倍）。</summary>
    public static string Escape(string? value)
    {
        string text = value ?? string.Empty;
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            text = "'" + text;
        }

        bool needsQuote = text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r');
        return needsQuote ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }
}
