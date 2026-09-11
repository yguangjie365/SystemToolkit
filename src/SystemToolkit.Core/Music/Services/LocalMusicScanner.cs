using System.Collections.Concurrent;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 本地曲库扫描器：递归枚举多个根目录下的音频文件，经 <see cref="IMusicTagReader"/>
/// 并行解析标签，产出 <see cref="MusicScanResult"/>。
/// </summary>
/// <remarks>
/// <para>搬移自旧工程 <c>LocalMusicScanner</c>（265 行），并行骨架
/// （<c>Parallel.ForEachAsync</c> + <c>ConcurrentBag</c> + 并行度上限 8）原样保留，
/// 标签层由 ATL 换成 <see cref="IMusicTagReader"/>。相对旧实现的六处修正见各成员备注，
/// 汇总：<b>路径枚举改为手写栈式遍历</b>（旧 <c>Directory.EnumerateFiles(AllDirectories)</c>
/// 遇到第一个无权限子目录会中断<b>整棵</b>枚举）、<b>跳过 ReparsePoint</b>（junction/符号链接
/// 成环会让扫描永不结束）、<b>跨根去重</b>、<b>取消时保留部分结果</b>、
/// <b>失败文件与不可达目录显式回传</b>（🔴 禁止静默失败）、<b>Id 改用 SHA-256</b>。</para>
/// <para><b>不负责持久化</b>：旧实现里的 <c>CacheFilePath</c> / <c>SaveCacheAsync</c> /
/// <c>LoadCacheAsync</c> 已移除——曲库落盘属 <c>IMusicLibraryStore</c>（批次 5），
/// 扫描器只做「目录 → 曲目列表」这一件事。旧实现还有一个从未被读取的死常量
/// <c>PerFileTimeoutMs</c> 和空的 <c>Dispose()</c>，一并删除。</para>
/// </remarks>
public sealed class LocalMusicScanner
{
    /// <summary>
    /// 并行度上限。
    /// </summary>
    /// <remarks>
    /// 上限 8 而不是 <see cref="System.Environment.ProcessorCount"/>：扫描是 IO 密集型，
    /// 机械盘上并行度过高只会制造随机寻道抖动，反而更慢；同时避免扫全盘时把 CPU 占满、
    /// 让 UI 失去响应。
    /// </remarks>
    private static readonly int MaxParallelism = Math.Min(8, Math.Max(1, System.Environment.ProcessorCount));

    /// <summary>本地曲目 ID 前缀（为二阶段在线音源的 ID 留出命名空间，避免冲突）。</summary>
    private const string LocalIdPrefix = "local:";

    /// <summary>标签缺失时的艺术家兜底值。</summary>
    private const string UnknownArtist = "未知艺术家";

    /// <summary>进度上报的最小间隔（每处理这么多个文件上报一次，避免 5000 次 UI 编组）。</summary>
    private const int ProgressStride = 25;

    private readonly ILogger _logger;
    private readonly IMusicTagReader _tagReader;
    private readonly IReadOnlySet<string> _supportedExtensions;

    /// <summary>构造扫描器。</summary>
    /// <param name="logger">日志契约。</param>
    /// <param name="tagReader">标签读取契约（测试可注入假件）。</param>
    public LocalMusicScanner(ILogger logger, IMusicTagReader tagReader)
    {
        _logger = logger;
        _tagReader = tagReader;
        _supportedExtensions = tagReader.SupportedExtensions;
    }

    /// <summary>
    /// 扫描多个根目录（各自递归），返回解析好的曲目、失败文件清单与不可达目录清单。
    /// </summary>
    /// <param name="rootDirectories">扫描根目录清单；允许含不存在/无权限的项（会计入不可达清单）。</param>
    /// <param name="progress">进度上报（<c>null</c> 表示不上报）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// 扫描结果。<b>取消时不抛异常</b>，而是返回已扫到的部分结果并把
    /// <see cref="MusicScanResult.WasCancelled"/> 置 true；
    /// 曲目按路径排序，故同输入多次扫描结果稳定可复现。
    /// </returns>
    public async Task<MusicScanResult> ScanAsync(
        IReadOnlyList<string> rootDirectories,
        IProgress<MusicScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        // LOG-4：曲库扫描是分钟级长操作——四出口全部经 timing 收敛为一条三字段记录
        LogTiming timing = _logger.Time("ScanMusicLibrary");
        if (rootDirectories is null || rootDirectories.Count == 0)
        {
            timing.Complete(LogResult.Rejected, LogLevel.Info, "曲库扫描未启动：未提供任何根目录");
            return MusicScanResult.Empty();
        }

        var inaccessible = new HashSet<string>(StringComparer.Ordinal);
        List<string> files;
        try
        {
            files = EnumerateFiles(rootDirectories, inaccessible, ct);
        }
        catch (OperationCanceledException)
        {
            timing.Complete(LogResult.Cancelled, LogLevel.Warn,
                $"曲库扫描在枚举阶段被取消（不可达目录 {inaccessible.Count} 个）");
            return MusicScanResult.Empty() with { WasCancelled = true };
        }

        if (files.Count == 0)
        {
            timing.Complete(LogResult.Success, LogLevel.Info,
                $"曲库扫描完成：未发现音频文件（不可达目录 {inaccessible.Count} 个）");
            return new MusicScanResult([], [], [.. inaccessible]);
        }

        _logger.Info($"[Music] 开始扫描 {files.Count} 个文件（并行度 {MaxParallelism}，根目录 {rootDirectories.Count} 个）");

        var songs = new ConcurrentBag<MusicSong>();
        var failures = new ConcurrentBag<MusicScanFailure>();
        int scanned = 0;
        bool cancelled = false;
        int total = files.Count;

        try
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelism,
                    CancellationToken = ct,
                },
                async (filePath, token) =>
                {
                    MusicTagReadResult result = await Task.Run(() => _tagReader.Read(filePath), token).ConfigureAwait(false);
                    int index = Interlocked.Increment(ref scanned);

                    if (result is { Success: true, Tags: not null })
                    {
                        songs.Add(ToSong(filePath, result.Tags));
                    }
                    else
                    {
                        failures.Add(new MusicScanFailure(filePath, result.FailureReason ?? "无法解析"));
                    }

                    if (progress is not null && (index % ProgressStride == 0 || index == total))
                    {
                        progress.Report(new MusicScanProgress(index, total, Path.GetFileName(filePath)));
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // 保留已扫到的部分结果：用户扫到第 3000 首时点取消，不该让这 3000 首作废。
            cancelled = true;
        }

        List<MusicSong> ordered = [.. songs.OrderBy(s => s.LocalPath, StringComparer.OrdinalIgnoreCase)];
        timing.Complete(
            cancelled ? LogResult.Cancelled : LogResult.Success,
            cancelled ? LogLevel.Warn : LogLevel.Info,
            $"曲库扫描{(cancelled ? "被取消（保留部分结果）" : "完成")}：成功 {songs.Count}、失败 {failures.Count}、不可达目录 {inaccessible.Count}");

        return new MusicScanResult(ordered, [.. failures], [.. inaccessible]) { WasCancelled = cancelled };
    }

    /// <summary>
    /// 把标签载荷与文件系统元数据合并成一首曲目，并施加曲库级兜底策略。
    /// </summary>
    /// <remarks>
    /// 兜底<b>只在扫描器这一层做</b>（读取器如实转述文件内容，见 <see cref="MusicTagInfo"/> 备注）：
    /// 标题缺失 → 文件名（不含扩展名）；艺术家缺失 → 「未知艺术家」。
    /// 专辑/流派/专辑艺术家缺失一律留空串，因为「无专辑」与「专辑名未知」对用户是两种含义，
    /// 不宜混为一谈；「专辑」视图分组时再回退到艺术家。
    /// </remarks>
    private MusicSong ToSong(string filePath, MusicTagInfo tags)
    {
        long sizeBytes = 0L;
        DateTime modifiedUtc = DateTime.MinValue;
        try
        {
            FileInfo info = new(filePath);
            sizeBytes = info.Length;
            modifiedUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            // 文件在枚举与解析之间被删掉/移走：元数据缺失不该让整个文件作废，标签仍然可用。
            _logger.Warn($"[Music] 读取文件系统元数据失败：{filePath} —— {ex.Message}");
        }

        string title = string.IsNullOrWhiteSpace(tags.Title)
            ? Path.GetFileNameWithoutExtension(filePath)
            : tags.Title;

        return new MusicSong
        {
            Id = BuildSongId(filePath),
            LocalPath = filePath,
            Name = title,
            Artist = string.IsNullOrWhiteSpace(tags.Artist) ? UnknownArtist : tags.Artist,
            Album = tags.Album,
            AlbumArtist = tags.AlbumArtist,
            Genre = tags.Genre,
            TrackNumber = tags.TrackNumber,
            Year = tags.Year,
            DurationMs = tags.DurationMs,
            BitrateKbps = tags.BitrateKbps,
            SampleRateHz = tags.SampleRateHz,
            Channels = tags.Channels,
            FileSizeBytes = sizeBytes,
            ModifiedTimeUtc = modifiedUtc,
            HasEmbeddedCover = tags.HasCover,
            HasEmbeddedLyrics = !string.IsNullOrWhiteSpace(tags.Lyrics),
        };
    }

    /// <summary>
    /// 由绝对路径生成跨进程稳定的曲目 ID：<c>local:</c> + 路径 SHA-256 的前 16 位十六进制。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>旧实现是坏的</b>：它用 <c>path.GetHashCode(StringComparison.OrdinalIgnoreCase)</c>，
    /// 而 .NET Core 起字符串哈希<b>每进程随机化</b>，重启后同一首歌的 ID 就变了，
    /// 于是曲库缓存永远命中不了、播放队列与「最近播放」历史全部静默失效。
    /// <para>旧实现还把文件大小拼进 ID（<c>:12345</c>），后果是重新压制同一首歌
    /// （标签改动、码率转换）就会换 ID，历史记录断链。ID 只该由<b>位置</b>决定。</para>
    /// <para>Windows 文件系统大小写不敏感，故先统一大写再哈希，避免
    /// <c>C:\Music</c> 与 <c>c:\music</c> 生成两个 ID。</para>
    /// </remarks>
    // MUSIC-5：Id 生成提升到 MusicSong.BuildIdFor（模型语义，测试/持久化共用单点真相）
    private static string BuildSongId(string fullPath) => MusicSong.BuildIdFor(fullPath);

    /// <summary>
    /// 手写栈式递归遍历：逐个目录枚举，单个目录失败只跳过它自己并记入不可达清单。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不能</b>用 <c>Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)</c>：
    /// 它的惰性枚举在撞上第一个 <see cref="UnauthorizedAccessException"/> 时直接抛出，
    /// <b>整棵树</b>剩余部分全丢。用户把「D:\」整个加进曲库时，
    /// 撞上 <c>D:\System Volume Information</c> 就会「扫描完成，0 首」——旧实现正是这个缺陷，
    /// 而且它 <c>catch { return []; }</c> 把原因也吞了。
    /// <para><b>跳过 ReparsePoint</b>：junction / 符号链接指回祖先目录会让递归成环，
    /// 扫描永不结束且路径无限变长。曲库场景下跟随链接的收益远低于挂死的风险。</para>
    /// </remarks>
    /// <param name="rootDirectories">根目录清单。</param>
    /// <param name="inaccessible">输出：无法枚举的目录（累积，调用方负责去重语义）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>去重后的待解析文件绝对路径清单。</returns>
    private List<string> EnumerateFiles(
        IReadOnlyList<string> rootDirectories,
        HashSet<string> inaccessible,
        CancellationToken ct)
    {
        // 跨根去重：用户可能同时添加了「D:\Music」与「D:\Music\Rock」（嵌套），
        // 或两个指向同一位置的 junction，不去重就会出现重复曲目。
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var files = new List<string>();
        var pending = new Stack<string>();

        foreach (string root in rootDirectories)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullPath;
            try
            {
                // GetFullPath 不会去掉结尾分隔符，故「D:\Music」与「D:\Music\」会算成两个根、
                // 把整棵子树枚举两遍。TrimEndingDirectorySeparator 让二者归一（对「D:\」这类根安全）。
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Music] 扫描根目录路径无效：{root} —— {ex.Message}");
                inaccessible.Add(root);
                continue;
            }

            if (!Directory.Exists(fullPath))
            {
                _logger.Warn($"[Music] 扫描根目录不存在：{fullPath}");
                inaccessible.Add(fullPath);
                continue;
            }

            pending.Push(fullPath);
        }

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            if (!seen.Add(dir))
            {
                // 已被另一条根路径覆盖过（嵌套根），跳过以免重复枚举整棵子树。
                continue;
            }

            EnumerateOneDirectory(dir, files, inaccessible, pending, ct);
        }

        return files;
    }

    /// <summary>枚举单个目录的子目录与音频文件；单个条目失败只影响它自己。</summary>
    private void EnumerateOneDirectory(
        string dir,
        List<string> files,
        HashSet<string> inaccessible,
        Stack<string> pending,
        CancellationToken ct)
    {
        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                if (IsReparsePoint(subDir, inaccessible))
                {
                    continue;
                }

                pending.Push(subDir);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordInaccessible(dir, ex, inaccessible);
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (_supportedExtensions.Contains(Path.GetExtension(file)))
                {
                    files.Add(file);
                }
            }
        }
        catch (Exception ex)
        {
            RecordInaccessible(dir, ex, inaccessible);
        }
    }

    /// <summary>
    /// 判定目录是否为 junction / 符号链接。判定失败（无权限等）时记入不可达清单并按「跳过」处理。
    /// </summary>
    private bool IsReparsePoint(string dir, HashSet<string> inaccessible)
    {
        try
        {
            return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Music] 子目录属性读取失败，已跳过：{dir} —— {ex.Message}");
            inaccessible.Add(dir);
            return true;
        }
    }

    /// <summary>把无法枚举的目录记入清单并留日志（🔴 不得静默吞掉）。</summary>
    private void RecordInaccessible(string dir, Exception ex, HashSet<string> inaccessible)
    {
        _logger.Warn($"[Music] 目录枚举失败，整段跳过：{dir} —— {ex.GetType().Name}: {ex.Message}");
        inaccessible.Add(dir);
    }
}
