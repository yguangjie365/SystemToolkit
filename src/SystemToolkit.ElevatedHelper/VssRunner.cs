using System.Text;
using Alphaleonis.Win32.Vss;

namespace SystemToolkit.ElevatedHelper;

/// <summary>
/// 子命令 vss：经 AlphaVSS 操作卷影复制服务（VSS），创建/删除备份用途卷影快照。
/// 协议（由 <see cref="Program"/> 分派，参数结构：--out &lt;结果文件&gt; vss &lt;create|delete&gt; …）：
///   vss create &lt;卷根路径&gt; —— 对指定卷创建 VSS 快照（Backup 上下文），成功向结果文件（UTF-8）
///   写单行 <c>ShadowId|DevicePath</c>（ShadowId 为大写 D 格式 Guid；DevicePath 形如
///   <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</c>），退出码 0。
///   vss delete &lt;ShadowId&gt; —— 删除对应快照，成功写单行 <c>deleted &lt;ShadowId&gt;</c>，退出码 0；
///   快照不存在视为成功（幂等），写 <c>deleted (absent)</c>。
///   失败时错误消息写入结果文件，退出码 1（helper 端禁止裸崩）。
/// 🔴 生命周期约定：创建成功的快照不在此删除，由调用方（主进程）用完后显式调 delete。
/// </summary>
public static class VssRunner
{
    /// <summary>VSS_E_OBJECT_NOT_FOUND（0x80042308）：目标快照不存在。</summary>
    private const int VssObjectNotFoundHResult = unchecked((int)0x80042308);

    /// <summary>
    /// 创建指定卷的备份用途卷影快照（备份成功与否不影响快照生命周期，删除归调用方）。
    /// </summary>
    /// <param name="args">仅一个参数：卷根路径（如 <c>C:\</c>，亦可 <c>C:</c>，内部归一化为 <c>C:\</c>）。</param>
    /// <param name="outFile">结果文件路径（UTF-8，成功写单行 <c>ShadowId|DevicePath</c>）。</param>
    /// <param name="cancellationToken">取消令牌（传递给 VSS 异步等待与结果写入）。</param>
    /// <returns>0 = 成功；1 = 失败（错误消息已写入结果文件）。</returns>
    public static async Task<int> CreateAsync(string[] args, string outFile, CancellationToken cancellationToken = default)
    {
        if (args.Length != 1)
        {
            return await WriteErrorAsync(outFile, "vss create 需要 <卷根路径> 参数", cancellationToken).ConfigureAwait(false);
        }

        string? volume = NormalizeVolumeRoot(args[0]);
        if (volume is null)
        {
            return await WriteErrorAsync(outFile, $"非法的卷根路径：{args[0]}（应为盘符根，如 C:\\）", cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(volume))
        {
            return await WriteErrorAsync(outFile, $"卷根路径不存在：{volume}", cancellationToken).ConfigureAwait(false);
        }

        Guid? snapshotId = null;
        string sentinel = outFile + ".sid";
        try
        {
            // AlphaVSS 2.0.3 调用链（逐方法经包内 netcoreapp3.1 程序集反射核实）：
            // VssFactoryProvider.Default.GetVssFactory() → CreateVssBackupComponents()
            //   → InitializeForBackup(null) → SetContext(Backup) → SetBackupState
            //   → StartSnapshotSet → AddToSnapshotSet(卷根)
            //   → PrepareForBackupAsync → DoSnapshotSetAsync → GetSnapshotProperties(id).SnapshotDeviceObject
            using IVssBackupComponents backup = VssFactoryProvider.Default.GetVssFactory().CreateVssBackupComponents();
            backup.InitializeForBackup(null!); // null = 全新备份会话（VSS 约定，非导入先前状态 XML）
            backup.SetContext(VssSnapshotContext.Backup);
            backup.SetBackupState(false, false, VssBackupType.Full, false);
            backup.StartSnapshotSet(); // 快照集 ID 不需要；单卷快照 ID 由 AddToSnapshotSet 返回
            snapshotId = backup.AddToSnapshotSet(volume);
            // 快照 ID 一经产生立即持久化到 sidecar（不带取消令牌），供主进程在超时/被强杀后仍能读到并清理孤儿卷影
            await File.WriteAllTextAsync(sentinel, snapshotId.Value.ToString("D").ToUpperInvariant(), Encoding.UTF8).ConfigureAwait(false);
            await backup.PrepareForBackupAsync(cancellationToken).ConfigureAwait(false);
            await backup.DoSnapshotSetAsync(cancellationToken).ConfigureAwait(false);
            VssSnapshotProperties properties = backup.GetSnapshotProperties(snapshotId.Value);
            string line = $"{snapshotId.Value.ToString("D").ToUpperInvariant()}|{properties.SnapshotDeviceObject}";
            await File.WriteAllTextAsync(outFile, line + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            TryDeleteFile(outFile + ".sid");
            return 0;
        }
        catch (Exception ex)
        {
            if (snapshotId is { } sid)
            {
                TryDeleteSnapshot(sid);
                TryDeleteFile(sentinel);
            }

            return await WriteErrorAsync(outFile, $"VSS 快照创建失败：{volume}：{ex.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 删除指定 ShadowId 的卷影快照（幂等：快照不存在亦视为成功，写 <c>deleted (absent)</c>）。
    /// </summary>
    /// <param name="args">仅一个参数：快照标识（Guid 文本，接受常见 Guid 形式，内部归一化输出）。</param>
    /// <param name="outFile">结果文件路径（UTF-8，成功写单行 <c>deleted &lt;ShadowId&gt;</c> 或 <c>deleted (absent)</c>）。</param>
    /// <param name="cancellationToken">取消令牌（传递给结果写入）。</param>
    /// <returns>0 = 成功（含快照不存在）；1 = 失败（错误消息已写入结果文件）。</returns>
    public static async Task<int> DeleteAsync(string[] args, string outFile, CancellationToken cancellationToken = default)
    {
        if (args.Length != 1)
        {
            return await WriteErrorAsync(outFile, "vss delete 需要 <ShadowId> 参数", cancellationToken).ConfigureAwait(false);
        }

        if (!Guid.TryParse(args[0].Trim(), out Guid snapshotId))
        {
            return await WriteErrorAsync(outFile, $"非法的 ShadowId：{args[0]}", cancellationToken).ConfigureAwait(false);
        }

        string normalizedId = snapshotId.ToString("D").ToUpperInvariant();
        try
        {
            using IVssBackupComponents backup = VssFactoryProvider.Default.GetVssFactory().CreateVssBackupComponents();
            backup.InitializeForBackup(null!);
            try
            {
                backup.DeleteSnapshot(snapshotId, forceDelete: true);
            }
            catch (Exception ex) when (ex is VssObjectNotFoundException || ex.HResult == VssObjectNotFoundHResult)
            {
                // 幂等：快照不存在即已达成"删除"目标（typed 异常为主，HRESULT 兜底防错误码映射缺口）
                await File.WriteAllTextAsync(outFile, "deleted (absent)" + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            await File.WriteAllTextAsync(outFile, $"deleted {normalizedId}" + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            return await WriteErrorAsync(outFile, $"VSS 快照删除失败：{normalizedId}：{ex.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>尽力删除已创建的卷影快照（创建失败/取消时清理孤儿卷影；失败不阻断错误回写）。</summary>
    private static void TryDeleteSnapshot(Guid snapshotId)
    {
        try
        {
            using IVssBackupComponents backup = VssFactoryProvider.Default.GetVssFactory().CreateVssBackupComponents();
            backup.InitializeForBackup(null!); // delete 路径仅需 Initialize（与 DeleteAsync 一致）
            backup.DeleteSnapshot(snapshotId, forceDelete: true);
        }
        catch
        {
            // 尽力而为：清理失败不阻断错误回写
        }
    }

    /// <summary>尽力删除临时 sidecar 文件（忽略异常）。</summary>
    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>卷根归一化：仅接受盘符根（<c>C:</c> / <c>C:\</c>），归一为 <c>C:\</c>（VSS AddToSnapshotSet
    /// 要求以反斜杠结尾的卷根）；相对路径、UNC、子目录等一律拒绝——提权面收窄。</summary>
    /// <returns>归一化卷根；不合法返回 <c>null</c>。</returns>
    private static string? NormalizeVolumeRoot(string raw)
    {
        string v = raw.Trim();
        if (v.Length is 2 or 3
            && char.IsAsciiLetter(v[0])
            && v[1] == ':'
            && (v.Length == 2 || v[2] == '\\'))
        {
            return char.ToUpperInvariant(v[0]) + ":\\";
        }

        return null;
    }

    /// <summary>失败出口：错误消息写入结果文件（UTF-8）；结果文件本身写不进去时退回 stderr，退出码恒为 1。</summary>
    private static async Task<int> WriteErrorAsync(string outFile, string message, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(outFile, message + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 结果文件写不进去时消息只能走 stderr，退出码仍是失败
            Console.Error.WriteLine(message);
        }

        return 1;
    }
}
