using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 收集日志的 <see cref="ILogger"/> 假件（照 <c>FileWebServerTests.CapturingLogger</c> 的做法）。
/// </summary>
/// <remarks>
/// 🔴 禁止静默失败要求「失败必须留痕」，所以日志内容本身就是断言对象，
/// 不能像旧工程那样丢一个 <c>_ => { }</c> 了事。断言失败时把全部日志打进消息，
/// 排查时能直接看到被测代码到底说了什么。
/// </remarks>
public sealed class CapturingLogger : ILogger
{
    /// <summary>已记录的日志行（带级别前缀）。</summary>
    public List<string> Messages { get; } = [];

    /// <summary>全部日志拼成一段文本，供断言失败消息使用。</summary>
    public string Dump() => string.Join("\n", Messages);

    /// <summary>是否记录过至少一条 Warn。</summary>
    public bool HasWarning => Messages.Exists(m => m.StartsWith("[WARN]", StringComparison.Ordinal));

    /// <inheritdoc/>
    public void Info(string message) => Messages.Add("[INFO] " + message);

    /// <inheritdoc/>
    public void Warn(string message) => Messages.Add("[WARN] " + message);

    /// <inheritdoc/>
    public void Error(string message, Exception? ex = null) => Messages.Add("[ERROR] " + message);
}

/// <summary>
/// 规则驱动的 <see cref="IMusicTagReader"/> 假件：按路径决定「成功/失败」与标签内容。
/// </summary>
/// <remarks>
/// <b>扫描器测试一律用它，不用真读取器</b>——这正是 <see cref="IMusicTagReader"/>
/// 契约存在的理由：扫描器的职责是「枚举、去重、并发、进度、失败归集、兜底」，
/// 与「TagLib 怎么解析容器」无关。用假件后这些用例不依赖音频夹具、可精确构造任意组合
/// （比如「第 3 个文件损坏」），真读取器的解析能力由 <c>TagLibMusicTagReaderTests</c> 单独覆盖。
/// </remarks>
public sealed class FakeTagReader : IMusicTagReader
{
    /// <summary>未指定扩展名时的默认清单。</summary>
    private static readonly string[] DefaultExtensions = [".mp3", ".flac"];

    private int _readCount;

    /// <summary>构造假件。</summary>
    /// <param name="extensions">声称支持的扩展名（省略时为 <c>.mp3</c> 与 <c>.flac</c>）。</param>
    public FakeTagReader(params string[] extensions)
    {
        string[] effective = extensions.Length > 0 ? extensions : DefaultExtensions;
        SupportedExtensions = new HashSet<string>(effective, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>路径包含此子串的文件判为读取失败（<c>null</c> 表示全部成功）。</summary>
    public string? FailWhenPathContains { get; set; }

    /// <summary>失败时回传的原因。</summary>
    public string FailureReason { get; set; } = "文件已损坏";

    /// <summary>成功时的标签工厂（<c>null</c> 表示回一个全空的 <see cref="MusicTagInfo"/>）。</summary>
    public Func<string, MusicTagInfo>? TagFactory { get; set; }

    /// <summary><see cref="Read"/> 被调用次数（并发安全）。</summary>
    public int ReadCount => Volatile.Read(ref _readCount);

    /// <inheritdoc/>
    public MusicTagReadResult Read(string filePath)
    {
        Interlocked.Increment(ref _readCount);

        if (FailWhenPathContains is not null &&
            filePath.Contains(FailWhenPathContains, StringComparison.OrdinalIgnoreCase))
        {
            return MusicTagReadResult.Fail(FailureReason);
        }

        return MusicTagReadResult.Ok(TagFactory?.Invoke(filePath) ?? new MusicTagInfo());
    }

    /// <inheritdoc/>
    public MusicCover? ReadCover(string filePath) => null;
}

/// <summary>
/// 收集全部进度上报的 <see cref="IProgress{T}"/> 假件（同步收集，不经 UI 编组）。
/// </summary>
/// <remarks>
/// 扫描器用 <c>Parallel.ForEachAsync</c> 并发上报，故内部加锁并保留上报顺序。
/// 生产环境用 <c>Progress&lt;T&gt;</c>（自动编组到 UI 线程），这里换成同步收集便于断言。
/// </remarks>
public sealed class RecordingProgress : IProgress<MusicScanProgress>
{
    private readonly List<MusicScanProgress> _snapshots = [];

    /// <summary>按上报顺序的进度快照副本。</summary>
    public IReadOnlyList<MusicScanProgress> Snapshots
    {
        get
        {
            lock (_snapshots)
            {
                return _snapshots.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public void Report(MusicScanProgress value)
    {
        lock (_snapshots)
        {
            _snapshots.Add(value);
        }
    }
}
