using System.Text.Json.Serialization;

namespace SystemToolkit.Core.Software.Models;

/// <summary>安装历史里的动作类型。</summary>
public enum InstallAction
{
    /// <summary>安装。</summary>
    Install = 0,

    /// <summary>升级。</summary>
    Upgrade = 1,

    /// <summary>卸载。</summary>
    Uninstall = 2,
}

/// <summary>安装历史里的结果。</summary>
public enum InstallOutcome
{
    /// <summary>成功。</summary>
    Success = 0,

    /// <summary>失败（含 winget 退出码与异常）。</summary>
    Failed = 1,

    /// <summary>用户取消（单条取消 / 批量"关窗即停"）。</summary>
    Cancelled = 2,

    /// <summary>按忽略清单跳过（未执行）。</summary>
    Skipped = 3,
}

/// <summary>
/// 一条安装历史记录。
/// <para>
/// 🔴 **为什么是独立存储而不是复用 `AppLog`**（2026-09-13 认领时回源实证）：
/// <c>AppLog</c> 对外只有 <c>AddSink/Write/CreateLogger/Reset/Shutdown</c>，**零读取 API**；
/// 可读的只有落盘日志，而日志有**保留期**（`LogMaintenance.DefaultRetainDays`）与**单文件 10 MB 滚动**——
/// 历史会在滚动后**悄悄消失**，而"安装历史"的全部价值恰恰是**经得起时间**。
/// 故本类型配 <c>InstallHistoryStore</c> 独立落盘。
/// </para>
/// </summary>
public sealed class InstallHistoryEntry
{
    /// <summary>发生时间（本地时区展示，落盘带偏移）。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>动作。</summary>
    public InstallAction Action { get; set; }

    /// <summary>结果。</summary>
    public InstallOutcome Outcome { get; set; }

    /// <summary>winget 包 Id（缺失时按空处理，但 <see cref="InstallHistoryLog.Sanitize"/> 会剔除空 Id）。</summary>
    public string PackageId { get; set; } = "";

    /// <summary>软件显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>动作前的版本（升级时有意义）。</summary>
    public string FromVersion { get; set; } = "";

    /// <summary>动作后的版本（成功时可填）。</summary>
    public string ToVersion { get; set; } = "";

    /// <summary>winget 退出码（成功为 0；取消/异常为 0）。</summary>
    public int ExitCode { get; set; }

    /// <summary>补充说明（失败原因提示 / 异常消息 / "按忽略清单跳过"）。</summary>
    public string Detail { get; set; } = "";

    /// <summary>动作文案（UI 直接绑定；与 CSV 共用 <see cref="InstallHistoryLabels"/>）。</summary>
    [JsonIgnore]
    public string ActionText => InstallHistoryLabels.ActionText(Action);

    /// <summary>结果文案（同上）。</summary>
    [JsonIgnore]
    public string OutcomeText => InstallHistoryLabels.OutcomeText(Outcome);

    /// <summary>本地时间文案（落盘存偏移，展示转本地时区）。</summary>
    [JsonIgnore]
    public string TimeText => Timestamp.ToLocalTime().ToString("MM-dd HH:mm");
}

/// <summary>
/// 动作与结果的显示文案。🔴 UI 与 CSV **共用这一份**——
/// 不让"安装/升级/卸载"这类标签在两个地方各写一遍（本仓有过同型漂移的实证）。
/// </summary>
public static class InstallHistoryLabels
{
    /// <summary>动作文案。</summary>
    public static string ActionText(InstallAction action) => action switch
    {
        InstallAction.Upgrade => "升级",
        InstallAction.Uninstall => "卸载",
        _ => "安装",
    };

    /// <summary>结果文案。</summary>
    public static string OutcomeText(InstallOutcome outcome) => outcome switch
    {
        InstallOutcome.Success => "成功",
        InstallOutcome.Failed => "失败",
        InstallOutcome.Cancelled => "取消",
        _ => "跳过",
    };
}

/// <summary>
/// 安装历史清单（最新在前，环上限）。
/// 读写在 <c>InstallHistoryStore</c>；本类型只做内存语义，便于单测直接钉裁剪与净化规则。
/// </summary>
public sealed class InstallHistoryLog
{
    /// <summary>条目上限（超出丢最旧；历史是"近期可回溯"，不是无限归档）。</summary>
    public const int MaxEntries = 200;

    /// <summary>格式版本（便于将来迁移）。</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>记录（**最新在前**）。</summary>
    public List<InstallHistoryEntry> Entries { get; set; } = new List<InstallHistoryEntry>();

    /// <summary>追加一条（插入到最前）并按上限裁剪。</summary>
    /// <returns>因超限被丢弃的条目数。</returns>
    public int Append(InstallHistoryEntry entry)
    {
        Entries ??= new List<InstallHistoryEntry>();
        Entries.Insert(0, entry);

        int overflow = Entries.Count - MaxEntries;
        if (overflow <= 0)
        {
            return 0;
        }

        Entries.RemoveRange(MaxEntries, overflow);
        return overflow;
    }

    /// <summary>剔除结构上无效的条目（null / 缺包 Id），返回剔除数量。</summary>
    public int Sanitize()
    {
        Entries ??= new List<InstallHistoryEntry>();
        return Entries.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.PackageId));
    }
}
