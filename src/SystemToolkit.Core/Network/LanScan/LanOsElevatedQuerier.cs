using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using SystemToolkit.Core.Drivers;
using SystemToolkit.Core.Elevated;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>
/// <see cref="ILanOsVersionQuerier"/> 的提权实现：一批目标一次 UAC（classnames 同款「批量回传」模式）。
/// Helper 协议：<c>--out &lt;结果文件&gt; osver &lt;ipv4&gt;…</c>；参数校验双端同源
/// <see cref="LanOsVerRules"/>。🔴 用户拒绝 UAC（1223）→ Denied 安全终止、无副作用，
/// 调用方保留 TTL 推断值；Helper 缺失 → Unavailable。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LanOsElevatedQuerier : ILanOsVersionQuerier
{
    /// <summary>整批预算：单台 2.5s × 台数 + 15s 缓冲（UAC 弹窗等待也算进去，宁长勿断）。</summary>
    public static TimeSpan BatchBudget(int targetCount) =>
        TimeSpan.FromMilliseconds(LanOsWmi.PerTargetBudgetMs * (double)targetCount + 15_000);

    private readonly string _helperPath;
    private readonly Func<string> _tempDirectoryProvider;

    /// <summary>helper 路径与临时目录可注入（测试替换，同 ElevatingCommandRunner 纪律）。</summary>
    public LanOsElevatedQuerier(
        string? helperPath = null,
        Func<string>? tempDirectoryProvider = null)
    {
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "SystemToolkit.ElevatedHelper.exe");
        _tempDirectoryProvider = tempDirectoryProvider ?? Path.GetTempPath;
    }

    /// <inheritdoc/>
    public async Task<LanOsQueryOutcome> QueryAsync(IReadOnlyList<string> ipv4s, CancellationToken ct = default)
    {
        string[] targets = ipv4s.Where(LanOsVerRules.IsValidTargetIp).Take(LanOsVerRules.MaxTargets).ToArray();
        if (targets.Length == 0)
        {
            return new LanOsQueryOutcome(LanOsQueryStatus.Ok, new Dictionary<string, LanOsRemoteInfo>());
        }

        if (!File.Exists(_helperPath))
        {
            return new LanOsQueryOutcome(LanOsQueryStatus.Unavailable, new Dictionary<string, LanOsRemoteInfo>());
        }

        string outFile = Path.Combine(_tempDirectoryProvider(), $"stk_osver_{Guid.NewGuid():N}.out");
        var psi = new ProcessStartInfo(_helperPath)
        {
            UseShellExecute = true, // verb=runas 必须走 shell 启动通道（ElevatingCommandRunner 同款）
            Verb = "runas",
            Arguments = string.Join(' ', new[] { "--out", ElevatedPnpUtilClient.Quote(outFile), "osver" }
                .Concat(targets.Select(ElevatedPnpUtilClient.Quote))),
        };

        using Process proc = new() { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LanOsQueryOutcome(LanOsQueryStatus.Denied, new Dictionary<string, LanOsRemoteInfo>());
        }

        ElevatedRunResult run = await ElevatedProcessFinisher.FinishAsync(
            proc, outFile, BatchBudget(targets.Length), ct).ConfigureAwait(false);
        if (!run.Success)
        {
            return new LanOsQueryOutcome(LanOsQueryStatus.Unavailable, new Dictionary<string, LanOsRemoteInfo>());
        }

        return new LanOsQueryOutcome(
            LanOsQueryStatus.Ok,
            LanOsVerRules.ParseOutput(run.Output));
    }
}
