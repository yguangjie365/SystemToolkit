using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Core.Network.SplitRoute;

/// <summary>
/// 分流选中的网卡（外网口 / 内网口各一）：<see cref="IfIndex"/> 由
/// <c>netsh interface ipv4 show interfaces</c> 解析所得（路由表按 idx 匹配，名称含空格不做列匹配）。
/// </summary>
/// <param name="Name">接口显示名（netsh <c>interface="…"</c> 参数）。</param>
/// <param name="IfIndex">接口索引。</param>
/// <param name="Gateway">该网卡当前网关（分流前提：必须有）。</param>
public sealed record SplitNicSelection(string Name, int IfIndex, string Gateway)
{
    /// <summary>下拉/预览展示文本。</summary>
    public string Display => $"{Name}（if {IfIndex} · gw {Gateway}）";
}

/// <summary>一次分流请求（V1 单对：A 外网 + B 内网——设计经用户拍板 2026-09-12）。</summary>
/// <param name="Wan">外网出口网卡（持有唯一默认路由）。</param>
/// <param name="Lan">内网出口网卡（默认路由被移除，按 CIDR 逐段指路）。</param>
/// <param name="Cidrs">内网网段清单（CIDR 记法，默认 RFC1918 三段）。</param>
public sealed record SplitRequest(SplitNicSelection Wan, SplitNicSelection Lan, IReadOnlyList<string> Cidrs)
{
    /// <summary>外网默认路由跃点（低于一切内网段路由，保证兜底出口）。</summary>
    public int WanDefaultMetric { get; init; } = 10;

    /// <summary>内网段路由跃点。</summary>
    public int LanRouteMetric { get; init; } = 25;

    /// <summary>内网卡接口级跃点（调高压过外网卡，防系统把回程/默认走内网口）。原值记入台账用于恢复。</summary>
    public int LanInterfaceMetric { get; init; } = 35;
}

/// <summary>预览/执行的操作种类。</summary>
public enum SplitOpKind
{
    /// <summary>删除一条路由。</summary>
    DeleteRoute = 0,

    /// <summary>新增一条路由。</summary>
    AddRoute = 1,

    /// <summary>设置接口跃点。</summary>
    SetInterfaceMetric = 2,
}

/// <summary>变更清单里的一行（预览所见即执行所得）。</summary>
/// <param name="Kind">操作种类。</param>
/// <param name="Description">人读描述（预览表直显）。</param>
/// <param name="FileName">命令可执行名（netsh / ipconfig）。</param>
/// <param name="Arguments">参数整串（经 <see cref="NetshArgs"/> 构造，白名单双端同源校验）。</param>
public sealed record SplitOp(SplitOpKind Kind, string Description, string FileName, string Arguments);

/// <summary>台账里的一条自建路由（恢复/守护只按这些四要素精确删除，绝不误伤既有路由——参考项目纪律）。</summary>
/// <param name="Prefix">目标网段 CIDR（如 <c>10.0.0.0/8</c>）。</param>
/// <param name="Nexthop">网关。</param>
/// <param name="InterfaceName">接口名。</param>
/// <param name="IfIndex">接口索引。</param>
/// <param name="IsDefaultRoute">是否为外网默认路由（0.0.0.0/0）。</param>
public sealed record SplitLedgerRoute(
    string Prefix, string Nexthop, string InterfaceName, int IfIndex, bool IsDefaultRoute);

/// <summary>分流台账（持久化形态）：应用时刻、两口快照、内网卡原接口跃点、自建路由清单。</summary>
public sealed record SplitLedger(
    int Version,
    DateTimeOffset AppliedAt,
    SplitNicSelection Wan,
    SplitNicSelection Lan,
    int LanOriginalInterfaceMetric,
    IReadOnlyList<SplitLedgerRoute> Routes);

/// <summary>应用后 / 重验报告（三判据：默认路由唯一走外网口、外连通、内连通）。</summary>
public sealed record SplitVerifyReport(
    bool DefaultRouteUniqueOnWan,
    string DefaultRoutesSummary,
    bool WanReachable,
    int? WanAvgMs,
    bool LanReachable,
    int? LanAvgMs,
    IReadOnlyList<string> Problems)
{
    /// <summary>三判据全过（应用成功/可保持态的唯一标准）。</summary>
    public bool AllPassed => DefaultRouteUniqueOnWan && WanReachable && LanReachable && Problems.Count == 0;
}

/// <summary>应用结果。<see cref="RolledBack"/>=true 表示验证失败已自动回滚到应用前状态。</summary>
public sealed record SplitApplyOutcome(
    bool Success, bool RolledBack, string Message, SplitVerifyReport? Verification = null);

/// <summary>CIDR 纯函数工具：用户输入闸门 + netsh 参数规范化。</summary>
public static class SplitCidr
{
    /// <summary>V1 默认内网段（RFC1918 三段——用户拍板保留为可删默认值）。</summary>
    public static readonly IReadOnlyList<string> DefaultPrivateCidrs =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    /// <summary>校验并规范化（去空白、前缀 0..32、八位组 ≤255）；非法返回 null。防注入的唯一入口。</summary>
    public static string? Normalize(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return null;
        }

        string[] parts = cidr.Trim().Split('/', 2);
        if (parts.Length != 2 || !IpValidation.IsIPv4(parts[0])
            || !int.TryParse(parts[1], out int len) || len is < 0 or > 32)
        {
            return null;
        }

        return $"{parts[0]}/{len}";
    }

    /// <summary>是否默认路由记法。</summary>
    public static bool IsDefault(string normalizedCidr) => normalizedCidr == "0.0.0.0/0";
}
