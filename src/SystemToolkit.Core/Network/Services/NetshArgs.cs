namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// netsh / ipconfig 参数构造的<b>唯一入口</b>：适配器名一律加引号
/// （先例：<c>WingetService.BuildIdArgs</c> 的参数构造回归测试）。
/// <para>
/// 适配器名来自系统枚举而非用户输入，注入面可控；仍统一加引号并由
/// <c>NetshArgsTests</c> 逐字回归（含空格 / 中文名）。
/// 因直连进程执行（不经 cmd），无需处理 cmd 元字符转义。
/// </para>
/// <para>
/// 返回的串既是执行参数，也是「复制命令」展示文本的组成部分——执行与展示共用同一构造，杜绝两处漂移。
/// </para>
/// </summary>
public static class NetshArgs
{
    /// <summary>
    /// 适配器名参数：<c>name="以太网"</c>。引号是 netsh 解析器的值定界符，含空格/中文必需。
    /// 【审查修复】适配器名<b>含引号字符时抛异常</b>——引号会打破 <c>name="…"</c> 的
    /// 定界边界，轻则 netsh 报错，重则操作到非预期接口。接口名来自系统枚举，正常不含
    /// 引号；出现即视为环境异常，宁可不操作（调用方 NetConfigService 会转成降级返回）。
    /// </summary>
    public static string Name(string adapter)
        => $"name=\"{ValidateAdapterName(adapter)}\"";

    /// <summary>适配器名卫生检查（Name 与 Renew 共用——【并行审查核实】Renew 此前手拼引号绕过了防线）。</summary>
    private static string ValidateAdapterName(string adapter)
    {
        if (adapter.Contains('"'))
        {
            throw new ArgumentException($"适配器名含非法引号字符，拒绝构造命令：「{adapter}」", nameof(adapter));
        }

        return adapter;
    }

    /// <summary>恢复 DHCP 自动获取 IP。</summary>
    public static string SetDhcp(string adapter)
        => $"interface ipv4 set address {Name(adapter)} source=dhcp";

    /// <summary>配置静态 IP。网关为 null 时省略 gateway 段（netsh 允许无网关的静态配置）。</summary>
    public static string SetStaticIp(string adapter, string ip, string mask, string? gateway)
        => gateway is null
            ? $"interface ipv4 set address {Name(adapter)} source=static address={ip} mask={mask}"
            : $"interface ipv4 set address {Name(adapter)} source=static address={ip} mask={mask} gateway={gateway}";

    /// <summary>设置接口跃点数（M6c P1-6：多网卡路由优先级，数值越小优先级越高）。</summary>
    public static string SetInterfaceMetric(string adapter, int metric)
        => $"interface ipv4 set interface {Name(adapter)} metric={metric}";

    /// <summary>DNS 恢复自动（DHCP）。</summary>
    public static string SetDnsToDhcp(string adapter)
        => $"interface ipv4 set dnsservers {Name(adapter)} source=dhcp";

    /// <summary>设置首选 DNS。</summary>
    public static string SetDnsPrimary(string adapter, string primary)
        => $"interface ipv4 set dnsservers {Name(adapter)} source=static address={primary} register=primary";

    /// <summary>追加备用 DNS（固定 index=2）。</summary>
    public static string AddDnsSecondary(string adapter, string secondary)
        => $"interface ipv4 add dnsservers {Name(adapter)} address={secondary} index=2";

    /// <summary>启用 / 禁用适配器（软开关，等价设备管理器停用）。</summary>
    public static string SetAdapterEnabled(string adapter, bool enabled)
        => $"interface set interface {Name(adapter)} admin={(enabled ? "enable" : "disable")}";

    // ── 修复类命令（M3）：两条 ipconfig 走 ipconfig.exe，其余走 netsh ──

    /// <summary>刷新 DNS 缓存（ipconfig /flushdns）。</summary>
    public static string FlushDns => "/flushdns";

    /// <summary>续订指定适配器的 DHCP 租约（ipconfig /renew "名称"）。走 Name 同源的引号卫生检查。</summary>
    public static string Renew(string adapter) => $"/renew \"{ValidateAdapterName(adapter)}\"";

    /// <summary>重置 Winsock 目录（需重启）。</summary>
    public static string WinsockReset => "winsock reset";

    /// <summary>重置 TCP/IP 协议栈（需重启）。</summary>
    public static string IpReset => "int ip reset";
}
