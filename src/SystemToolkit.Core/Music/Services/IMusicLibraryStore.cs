using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 曲库读取结果。
/// </summary>
/// <param name="Library">读到的曲库；读取失败时为空曲库（<see cref="MusicLibrary"/> 的默认值）。</param>
/// <param name="LoadWarning">
/// 降级原因（可直接展示给用户，如「曲库缓存文件损坏，已重置——请重新扫描」）；正常读取时为 null。
/// </param>
/// <remarks>
/// 用「结果 + 警告」而不是抛异常或直接吞掉：曲库 JSON 是<b>可重建的派生缓存</b>
/// （重新扫描目录即可完整恢复），所以损坏时降级为空是对的，不该像
/// <c>TcpTuningService</c> 还原快照那样拒绝继续。但 🔴 禁止静默失败——
/// 旧工程读缓存失败只写日志、对 UI 返回空列表，用户看到的是「曲库 0 首」且毫无解释，
/// 无法区分「真的没歌」和「缓存坏了」。本字段就是为了把这句话带到 UI 上。
/// </remarks>
public sealed record MusicLibraryLoadResult(MusicLibrary Library, string? LoadWarning)
{
    /// <summary>是否是降级读取（缓存损坏/不可读）。</summary>
    public bool IsDegraded => LoadWarning is not null;
}

/// <summary>
/// 曲库持久化契约（Q-008 用户 2026-09-07 裁定：模块私有 JSON + <c>AtomicFile</c>，
/// 落 <c>%AppData%/SystemToolkit/music-library.json</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 SQLite</b>：仓库当前零 SQLite 代码（<c>Directory.Packages.props</c> 无相关包、
/// <c>src/</c> 无 <c>SqliteConnection</c>），已交付的七个模块一律 JSON + <c>AtomicFile</c>。
/// 为音乐单独引入一套存储栈不值当，且 JSON 天然满足 Docs/03 §4.9 的「模块故障隔离」倾向
/// ——曲库文件损坏不会波及主库。</para>
/// <para>🟠 <b>写入必须走 <see cref="Utilities.AtomicFile"/></b>（<c>SourceSanityGuardTests</c>
/// 会拦直写文件的 <c>File.WriteAllText</c>）：扫描到一半被中断/断电时，
/// 非原子写入会留下半截 JSON，下次启动整个曲库读不回来。
/// 旧工程 <c>LocalMusicScanner.SaveCacheAsync</c> 用的是裸 <c>File.Create</c>，正是这个问题。</para>
/// <para><b>不含封面</b>：见 <see cref="MusicSong"/> 的 remarks——封面惰性读取，不入库。</para>
/// </remarks>
public interface IMusicLibraryStore
{
    /// <summary>曲库文件绝对路径（UI 的「打开曲库文件位置」与诊断信息用）。</summary>
    string LibraryFilePath { get; }

    /// <summary>
    /// 读取曲库。文件不存在时返回空曲库（首次运行属正常，<see cref="MusicLibraryLoadResult.LoadWarning"/> 为 null）；
    /// 文件损坏或不可读时返回空曲库 + 降级警告。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>读取结果（含降级警告）。</returns>
    /// <remarks>
    /// 读到曲库后还应剔除 <see cref="MusicSong.LocalPath"/> 已不存在的条目
    /// （用户移动/删除了文件），并把剔除数量并入 <see cref="MusicLibraryLoadResult.LoadWarning"/>，
    /// 否则列表里会留着一堆点了就报错的幽灵曲目。
    /// </remarks>
    Task<MusicLibraryLoadResult> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// 原子写入曲库（覆盖式）。必要时自动创建目标目录。
    /// </summary>
    /// <param name="library">要落盘的曲库。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入任务。</returns>
    /// <remarks>
    /// 写入失败（磁盘满、无权限）时抛 <see cref="IOException"/> 或
    /// <see cref="UnauthorizedAccessException"/>——落盘失败<b>不能</b>静默，
    /// 否则用户以为曲库已保存，重启后发现扫描白做了。
    /// </remarks>
    Task SaveAsync(MusicLibrary library, CancellationToken ct = default);
}
