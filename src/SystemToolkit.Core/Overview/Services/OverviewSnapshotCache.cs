using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>
/// 概览磁盘快照缓存（JSON + AtomicFile 原子写）。
/// <para>复用来源：旧工程 OverviewViewModel 内嵌缓存实现（含审查修复 R3 测试隔离 / W2 原子写），
/// 按复用纪律提取为 Core 类——逻辑零改动，仅从 UI 层移到正确的层。</para>
/// <para>用途（用户 2026-09-04）：硬件/系统信息/已安装程序是慢变量，持久化后启动秒显；
/// 温度与占用是快变量，进入页面后由 2s 采样器刷新，不依赖缓存新鲜度。</para>
/// <para>失败哲学：坏缓存 / 超龄 / 模型漂移一律降级为冷启动采集（慢几秒但结果正确），绝不因缓存启动失败。</para>
/// </summary>
public sealed class OverviewSnapshotCache
{
    /// <summary>磁盘缓存最大有效期。超过视为无缓存，走冷启动采集。</summary>
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    private readonly string _filePath;
    private readonly ILogger _logger;

    /// <summary>路径可注入（测试隔离，审查修复 R3 同款）；默认与旧工程同路径，旧缓存可直接沿用。</summary>
    public OverviewSnapshotCache(ILogger? logger = null, string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>默认缓存文件路径（%LOCALAPPDATA%\SystemToolkit\overview-cache.json，与旧工程同路径）。</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "overview-cache.json");

    /// <summary>
    /// 磁盘缓存条目：采集时点 + 完整数据。
    /// </summary>
    /// <param name="CollectedAt">采集时间（UTC，用于超龄判定）。</param>
    /// <param name="Data">本次采集的完整概览数据。</param>
    public sealed record SnapshotEntry(DateTimeOffset CollectedAt, OverviewData Data);

    /// <summary>读出快照；文件缺失/损坏/超龄返回 null，调用方退化为冷启动采集。</summary>
    public SnapshotEntry? TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(_filePath), CacheJsonOptions);
            if (entry?.Data is null)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - entry.CollectedAt > CacheMaxAge)
            {
                _logger.Info("概览快照已超龄（>30 天），走冷启动采集");
                return null;
            }

            return new SnapshotEntry(entry.CollectedAt, entry.Data);
        }
        catch (Exception ex)
        {
            // 缓存损坏 / 模型漂移：静默退化会让「每次启动都慢几十秒」无从排查，必须留痕
            _logger.Warn("概览快照读取失败，退化为冷启动采集：" + ex.Message);
            return null;
        }
    }

    /// <summary>写入快照（原子写，审查修复 W2：半截 JSON = 每次启动都慢采集）。失败不影响主流程。</summary>
    public void Save(OverviewData data)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            AtomicFile.WriteAllText(_filePath, Serialize(data));
        }
        catch (Exception ex)
        {
            _logger.Warn("概览快照写入失败（下次启动将冷启动采集）：" + ex.Message);
        }
    }

    // ============================================================
    // 序列化：生产读写与单元测试共用同一份配置，避免两份逻辑漂移。
    //
    // 【重要】OverviewItem / OverviewRow / InstalledProgram 的属性全部是
    // 只读的 { get; }，且只有一个带默认参数的公共构造函数。System.Text.Json
    // 走「参数化构造」反序列化：按 camelCase 匹配构造函数参数名，JSON 中
    // 缺失的参数取默认值。两个必须知道的结论（均已实测确认）：
    //   1. 参数名必须能匹配到同名属性（大小写不敏感），否则直接抛异常——
    //      不是静默降级。异常被 TryLoad 吞掉，退化为冷启动采集：慢几秒，但结果正确。
    //   2. 因此「改模型参数名 / 增删属性」是一种无声的缓存失效，编译期无任何提示。
    //      改这些模型后必须跑 OverviewSnapshotCacheTests 的缓存契约用例。
    // ============================================================
    internal static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static string Serialize(OverviewData data)
        => JsonSerializer.Serialize(new CacheEntry { CollectedAt = DateTimeOffset.UtcNow, Data = data }, CacheJsonOptions);

    internal static OverviewData? Deserialize(string json)
        => JsonSerializer.Deserialize<CacheEntry>(json, CacheJsonOptions)?.Data;

    private sealed class CacheEntry
    {
        public DateTimeOffset CollectedAt { get; set; }

        public OverviewData Data { get; set; } = new();
    }
}
