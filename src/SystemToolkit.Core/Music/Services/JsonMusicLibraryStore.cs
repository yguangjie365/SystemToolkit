using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 曲库持久化实现（Q-008 裁定：模块私有 JSON，落 <c>%AppData%/SystemToolkit/music-library.json</c>）。
/// </summary>
/// <remarks>
/// <para>写入走 <see cref="AtomicFile"/>（崩溃/断电不留半截 JSON）；读取降级策略按契约注释：
/// 文件不存在 = 首次运行（空曲库、无警告）；损坏/不可读 = 空曲库 + 降级警告（🔴 不静默）；
/// 成功后再剔除 <see cref="MusicSong.LocalPath"/> 已不存在的条目（用户移动/删除文件），
/// 剔除数量并入 <see cref="MusicLibraryLoadResult.LoadWarning"/> 带到 UI。</para>
/// </remarks>
public sealed class JsonMusicLibraryStore(string filePath, ILogger? logger = null) : IMusicLibraryStore
{
    /// <summary>曲库 JSON 序列化选项（缩进便于人工排查；MusicSong 全为基本类型与字符串，无需自定义转换器）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <inheritdoc />
    public string LibraryFilePath => filePath;

    /// <inheritdoc />
    public async Task<MusicLibraryLoadResult> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return new MusicLibraryLoadResult(new MusicLibrary(), null);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            logger?.Warn($"曲库文件不可读（{ex.Message}），已降级为空曲库——请重新扫描：{filePath}");
            return new MusicLibraryLoadResult(new MusicLibrary(), "曲库文件不可读，已重置——请重新扫描目录");
        }

        MusicLibrary? library;
        try
        {
            library = JsonSerializer.Deserialize<MusicLibrary>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            logger?.Warn($"曲库 JSON 损坏（{ex.Message}），已降级为空曲库——请重新扫描：{filePath}");
            return new MusicLibraryLoadResult(new MusicLibrary(), "曲库缓存文件损坏，已重置——请重新扫描目录");
        }

        library ??= new MusicLibrary();

        // 剔除 LocalPath 已不存在的条目（用户移动/删除了文件），避免列表里留着点了就报错的幽灵曲目
        int before = library.Songs.Count;
        var alive = library.Songs.Where(s => File.Exists(s.LocalPath)).ToList();
        string? warning = null;
        if (alive.Count < before)
        {
            int removed = before - alive.Count;
            warning = $"已剔除 {removed} 个已不存在的文件（可能被移动或删除），重新扫描可恢复";
            logger?.Warn($"曲库加载时剔除 {removed} 个已不存在的文件（共 {before} → {alive.Count}）");
            library = library with { Songs = alive };
        }

        return new MusicLibraryLoadResult(library, warning);
    }

    /// <inheritdoc />
    public Task SaveAsync(MusicLibrary library, CancellationToken ct = default)
    {
        // Task.Run：序列化 + 原子写离开调用线程（UI 调用方不用再自己包后台线程）；
        // 写失败时 AtomicFile 原样抛出（磁盘满/无权限不静默），经 await 传回调用方
        return Task.Run(() => AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(library, JsonOpts)), ct);
    }
}
