using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 传输历史持久化（JSON 文件 + 原子写入）。
/// <para>
/// 用 JSON 而不是 SQLite：历史只是给人看的一叠流水账，没有查询需求，
/// 引入数据库会带来依赖与迁移成本，收益不成比例。
/// </para>
/// <para>
/// 关键约束：
/// <list type="bullet">
/// <item>写入用 <c>AtomicFile</c>——写到一半断电会留下半截 JSON，下次加载全部失败。</item>
/// <item>条目上限 <see cref="MaxEntries"/>，超出保留最近的：历史会无限增长，且陈旧记录没有价值。</item>
/// <item>任何读取失败（文件损坏 / 版本不兼容）都降级为空列表，绝不能让历史拖垮模块启动。</item>
/// </list>
/// </para>
/// </summary>
public sealed class TransferHistoryService
{
    /// <summary>最多保留的历史条数。</summary>
    public const int MaxEntries = 200;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _sync = new();

    /// <summary>
    /// 创建历史服务。
    /// </summary>
    /// <param name="directory">存放目录（通常传 ConfigService.ConfigDir）；为 null 时使用系统临时目录（测试用）。</param>
    public TransferHistoryService(string? directory = null)
    {
        string dir = directory ?? Path.Combine(Path.GetTempPath(), "SystemToolkit.History");
        _filePath = Path.Combine(dir, "transfers.json");
    }

    /// <summary>历史文件路径（调试与测试用）。</summary>
    public string FilePath => _filePath;

    /// <summary>读取全部历史（按结束时间倒序；文件缺失或损坏时返回空列表）。</summary>
    public IReadOnlyList<TransferHistoryEntry> Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_filePath))
                    return Array.Empty<TransferHistoryEntry>();
                string json = File.ReadAllText(_filePath);
                List<TransferHistoryEntry>? list = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(json, JsonOpts);
                if (list is null)
                    return Array.Empty<TransferHistoryEntry>();
                return list.OrderByDescending(e => e.FinishedAt).ToList();
            }
            catch
            {
                // 历史损坏不应影响传输功能本身
                return Array.Empty<TransferHistoryEntry>();
            }
        }
    }

    /// <summary>追加一条记录并落盘（超过上限时丢弃最旧的）。</summary>
    public void Append(TransferHistoryEntry entry)
    {
        lock (_sync)
        {
            var list = new List<TransferHistoryEntry>();
            try
            {
                if (File.Exists(_filePath))
                {
                    List<TransferHistoryEntry>? existing = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(
                        File.ReadAllText(_filePath), JsonOpts);
                    if (existing is not null)
                        list = existing;
                }
            }
            catch
            {
                // 读不出来就当没有历史，本次写入会重建一个干净文件
                list = new List<TransferHistoryEntry>();
            }

            list.Add(entry);
            if (list.Count > MaxEntries)
                list = list.OrderByDescending(e => e.FinishedAt).Take(MaxEntries).ToList();

            try
            {
                string dir = Path.GetDirectoryName(_filePath)!;
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(list, JsonOpts));
            }
            catch
            {
                // 历史写不进去不是致命问题，静默降级
            }
        }
    }

    /// <summary>清空历史。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
            catch
            {
                // 同上：清空失败不影响主功能
            }
        }
    }
}
