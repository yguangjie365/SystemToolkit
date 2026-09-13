using System.Text.Json;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.GameManager.Cover;

/// <summary>一条候选封面地址的失败记录。</summary>
public sealed class CoverFailureEntry
{
    /// <summary>候选地址（记忆的键）。</summary>
    public string Url { get; set; } = "";

    /// <summary>失败原因（如 <c>HTTP 404</c>）。</summary>
    public string Reason { get; set; } = "";

    /// <summary>最近一次失败时间。</summary>
    public DateTimeOffset FailedAt { get; set; }

    /// <summary>累计失败次数（同一地址反复失败时递增，便于诊断）。</summary>
    public int Count { get; set; }
}

/// <summary>失败记忆清单（可序列化的落盘形态）。</summary>
public sealed class CoverFailureLog
{
    /// <summary>条目上限（地址空间有限，防被异常数据撑爆）。</summary>
    public const int MaxEntries = 300;

    /// <summary>格式版本。</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>记录。</summary>
    public List<CoverFailureEntry> Entries { get; set; } = new List<CoverFailureEntry>();

    /// <summary>剔除结构上无效的条目（缺 Url），返回剔除数量。</summary>
    public int Sanitize()
    {
        Entries ??= new List<CoverFailureEntry>();
        return Entries.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.Url));
    }
}

/// <summary>
/// **封面候选失败记忆**（落地计划 B6）。
/// <para>
/// 目的：Steam CDN 的候选链里存在**已知必然失败**的项（如旧域 <c>cdn.cloudflare.steamstatic.com</c>
/// 截至 2026-09-13 实测 404，代码里作为"以防回退"的保底保留），若不记忆，则**每次刷新游戏库**
/// 都会把整条候选链重试一遍（每个 URL 10 秒超时）——刷一次库白等几十秒。
/// </para>
/// <para>
/// 🔴 **只记"确定性失败"，不记瞬时故障**（判据见 <see cref="ShouldRemember"/>）：
/// 404/410 表示"这个资源就是不在了"，该记；超时/5xx/DNS 抖动是暂时的，记下来会让**网络抖动
/// 演变成一整天的封面缺失**——那比多试一次更糟。
/// </para>
/// <para>
/// 🔴 记忆**带 TTL**（默认 6 小时）并在过期后自动失效：CDN 会变（本仓就经历过域迁移），
/// 永久拉黑一个地址等于把"将来可能修好"的路堵死。成功一次也会**立即清除**该地址的记忆。
/// </para>
/// </summary>
public sealed class CoverFailureMemory
{
    /// <summary>默认记忆有效期（6 小时）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);

    /// <summary>默认落盘位置（与其它模块数据同用统一配置根）。</summary>
    public static string DefaultFilePath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "game-manager", "cover-failures.json");

    /// <summary>JSON 选项（与其它清单一致：缩进 + snake_case 字段名）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Action<string> _log;

    private readonly TimeSpan _ttl;

    private readonly string _filePath;

    private CoverFailureLog? _logData;

    /// <summary>注入落盘路径、TTL 与日志回调（测试可全部替换）。</summary>
    public CoverFailureMemory(string? filePath = null, TimeSpan? ttl = null, Action<string>? log = null)
    {
        _filePath = filePath ?? DefaultFilePath;
        _ttl = ttl ?? DefaultTtl;
        _log = log ?? ((Action<string>)delegate (string msg)
        {
            Console.WriteLine("[cover] " + msg);
        });
    }

    /// <summary>
    /// 该地址是否处于"近期确定性失败"状态（命中则调用方应**跳过**这次尝试）。
    /// 惰性加载：首次查询才读盘。
    /// </summary>
    public bool IsFailed(string? url, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        CoverFailureLog data = EnsureLoaded();
        for (int i = 0; i < data.Entries.Count; i++)
        {
            CoverFailureEntry entry = data.Entries[i];
            if (!string.Equals(entry.Url, url, StringComparison.Ordinal))
            {
                continue;
            }

            if (now - entry.FailedAt <= _ttl)
            {
                return true;
            }

            // 过期即失效并摘除（下次会真的重试一次）
            data.Entries.RemoveAt(i);
            Save(data);
            return false;
        }

        return false;
    }

    /// <summary>记录一次确定性失败（同一地址累计次数）。</summary>
    public void MarkFailed(string? url, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        CoverFailureLog data = EnsureLoaded();
        CoverFailureEntry? entry = data.Entries.FirstOrDefault(e => string.Equals(e.Url, url, StringComparison.Ordinal));
        if (entry is null)
        {
            entry = new CoverFailureEntry { Url = url };
            data.Entries.Add(entry);
        }

        entry.Reason = reason ?? "";
        entry.FailedAt = now;
        entry.Count++;

        if (data.Entries.Count > CoverFailureLog.MaxEntries)
        {
            // 丢最旧（按失败时间）
            data.Entries.Sort(static (a, b) => b.FailedAt.CompareTo(a.FailedAt));
            data.Entries.RemoveRange(CoverFailureLog.MaxEntries, data.Entries.Count - CoverFailureLog.MaxEntries);
        }

        Save(data);
    }

    /// <summary>该地址成功一次即清除记忆（"曾经坏过"不该跟着它一辈子）。</summary>
    public void ClearFailed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        CoverFailureLog data = EnsureLoaded();
        if (data.Entries.RemoveAll(e => string.Equals(e.Url, url, StringComparison.Ordinal)) > 0)
        {
            Save(data);
        }
    }

    /// <summary>
    /// 是否值得记住这次失败：只认**确定性失败**（资源不存在/已移除）。
    /// 🔴 超时、连接失败、5xx 一律**不记**——那类失败记下来会把短暂的网络抖动
    /// 放大成"一整天取不到封面"。
    /// </summary>
    public static bool ShouldRemember(int statusCode) => statusCode is 404 or 410;

    /// <summary>移除已过期条目（供启动清理；返回移除数量）。</summary>
    public int Prune(DateTimeOffset now)
    {
        CoverFailureLog data = EnsureLoaded();
        int removed = data.Entries.RemoveAll(e => now - e.FailedAt > _ttl);
        if (removed > 0)
        {
            Save(data);
        }

        return removed;
    }

    private CoverFailureLog EnsureLoaded()
    {
        if (_logData is not null)
        {
            return _logData;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                CoverFailureLog? loaded = JsonSerializer.Deserialize<CoverFailureLog>(File.ReadAllText(_filePath), JsonOpts);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    _logData = loaded;
                    return _logData;
                }
            }
        }
        catch (Exception ex)
        {
            _log("封面失败记忆损坏，按空记忆继续：" + ex.Message);
        }

        _logData = new CoverFailureLog();
        return _logData;
    }

    /// <summary>
    /// 落盘。🔴 与其它清单不同，这里**写失败只留痕、不上抛**：
    /// 失败记忆是**缓存性质的加速信息**，丢了只是下次多试几次，不该因为它写不进去
    /// 而把"获取封面"这条路打断（对比：用户清单/忽略清单丢失是数据损失，必须上抛）。
    /// </summary>
    private void Save(CoverFailureLog data)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch (Exception ex)
        {
            _log("保存封面失败记忆失败（不影响本次运行）：" + ex.Message);
        }
    }
}
