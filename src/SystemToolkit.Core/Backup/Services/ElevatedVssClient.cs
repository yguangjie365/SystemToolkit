using System.Diagnostics;
using System.Runtime.Versioning;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Drivers;
using SystemToolkit.Core.Elevated;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>VSS 卷影快照租约：持有 ShadowId，释放时经 Helper 删除快照（幂等）。</summary>
public sealed class VssLease : IAsyncDisposable
{
    /// <summary>卷影快照 ID（Guid）。</summary>
    public string ShadowId { get; }

    /// <summary>快照设备路径（\??\GLOBALROOT\Device\HarddiskVolumeShadowCopyN，尾无反斜杠）。</summary>
    public string DevicePath { get; }

    private readonly Func<VssLease, Task> _deleteAsync;

    /// <summary>内部构造（由 <see cref="ElevatedVssClient"/> 创建）。</summary>
    public VssLease(string shadowId, string devicePath, Func<VssLease, Task> deleteAsync)
    {
        ShadowId = shadowId;
        DevicePath = devicePath;
        _deleteAsync = deleteAsync;
    }

    /// <summary>删除卷影快照（释放租约；失败仅由调用方日志侧感知，不阻断备份收尾）。</summary>
    public ValueTask DisposeAsync()
        => new(_deleteAsync(this));
}

/// <summary>
/// VSS 卷影客户端（批次二）：经 Elevated Helper（verb=vss）以管理员权限创建/删除卷影快照。
/// 与 helper 的协议：`--out &lt;file&gt; vss create &lt;卷根&gt;` 成功后 out 文件单行
/// `ShadowId|DevicePath`；`vss delete &lt;ShadowId&gt;` 幂等删除。
/// UAC 被拒绝（1223）、helper 缺失、超时 → 返回 null 并由 onLog 留痕，调用方回退普通备份。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedVssClient
{
    /// <summary>快照创建/删除的单次超时（VSS 常规秒级，预留网络盘/慢盘余量）。</summary>
    private static readonly TimeSpan VssTimeout = TimeSpan.FromMinutes(3);

    private readonly string _helperPath;
    private readonly Func<string> _tempDirectoryProvider;
    private readonly ILogger _logger;

    /// <summary>构造；helperPath 缺省应用目录下的 SystemToolkit.ElevatedHelper.exe。</summary>
    public ElevatedVssClient(string? helperPath = null, Func<string>? tempDirectoryProvider = null, ILogger? logger = null)
    {
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "SystemToolkit.ElevatedHelper.exe");
        _tempDirectoryProvider = tempDirectoryProvider ?? Path.GetTempPath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 为指定卷根（如 <c>C:\</c>）创建卷影快照。成功返回租约（含设备路径）；
    /// UAC 拒绝 / helper 缺失 / 超时 / VSS 失败 → null（调用方回退普通备份）。
    /// </summary>
    public async Task<VssLease?> CreateAsync(string volumeRoot, Action<string>? onLog = null, CancellationToken ct = default)
    {
        (int exit, string? content) = await RunVssAsync(["create", volumeRoot], onLog, ct).ConfigureAwait(false);
        if (exit != 0 || content is null)
        {
            return null;
        }

        string[] parts = content.Split('|', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out Guid shadowId))
        {
            onLog?.Invoke($"[VSS] ❌ helper 输出格式异常：{content}");
            return null;
        }

        var lease = new VssLease(
            parts[0].ToUpperInvariant(), parts[1],
            lease => DeleteAsync(lease.ShadowId, onLog, CancellationToken.None));
        _logger.Info($"VSS 快照已创建：{parts[1]}（{lease.ShadowId}）");
        return lease;
    }

    /// <summary>删除卷影快照（幂等；失败仅记日志）。</summary>
    public async Task DeleteAsync(string shadowId, Action<string>? onLog = null, CancellationToken ct = default)
    {
        await RunVssAsync(["delete", shadowId], onLog, ct).ConfigureAwait(false);
    }

    /// <summary>执行一次 helper vss 调用；返回退出码与 out 文件内容（首行）。</summary>
    private async Task<(int ExitCode, string? Content)> RunVssAsync(
        string[] verbArgs, Action<string>? onLog, CancellationToken ct)
    {
        string? content = null;
        if (!File.Exists(_helperPath))
        {
            onLog?.Invoke("[VSS] ❌ 提权辅助进程缺失，回退普通备份");
            return (-1, null);
        }

        string outFile = Path.Combine(_tempDirectoryProvider(), $"stk_vss_{Guid.NewGuid():N}.out");
        var psi = new ProcessStartInfo(_helperPath)
        {
            UseShellExecute = true, // verb=runas 必须走 shell 启动通道
            Verb = "runas",
            Arguments = string.Join(' ', new[] { "--out", ElevatedPnpUtilClient.Quote(outFile), "vss" }
                .Concat(verbArgs.Select(ElevatedPnpUtilClient.Quote))),
        };

        using Process proc = new() { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            onLog?.Invoke("[VSS] ❌ 用户拒绝了 UAC 提权请求，回退普通备份");
            return (1223, null);
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"[VSS] ❌ 无法启动提权辅助进程，回退普通备份：{ex.Message}");
            return (-1, null);
        }

        ElevatedRunResult result = await ElevatedProcessFinisher
            .FinishAsync(proc, outFile, VssTimeout, ct).ConfigureAwait(false);
        OutputDecoder.ForEachLine(result.Output, line => onLog?.Invoke($"[VSS] {line}"));
        // 审查 S-2：create 快照 ID 已由 helper 持久化到 outFile.sid sidecar；
        // helper 异常退出（超时/被强杀/崩溃）时据此补删可能遗留的卷影快照，避免孤儿卷影累积占盘。
        if (verbArgs.Length > 0 && verbArgs[0] == "create")
        {
            string sentinel = outFile + ".sid";
            if (result.ExitCode != 0 && File.Exists(sentinel))
            {
                string? orphan = null;
                try
                {
                    orphan = (await File.ReadAllTextAsync(sentinel).ConfigureAwait(false)).Trim();
                }
                catch
                {
                    // sidecar 读取失败仅视为无孤儿可清（系统边界，捕获后继续）
                }
                if (Guid.TryParse(orphan, out _))
                {
                    onLog?.Invoke($"[VSS] ⚠️ 检测到可能遗留的卷影快照 {orphan}，尝试清理（若再次弹出 UAC 请允许）");
                    await DeleteAsync(orphan!, onLog, CancellationToken.None).ConfigureAwait(false);
                }
            }
            try
            {
                File.Delete(sentinel);
            }
            catch
            {
                // 清理尽力而为
            }
        }


        if (result.ExitCode != 0)
        {
            return (result.ExitCode, null);
        }

        content = result.Output.Trim().Split('\n', 2)[0].Trim();
        return (0, content);
    }
}
