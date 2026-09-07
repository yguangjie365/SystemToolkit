using System.Text;
using SystemToolkit.Core.Overview.Models;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>
/// 硬件信息报告（Markdown）构建器：纯函数、零 WMI 依赖，可直接单测。
/// 内容：硬件统计卡 + 硬件/系统详情卡 + 传感器快照（若有）。
/// 不含已安装程序清单（隐私面大且与「硬件信息」主题无关）。
/// </summary>
public static class OverviewReportBuilder
{
    /// <summary>构建 Markdown 报告全文。</summary>
    public static string Build(OverviewData data, string machineName, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 硬件信息报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{generatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- 计算机：{machineName}");
        sb.AppendLine();

        AppendSection(sb, "硬件概览", data.HardwareStats);
        AppendSection(sb, "硬件详情", data.Hardware);
        AppendSection(sb, "系统概览", data.System);

        // 传感器快照（探测失败/驱动不可用时整节省略，不输出空表）
        List<SensorReading>? readings = data.Sensors?.Sensors;
        if (readings is { Count: > 0 })
        {
            sb.AppendLine("## 传感器快照");
            sb.AppendLine();
            sb.AppendLine("| 硬件 | 传感器 | 类型 | 值 |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (SensorReading r in readings)
            {
                sb.Append("| ").Append(Escape(r.Hardware))
                    .Append(" | ").Append(Escape(r.Name))
                    .Append(" | ").Append(Escape(r.SensorType))
                    .Append(" | ").Append(r.Value.ToString("0.##")).Append(' ').Append(r.Unit)
                    .AppendLine(" |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<OverviewItem> items)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        foreach (OverviewItem item in items)
        {
            if (item.Rows.Count > 0)
            {
                sb.AppendLine($"### {Escape(item.Label)}");
                sb.AppendLine();
                sb.AppendLine("| 项目 | 值 |");
                sb.AppendLine("| --- | --- |");
                foreach (OverviewRow row in item.Rows)
                {
                    sb.Append("| ").Append(Escape(row.Key))
                        .Append(" | ").Append(Escape(row.Value))
                        .AppendLine(" |");
                }
                sb.AppendLine();
            }
            else
            {
                // 统计卡：标题 + 大数字 + 副文案 + 进度条说明
                sb.Append("- ").Append(Escape(item.Label)).Append("：").Append(Escape(item.Value ?? "—"));
                if (!string.IsNullOrEmpty(item.Sub))
                {
                    sb.Append("（").Append(Escape(item.Sub!)).Append('）');
                }
                if (item.Percent is { } p)
                {
                    sb.Append(" · ").Append(Escape(item.PercentLabel ?? "占用")).Append(' ').Append(p).Append('%');
                }
                sb.AppendLine();
            }
        }
        sb.AppendLine();
    }

    /// <summary>Markdown 表格管道符转义，防止值里出现 | 破坏表格结构。</summary>
    private static string Escape(string? text) => (text ?? string.Empty).Replace("|", "\\|");
}
