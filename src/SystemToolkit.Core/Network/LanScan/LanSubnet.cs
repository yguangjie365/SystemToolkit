using System.Net;
using System.Net.Sockets;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>子网扫描计划：主机 IP 清单 + 展示用网段标签。</summary>
/// <param name="LocalIp">本机在该网段的 IPv4。</param>
/// <param name="NetworkLabel">如 <c>192.168.1.0/24</c>（UI 芯片展示）。</param>
/// <param name="Hosts">待探测主机 IP（已排除网络号与广播地址，升序）。</param>
/// <param name="Truncated">网段规模超上限被截断（大网段防呆，UI 需黄标提示）。</param>
public sealed record LanSubnetPlan(
    string LocalIp,
    string NetworkLabel,
    IReadOnlyList<string> Hosts,
    bool Truncated);

/// <summary>
/// 子网枚举纯函数：「ip/prefix」（<see cref="SystemToolkit.Core.Network.Models.NetAdapterInfo.IPv4WithMask"/>
/// 的既有格式）→ 主机 IP 列表。零系统调用，可测性优先。
/// </summary>
public static class LanSubnet
{
    /// <summary>V1 默认单轮扫描上限：/24 恰好全覆盖；/16 只扫前 1024（Truncated 显式标注，不做静默假设）。</summary>
    public const int DefaultHostLimit = 1024;

    /// <summary>从适配器条目 <c>"192.168.1.5/24"</c> 构建扫描计划；畸形输入返回 false（UI 走失败态）。</summary>
    public static bool TryBuildFromAdapterEntry(string ipWithPrefix, int limit, out LanSubnetPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(ipWithPrefix))
        {
            return false;
        }

        string[] parts = ipWithPrefix.Split('/', 2);
        int prefix = parts.Length == 2 && int.TryParse(parts[1], out int p) ? p : 32;
        return TryBuild(parts[0].Trim(), prefix, limit, out plan);
    }

    /// <summary>
    /// 按「IP + 前缀长度」枚举主机地址。约定：
    /// /31、/32 视为单地址（点对点兜底，至少扫本机自身 IP 所在的这一两个地址）；
    /// 其余排除网络号与广播；超过 <paramref name="limit"/> 截断并置 Truncated。
    /// </summary>
    public static bool TryBuild(string ip, int prefixLength, int limit, out LanSubnetPlan? plan)
    {
        plan = null;
        if (limit <= 0 || prefixLength is < 0 or > 32
            || !IPAddress.TryParse(ip, out IPAddress? addr)
            || addr.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        uint ipNum = ToUInt(addr.GetAddressBytes());
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        uint network = ipNum & mask;
        uint broadcast = network | ~mask;

        List<string> hosts = [];
        bool truncated = false;
        if (broadcast - network <= 1) // /31、/32：无主机位，直接给地址本身
        {
            hosts.Add(Format(ipNum));
        }
        else
        {
            for (uint host = network + 1; host < broadcast; host++)
            {
                if (hosts.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                hosts.Add(Format(host));
            }
        }

        plan = new LanSubnetPlan(
            Format(ipNum),
            $"{Format(network)}/{prefixLength}",
            hosts,
            truncated);
        return true;
    }

    // 点分字节序即大端数值：bytes[0] 为最高字节
    private static uint ToUInt(byte[] bytes) =>
        (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

    private static string Format(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
}
