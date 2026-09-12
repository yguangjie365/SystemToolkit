using System.Management;
using System.Runtime.Versioning;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// 远程 WMI 读取 <c>Win32_OperatingSystem.Caption/Version</c>（真实 OS 名与版本号）。
/// <para>
/// 🔴 只读、认证走当前进程令牌——设计意图是<b>在 ElevatedHelper（runas 后的完整管理员令牌）
/// 内调用</b>：域管/目标机管理员账号此时才拿得到数据；无权限目标按台降级 null。
/// 每目标独立预算超时（DCOM 连不通防火墙 DROP 形态可能长时间挂起），孤儿任务显式吞异常、
/// 随 Helper 进程退出自然收尾。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class LanOsWmi
{
    /// <summary>单台查询预算（毫秒）——Helper 端与服务端超时同值，改一处须同步改注释。</summary>
    public const int PerTargetBudgetMs = 2500;

    /// <summary>单台查询（带预算超时；一切失败形态返回 null 降级，永不抛）。</summary>
    public static async Task<LanOsRemoteInfo?> TryQueryAsync(string ipv4, CancellationToken ct = default)
    {
        if (!LanOsVerRules.IsValidTargetIp(ipv4))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        Task<LanOsRemoteInfo?> query = Task.Run(() => Query(ipv4));
        _ = query.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        Task winner = await Task.WhenAny(query, Task.Delay(PerTargetBudgetMs, CancellationToken.None))
            .ConfigureAwait(false);
        if (winner != query)
        {
            return null; // 超时：等它自己 internally fail
        }

        try
        {
            return await query.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // 无权限/非 Windows/防火墙拒——静默降级为推断值
        }
    }

    private static LanOsRemoteInfo? Query(string ipv4)
    {
        var scope = new ManagementScope($@"\\{ipv4}\root\cimv2");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(
            scope, new ObjectQuery("SELECT Caption, Version FROM Win32_OperatingSystem"));
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject row in results)
        {
            using ManagementBaseObject disposable = row;
            string? caption = disposable["Caption"]?.ToString()?.Trim();
            string? version = disposable["Version"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(caption))
            {
                return new LanOsRemoteInfo(caption, version ?? "");
            }
        }

        return null;
    }
}
