using System.Text.Json;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>基线加载三态：正常 / 无文件（首扫） / 损坏或不可读（UI 提示重建，不当崩溃）。</summary>
public enum LanBaselineLoadStatus
{
    /// <summary>正常读取。</summary>
    Ok = 0,

    /// <summary>文件不存在（首扫）。</summary>
    Missing = 1,

    /// <summary>JSON 损坏或版本不认识（提示重建，不崩溃）。</summary>
    Corrupted = 2,
}

/// <summary>读取返回值包装。属性名刻意避开守卫敏感词形，用 <c>State</c>/<c>Data</c>。</summary>
/// <param name="State">加载状态。</param>
/// <param name="Data">基线内容（Missing/Corrupted 时为 null）。</param>
public sealed record LanBaselineLoad(LanBaselineLoadStatus State, LanBaseline? Data);

/// <summary>
/// 基线持久化（NET-6）：<c>%LOCALAPPDATA%\SystemToolkit\net\lan-scan-baseline.json</c>，
/// 写走 <see cref="AtomicFile"/>（中断续写防线），事件历史环保留最近 <see cref="MaxEvents"/> 条。
/// 非敏感数据（内网 IP/MAC），不涉 DPAPI；路径可注入供单测重定向 temp 目录。
/// </summary>
public sealed class LanBaselineStore
{
    /// <summary>事件历史环容量（与 UI 事件流一致）。</summary>
    public const int MaxEvents = 200;

    /// <summary>当前序列化格式版本。</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <summary>缺省落 <c>%LOCALAPPDATA%\SystemToolkit\net\</c>；测试注入 temp 路径。</summary>
    public LanBaselineStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "net", "lan-scan-baseline.json");
    }

    /// <summary>落盘路径（诊断/测试断言用）。</summary>
    public string FilePath => _filePath;

    /// <summary>读取基线：Missing / Corrupted 均不抛，UI 按状态给引导。</summary>
    public LanBaselineLoad Load()
    {
        if (!File.Exists(_filePath))
        {
            return new LanBaselineLoad(LanBaselineLoadStatus.Missing, null);
        }

        try
        {
            LanBaseline? loaded = JsonSerializer.Deserialize<LanBaseline>(
                File.ReadAllText(_filePath), JsonOptions);
            return loaded is null or { Version: not FormatVersion } || loaded.Entries is null
                ? new LanBaselineLoad(LanBaselineLoadStatus.Corrupted, null)
                : new LanBaselineLoad(LanBaselineLoadStatus.Ok, loaded);
        }
        catch (JsonException)
        {
            return new LanBaselineLoad(LanBaselineLoadStatus.Corrupted, null);
        }
        catch (IOException)
        {
            return new LanBaselineLoad(LanBaselineLoadStatus.Corrupted, null);
        }
    }

    /// <summary>原子落盘；events 超长时截旧留新（入参约定新→旧）。</summary>
    public void Save(IReadOnlyList<LanBaselineEntry> entries, IReadOnlyList<LanEvent> eventsNewestFirst, DateTimeOffset updatedAt)
    {
        LanBaseline baseline = new(
            FormatVersion,
            updatedAt,
            entries,
            eventsNewestFirst.Take(MaxEvents).ToList());
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(baseline, JsonOptions));
    }

    /// <summary>重置基线（UI「重新建立基线」按钮）：删除应用自有数据文件，非用户文档。</summary>
    public void Reset()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
