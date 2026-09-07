namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 严格 IPv4 文本校验（【审查修复】🟡2.3）。
/// <para>
/// <c>IPAddress.TryParse</c> 会接受旧式简写：<c>"1.2.3"</c> → 1.2.0.3、<c>"1.2"</c> → 1.0.0.2、
/// 十六进制混合 <c>"0x1.0x2"</c>——这些值能通过校验，随后 netsh 必然报「无效地址」，
/// 用户只能看到退出码无法定位。本类要求<b>恰好四段十进制数字</b>（每段 0–255），
/// 拒绝符号 / 空白 / 前导正号等宽松形态。设置页 VM 与 NetConfigService 服务层共用，
/// 保证「表单校验」与「服务防线」规则一致。
/// </para>
/// </summary>
public static class IpValidation
{
    private static readonly System.Globalization.NumberStyles SegmentStyle =
        System.Globalization.NumberStyles.None; // 禁符号、禁空白、禁千分位

    /// <summary>
    /// WinINET 代理服务器格式校验（【核实报告 N14】）：必须为 <c>host:port</c>，
    /// host 非空（允许域名/IP），port 为 1–65535 数字。此前只查非空，
    /// <c>abc</c> 会被原样写入注册表、浏览器静默不识别。
    /// </summary>
    public static bool IsProxyServer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1)
        {
            return false; // 无端口段 / 端口段为空
        }

        return trimmed[..colon].Trim().Length > 0
            && int.TryParse(trimmed[(colon + 1)..], out int port)
            && port is >= 1 and <= 65535;
    }

    /// <summary>严格 IPv4 校验：恰好四段十进制数字（每段 0–255），拒绝 <c>TryParse</c> 会接受的简写 / 十六进制等宽松形态。</summary>
    public static bool IsIPv4(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] segments = text.Trim().Split('.');
        return segments.Length == 4
            && segments.All(s =>
                s.Length > 0
                && s.Length <= 3
                && byte.TryParse(s, SegmentStyle, System.Globalization.CultureInfo.InvariantCulture, out _));
    }
}
