using System.Net;
using System.Net.Sockets;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>远程精确 OS 查询结果（一台目标一行）。</summary>
/// <param name="Caption">Win32_OperatingSystem.Caption（随目标系统本地化，如「Microsoft Windows 10 专业版」）。</param>
/// <param name="Version">Win32_OperatingSystem.Version（如 <c>10.0.19045</c>）。</param>
public sealed record LanOsRemoteInfo(string Caption, string Version)
{
    /// <summary>UI 直显格式：Windows 11 Pro · 10.0.22631。</summary>
    public string Display => string.IsNullOrWhiteSpace(Version) ? Caption : $"{Caption} · {Version}";
}

/// <summary>批量查询出口状态：拒绝 UAC 与失败必须可区分（前者静默降级为推断值，后者记日志）。</summary>
public enum LanOsQueryStatus
{
    /// <summary>查询完成（单机无权限仅是结果缺项，不算失败）。</summary>
    Ok = 0,

    /// <summary>用户拒绝 UAC：调用方保留 TTL 推断值，无副作用。</summary>
    Denied = 1,

    /// <summary>Helper 缺失/整批失败：同样降级推断值并留痕。</summary>
    Unavailable = 2,
}

/// <summary>一次批量精确查询的返回。</summary>
/// <param name="State">出口状态。</param>
/// <param name="Results">命中 IP → OS 信息（Denied/Unavailable 时空表）。</param>
public sealed record LanOsQueryOutcome(LanOsQueryStatus State, IReadOnlyDictionary<string, LanOsRemoteInfo> Results);

/// <summary>
/// 精确 OS 查询器接缝（NET-6 增强，用户 2026-09-12 批准提权路线）：
/// 对候选 Windows 设备批量远程 WMI（<c>Win32_OperatingSystem</c>）读真实 Caption+版本号。
/// 实现在提权 Helper 进程执行（verb <c>osver</c>，一次 UAC 覆盖整批）——只有以带管理员
/// SID 的令牌认证，对目标机有管理权的账号才能读回；无权限/非 Windows 目标逐台空值降级。
/// </summary>
public interface ILanOsVersionQuerier
{
    /// <summary>批量查询（≤ <see cref="LanOsVerRules.MaxTargets"/> 台；永不抛——异常一律转状态码）。</summary>
    Task<LanOsQueryOutcome> QueryAsync(IReadOnlyList<string> ipv4s, CancellationToken ct = default);
}

/// <summary>
/// osver 动词的双端同源规则（Core 客户端与 ElevatedHelper 都走这里，杜绝两份规则漂移——
/// NetshTokenRules 同款纪律）。
/// </summary>
public static class LanOsVerRules
{
    /// <summary>单批目标上限（再多没意义：/24 网段的 Windows 机通常远少于这个数；也封死提权面滥用）。</summary>
    public const int MaxTargets = 64;

    /// <summary>严格 IPv4 点分十进制（拒绝 CIDR/端口/空白/IPv6——helper 参数面的唯一闸门）。</summary>
    public static bool IsValidTargetIp(string token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Trim() == token
        && IPAddress.TryParse(token, out IPAddress? addr)
        && addr.AddressFamily == AddressFamily.InterNetwork
        && !token.Contains('/')
        && !token.Contains(':');

    /// <summary>
    /// 解析 Helper 结果文件：每行 <c>ip=Caption|Version</c>（查询失败/无权限写 <c>ip=</c>，跳过）。
    /// 容忍 CR/空行/无等号脏行（防御式，不抛）。
    /// </summary>
    public static Dictionary<string, LanOsRemoteInfo> ParseOutput(string? output)
    {
        var results = new Dictionary<string, LanOsRemoteInfo>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(output))
        {
            return results;
        }

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0 || !IsValidTargetIp(line[..eq]))
            {
                continue;
            }

            string payload = line[(eq + 1)..];
            if (payload.Length == 0)
            {
                continue; // 该目标无权限/非 Windows——维持调用方降级值
            }

            int bar = payload.IndexOf('|');
            string caption = bar < 0 ? payload : payload[..bar];
            string version = bar < 0 ? "" : payload[(bar + 1)..];
            if (caption.Length > 0)
            {
                results[line[..eq]] = new LanOsRemoteInfo(caption, version);
            }
        }

        return results;
    }

    /// <summary>拼 Helper 结果行（Helper 端与单测共用同一序列化形状）。</summary>
    public static string FormatResultLine(string ipv4, LanOsRemoteInfo? info) =>
        info is null ? $"{ipv4}=" : $"{ipv4}={info.Caption}|{info.Version}";
}
