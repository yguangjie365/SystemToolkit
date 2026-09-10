namespace SystemToolkit.Core.Music.Services;

/// <summary>在线搜索历史持久化（模块私有 JSON，落 %AppData%/SystemToolkit/music-search-history.json）。</summary>
public interface ISearchHistoryStore
{
    /// <summary>读取最近搜索词（新→旧）。文件不存在/损坏 → 空列表（🔴 不静默，损坏经 logger 记）。</summary>
    Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default);

    /// <summary>覆盖保存搜索历史（原子写）。</summary>
    Task SaveAsync(IReadOnlyList<string> history, CancellationToken ct = default);
}
