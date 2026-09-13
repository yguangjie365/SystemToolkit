namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 监听端口校验（2026-09-13 批次 P3）：两处 UI（电脑互传的 TCP/UDP、手机通道的 Web/HTTPS）共用同一判据。
/// <para>
/// 为什么提出来：端口规则（范围、组内唯一）若各页各写一遍，两处迟早会给出不同结论——
/// 用户看到"这里说合法、那里说不合法"就再也不会信任任何一处校验。
/// </para>
/// </summary>
public static class PortValidator
{
    /// <summary>可用端口下限：1024 以下属系统保留段，普通应用不应占用。</summary>
    public const int MinPort = 1024;

    /// <summary>可用端口上限。</summary>
    public const int MaxPort = 65535;

    /// <summary>端口是否在可用范围（含边界）。</summary>
    public static bool IsInRange(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>
    /// 校验一组端口：先逐个查范围，再查组内是否重复。
    /// </summary>
    /// <param name="ports">(界面标签, 端口值) 序列，标签用于把问题指到具体那一项。</param>
    /// <returns>中文错误文案；<c>null</c> = 全部通过。</returns>
    public static string? Validate(params (string Label, int Port)[] ports)
    {
        var seen = new Dictionary<int, string>();
        foreach ((string label, int port) in ports)
        {
            if (!IsInRange(port))
            {
                return $"{label}需在 {MinPort}–{MaxPort} 之间（当前 {port}）";
            }

            if (seen.TryGetValue(port, out string? owner))
            {
                return $"{label}与{owner}不能相同（都是 {port}）";
            }

            seen[port] = label;
        }

        return null;
    }
}
