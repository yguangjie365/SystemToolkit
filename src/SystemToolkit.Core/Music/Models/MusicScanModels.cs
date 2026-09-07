namespace SystemToolkit.Core.Music.Models;

/// <summary>扫描进度快照（供 <see cref="IProgress{T}"/> 上报，UI 进度条与「扫描中 x/y」文案绑定）。</summary>
/// <param name="Scanned">已处理文件数（含失败跳过的）。</param>
/// <param name="Total">本轮待处理文件总数。</param>
/// <param name="CurrentFile">当前正在处理的文件名（不含目录，避免长路径撑爆状态栏）。</param>
public sealed record MusicScanProgress(int Scanned, int Total, string CurrentFile)
{
    /// <summary>完成比例（0–1）。<see cref="Total"/> 为 0 时返回 0，不产生 NaN。</summary>
    public double Ratio => Total > 0 ? Math.Clamp(Scanned / (double)Total, 0d, 1d) : 0d;

    /// <summary>百分比整数（0–100），供进度条 <c>Value</c> 直接绑定。</summary>
    public int Percent => (int)Math.Round(Ratio * 100);
}

/// <summary>
/// 一个扫描失败条目（损坏文件 / 无法解析的文件）。
/// </summary>
/// <param name="FilePath">失败文件的绝对路径。</param>
/// <param name="Reason">失败原因（可直接展示，如「文件已损坏」「不支持的音频格式」）。</param>
/// <remarks>
/// 🔴 禁止静默失败：旧工程 <c>LocalMusicScanner</c> 把解析失败的文件只写进日志、
/// 对调用方返回 null，用户在 UI 上完全看不到「为什么少了 12 首」。
/// 本类型就是为了让失败清单能一路传到 UI 的失败态。
/// </remarks>
public sealed record MusicScanFailure(string FilePath, string Reason);

/// <summary>一次目录扫描的完整结果。</summary>
/// <param name="Songs">成功解析的曲目（按路径排序，结果稳定可复现）。</param>
/// <param name="Failures">解析失败的文件清单。</param>
/// <param name="InaccessiblePaths">
/// 因权限不足或路径不存在而<b>整段无法枚举</b>的目录清单。
/// </param>
/// <remarks>
/// <para><see cref="InaccessiblePaths"/> 单列一档是因为它与单文件失败量级不同：
/// 一个无权限的子目录可能吞掉几百首歌。旧工程在 <c>EnumerateSupportedFiles</c> 里
/// 直接 <c>catch (UnauthorizedAccessException) { return []; }</c>，
/// 结果是「扫描完成，0 首」且没有任何解释——典型的静默失败。</para>
/// <para><see cref="WasCancelled"/>：取消时<b>保留</b>已扫到的部分结果而不是抛
/// <see cref="OperationCanceledException"/> 丢弃全部。旧工程是后者，
/// 用户扫到第 3000 首时点取消，前 3000 首的解析成果直接作废、下次从零开始。</para>
/// </remarks>
public sealed record MusicScanResult(
    List<MusicSong> Songs,
    List<MusicScanFailure> Failures,
    List<string> InaccessiblePaths)
{
    /// <summary>本轮是否被用户取消（true 时 <see cref="Songs"/> 是部分结果）。</summary>
    public bool WasCancelled { get; init; }

    /// <summary>处理过的文件总数（成功 + 失败）。</summary>
    public int ProcessedCount => Songs.Count + Failures.Count;

    /// <summary>是否完全成功（无失败文件、无不可达目录、未被取消）。</summary>
    public bool IsClean => Failures.Count == 0 && InaccessiblePaths.Count == 0 && !WasCancelled;

    /// <summary>构造一个空结果（目录不存在、无音频文件等早退场景）。</summary>
    public static MusicScanResult Empty() => new([], [], []);
}
