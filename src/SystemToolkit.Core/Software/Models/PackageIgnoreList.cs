namespace SystemToolkit.Core.Software.Models;

/// <summary>忽略范围。</summary>
public enum PackageIgnoreScope
{
    /// <summary>只忽略**这个版本**的更新（候选版本变了之后忽略自动失效）。</summary>
    Version = 0,

    /// <summary>永久忽略：不再提示更新，且**不参与批量安装 / 恢复环境**。</summary>
    Permanent = 1,
}

/// <summary>忽略清单条目（包级）。</summary>
public sealed class PackageIgnoreEntry
{
    /// <summary>winget 包 Id（或 msstore 的 9 位 id）。</summary>
    public string Id { get; set; } = "";

    /// <summary>源名（winget / msstore）；留空表示"不区分源"。</summary>
    public string Source { get; set; } = "";

    /// <summary>忽略范围。</summary>
    public PackageIgnoreScope Scope { get; set; } = PackageIgnoreScope.Permanent;

    /// <summary><see cref="PackageIgnoreScope.Version"/> 时被忽略的那个版本号。</summary>
    public string Version { get; set; } = "";

    /// <summary>记录时间（用于 UI 展示"何时忽略的"，也是清理排序依据）。</summary>
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// 忽略清单（包级"跳过此版本 / 永久忽略"）。纯内存模型：查询与增删都在这里，
/// 落盘由 <c>PackageIgnoreStore</c> 负责 —— 两者分离以便单测直接钉判据。
/// <para>
/// 🔴 **判据只有一份**（<see cref="IsIgnored"/>）：忽略是否命中必须全仓走这一个方法，
/// 不允许调用方各自写"比较 Id 和版本"的变体 —— 本仓有过"同一判据两处各写、改一处漏一处"的实证。
/// </para>
/// </summary>
public sealed class PackageIgnoreList
{
    /// <summary>条目上限（防文件被手工编辑成超大集合拖慢加载/界面；超出时丢弃最旧条目）。</summary>
    public const int MaxEntries = 500;

    /// <summary>清单格式版本（便于将来迁移）。</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>忽略条目（按记录时间倒序保存）。</summary>
    public List<PackageIgnoreEntry> Entries { get; set; } = new List<PackageIgnoreEntry>();

    /// <summary>
    /// **唯一判据**：该包在该候选版本下是否应被忽略。
    /// <para>
    /// 规则：Id 大小写不敏感相等；条目 <see cref="PackageIgnoreEntry.Source"/> 为空表示不限源，否则源也须相等。
    /// <see cref="PackageIgnoreScope.Permanent"/> 无条件命中；
    /// <see cref="PackageIgnoreScope.Version"/> 只在**候选版本与记录版本相同**时命中。
    /// </para>
    /// <para>
    /// 🔴 候选版本未知（空）时，Version 范围的条目**不命中** —— 保守方向：
    /// 无法确认是同一个版本时宁可**不跳过**（跳过会静默藏掉东西，不跳过最多多提示一次）。
    /// </para>
    /// </summary>
    public bool IsIgnored(string? id, string? source, string? candidateVersion)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        foreach (PackageIgnoreEntry entry in Entries)
        {
            if (entry is null || !string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.Source)
                && !string.Equals(entry.Source, source ?? "", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Scope == PackageIgnoreScope.Permanent)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(candidateVersion)
                && !string.IsNullOrWhiteSpace(entry.Version)
                && string.Equals(entry.Version, candidateVersion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>该 Id 是否有任何忽略记录（不论范围）——供「取消忽略」判断可用性。</summary>
    public bool Contains(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && Entries.Any(e => e is not null && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 新增或更新一条忽略记录（同 Id + 同源视为同一条，避免重复条目）；
    /// 超出 <see cref="MaxEntries"/> 时丢弃**最旧**的条目并回传丢弃数量。
    /// </summary>
    /// <returns>因超限被丢弃的条目数。</returns>
    public int AddOrUpdate(PackageIgnoreEntry entry)
    {
        Entries ??= new List<PackageIgnoreEntry>();
        string source = entry.Source ?? "";

        Entries.RemoveAll(e => e is not null
            && string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Source ?? "", source, StringComparison.OrdinalIgnoreCase));
        Entries.Add(entry);

        // 按记录时间倒序（新在前）——UI 与"丢最旧"都以这个序为准
        Entries.Sort(static (a, b) => b.RecordedAt.CompareTo(a.RecordedAt));

        int overflow = Entries.Count - MaxEntries;
        if (overflow <= 0)
        {
            return 0;
        }

        Entries.RemoveRange(MaxEntries, overflow);
        return overflow;
    }

    /// <summary>移除该 Id 的全部忽略记录（不论源与范围）。</summary>
    /// <returns>移除的条目数。</returns>
    public int Remove(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return 0;
        }

        return Entries.RemoveAll(e => e is not null && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>剔除结构上无效的条目（缺 Id / null），返回剔除数量。</summary>
    public int Sanitize()
    {
        Entries ??= new List<PackageIgnoreEntry>();
        return Entries.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.Id));
    }
}
