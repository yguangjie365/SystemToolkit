using System.Text;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// 局域网事件 → CSV（落地计划 B3-②，源报告 P1-2「事件流 CSV 导出」）。
/// <para>
/// 为什么放在 Core 而不是 VM 里拼字符串：CSV 的转义规则（逗号 / 引号 / 换行）是**格式契约**，
/// 拼错了会静默产出错行的文件——那种错误要到 Excel 里才被发现，而且看起来"像事件记录本来就乱"。
/// 放这里可以用纯函数单测钉住（与互传的 <c>TransferHistoryCsv</c> 同范式）。
/// </para>
/// <para>
/// <b>列序</b>：时间 / 事件类型 / IP / MAC / 厂商 / 主机名 / 详情。
/// MAC 取**新观测值**（<see cref="LanEvent.NewMac"/>），空则回落设备当前 MAC；
/// 绑定变更的"旧→新"仍由「详情」承载（那是人读摘要）。
/// </para>
/// <para>
/// <b>行序</b>：沿用持久化的新→旧顺序，与界面事件流一致（"所见即所得"，
/// 同互传历史导出的口径）。
/// </para>
/// </summary>
public static class LanEventCsv
{
    /// <summary>默认导出文件名前缀（调用方只在后面补时间戳）。</summary>
    public const string FileNamePrefix = "SystemToolkit-局域网事件-";

    /// <summary>表头（列顺序与 <see cref="Build"/> 一致）。</summary>
    public const string Header = "时间,事件类型,IP,MAC,厂商,主机名,详情";

    /// <summary>
    /// 生成 CSV 文本（不含 BOM；BOM 由写出方决定——Excel 需要它才认 UTF-8）。
    /// </summary>
    /// <param name="events">事件（持久化顺序：新→旧）。</param>
    /// <param name="devicesByIp">
    /// 可选的设备索引，用于补齐厂商/主机名。事件本身不带这两个字段，
    /// 而它们在取证时正是"这条事件是谁"的关键。缺索引时留空，不臆造。
    /// </param>
    public static string Build(
        IEnumerable<LanEvent> events,
        IReadOnlyDictionary<string, LanDevice>? devicesByIp = null)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");

        foreach (LanEvent evt in events)
        {
            LanDevice? device = null;
            if (devicesByIp is not null && devicesByIp.TryGetValue(evt.Ip, out LanDevice? found))
            {
                device = found;
            }

            string mac = evt.NewMac ?? device?.Mac ?? string.Empty;

            sb.Append(Escape(evt.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Escape(TypeText(evt.Type))).Append(',')
              .Append(Escape(evt.Ip)).Append(',')
              .Append(Escape(mac)).Append(',')
              .Append(Escape(device?.Vendor)).Append(',')
              .Append(Escape(device?.Hostname)).Append(',')
              .Append(Escape(evt.Detail))
              .Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>事件类型的中文文案（CSV 与告警文本共用一处，避免两处各写一份）。</summary>
    public static string TypeText(LanEventType type) => type switch
    {
        LanEventType.NewDevice => "新设备",
        LanEventType.BindingChanged => "绑定变更",
        LanEventType.Conflict => "IP 冲突",
        LanEventType.DeviceGone => "离线",
        _ => type.ToString(),
    };

    /// <summary>
    /// 单字段转义：委托 <see cref="SystemToolkit.Core.Utilities.CsvField"/>（RFC 4180 + 公式注入前缀处置）。
    /// 🟠 v10-2：主机名/厂商等字段为对端可控输入，"=" 开头会被 Excel/WPS 当公式执行。
    /// </summary>
    private static string Escape(string? value) => SystemToolkit.Core.Utilities.CsvField.Escape(value);
}
