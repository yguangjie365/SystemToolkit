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
            return list ?? [];
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
