using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <c>netsh interface tcp show global</c> 输出解析器（纯函数，重点单测对象）。
/// <para>
/// 【本地化】netsh 输出随系统语言变化——中英文标签各一套正则匹配，值统一小写归一；
/// <b>缺行 / 乱码 / 不认识的标签一律降级为 null（未知）</b>，绝不猜值。
/// 匹配按「整行小写后包含标签」进行，容忍中英文标签两侧的空白宽度差异。
/// </para>
/// </summary>
public static class TcpSettingsParser
{
    /// <summary>自动调谐级别的候选标签（zh / en 各版本措辞不一，宽匹配）。</summary>
    private static readonly string[] AutoTuningLabels =
    {
        "接收窗口自动调节级别",
        "窗口自动调节级别",
        "receive window auto-tuning level",
        "auto-tuning level",
        "autotuning level",
    };

    private static readonly string[] RssLabels =
    {
        "接收方缩放状态",
        "receive-side scaling state",
        "receive side scaling state",
    };

    private static readonly string[] EcnLabels =
    {
        "ecn 功能",        // Win11 24H2 zh-CN 实测措辞（真机 2026-08-31）
		"ecn 能力",        // Win10 zh-CN
		"ecn capability",
    };

    private static readonly string[] InitialRtoLabels =
    {
        "初始 rto",
        "initial rto",
    };

    private static readonly string[] CongestionProviderLabels =
    {
        "拥塞控制提供程序",
        "congestion control provider",
    };

    private static readonly string[] RscLabels =
    {
        "接收段合并状态",
        "rsc(接收段合并状态)",
        "receive segment coalescing",
    };

    private static readonly string[] Rfc1323Labels =
    {
        "rfc 1323 时间戳",
        "rfc 1323 timestamps",
    };

    /// <summary>逐行解析 show global 输出：按「标签: 值」宽匹配各字段，缺行 / 乱码降级 null；NetworkThrottlingIndex 不在该输出内，恒为 null（由注册表读取补充）。</summary>
    public static TcpGlobalSettings Parse(IEnumerable<string> lines)
    {
        string? autoTuning = null;
        bool? rss = null;
        bool? ecn = null;
        string? initialRto = null;
        string? congestionProvider = null;
        string? rsc = null;
        string? rfc1323 = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue; // 标题行 / 分隔线 / 空行 / 乱码行
            }

            string label = line[..separator].Trim().ToLowerInvariant();
            string value = line[(separator + 1)..].Trim().ToLowerInvariant();

            if (autoTuning is null && Matches(label, AutoTuningLabels))
            {
                autoTuning = value.Length == 0 ? null : value; // normal / disabled / highlyrestricted / experimental …
            }
            else if (rss is null && Matches(label, RssLabels))
            {
                rss = ParseSwitch(value);
            }
            else if (ecn is null && Matches(label, EcnLabels))
            {
                ecn = ParseSwitch(value);
            }
            else if (initialRto is null && Matches(label, InitialRtoLabels))
            {
                initialRto = value.Length == 0 ? null : value; // 原值展示（数字），不布尔化
            }
            else if (congestionProvider is null && Matches(label, CongestionProviderLabels))
            {
                congestionProvider = value.Length == 0 ? null : value; // default / cubic …
            }
            else if (rsc is null && Matches(label, RscLabels))
            {
                rsc = ParseSwitch(value) switch
                {
                    true => "enabled",
                    false => "disabled",
                    _ => value.Length == 0 ? null : value, // 未知取值原样展示
                };
            }
            else if (rfc1323 is null && Matches(label, Rfc1323Labels))
            {
                rfc1323 = value.Length == 0 ? null : value; // allowed / disabled …原值
            }
        }

        return new TcpGlobalSettings(autoTuning, rss, ecn, NetworkThrottlingIndex: null,
            InitialRto: initialRto, CongestionProvider: congestionProvider,
            RscState: rsc, Rfc1323Timestamps: rfc1323);
    }

    private static bool Matches(string label, string[] labels)
        => labels.Any(l => label.Contains(l, StringComparison.Ordinal));

    /// <summary>enabled / disabled → bool；其它值（含乱码）一律 null，不猜。</summary>
    private static bool? ParseSwitch(string value) => value switch
    {
        "enabled" => true,
        "disabled" => false,
        "enable" => true,
        "disable" => false,
        _ => null,
    };
}
