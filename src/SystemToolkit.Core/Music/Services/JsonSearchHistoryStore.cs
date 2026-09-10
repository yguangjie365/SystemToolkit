using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 搜索历史 JSON 持久化（AtomicFile 原子写；读失败降级空表，🔴 不静默——损坏经 logger 记一条）。
/// 与 <see cref="JsonMusicLibraryStore"/> 同范式，仅承载字符串列表。
/// </summary>
public sealed class JsonSearchHistoryStore(string filePath, ILogger? logger = null) : ISearchHistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>单条长度上限（🟠 审查 2026-09-11，🟠-7）。与 VM 侧
    /// <c>MusicManagerViewModel.MaxSearchHistoryLength</c> 保持一致（跨层不能直接引用，故各自定义）。</summary>
    private const int MaxEntryLength = 64;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath, ct);
            List<string>? list = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
            if (list is null || list.Count == 0)
            {
                return [];
            }

            // 🟠-7（读取侧纵深防御）：写入侧 RecordSearchHistory 已是第一道闸，此处再清洗一遍，
            // 用途有二：① 清理**升级前遗留**的脏数据（含零宽/超长的历史项在 UI 上是不可见的
            // 空条目，用户肉眼无法复现也就永远删不掉）；② 防御外部直接改写该 JSON 的情况。
            var cleaned = new List<string>(list.Count);
            foreach (string raw in list)
            {
                string q = (TextSanitizer.StripInvisible(raw) ?? string.Empty).Trim();
                if (q.Length == 0 || q.Length > MaxEntryLength)
                {
                    continue;
                }

                if (cleaned.Any(h => string.Equals(h, q, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                cleaned.Add(q);
            }

            return cleaned;
        }
        catch (Exception ex)
        {
            logger?.Warn($"搜索历史文件不可读/损坏（{ex.Message}），已降级为空——{filePath}");
            return [];
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyList<string> history, CancellationToken ct = default)
        => Task.Run(() => AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(history, JsonOpts)), ct);
}
