namespace SystemToolkit.Core.GameManager.Models;

/// <summary>
/// 在线库存拉取结果（批次 4，2026-09-13）：**成功/失败都走同一个形状**，避免"失败返回 null"
/// 让调用方丢掉失败原因（UI 必须能如实说出为什么退回本地数据）。
/// </summary>
public sealed record SteamOnlineInventoryResult
{
    /// <summary>拉取到的条目（失败时为空集合）。</summary>
    public IReadOnlyList<SteamInventoryGame> Games { get; init; } = Array.Empty<SteamInventoryGame>();

    /// <summary>
    /// 失败或"可疑空结果"的原因（成功时 <c>null</c>）。
    /// <para>
    /// 正常失败示例：超时 / 网络不可达 / Key 无效（HTTP 401/403）。
    /// 可疑空结果：HTTP 成功但 <c>games</c> 缺失或为空——官方接口在「游戏详情」非公开时**正是这样返回**
    /// （实测见方案 §批次 4 风险栏），故不可当作"该账号真的没有游戏"。
    /// </para>
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// 是否为「HTTP 成功但结果为空」这一**可疑**情形（多半是隐私设置里「游戏详情」非公开）。
    /// <para>
    /// 调用方据此区分「账号真的没有游戏」与「接口被隐私设置挡住」——后者必须给出可操作提示，
    /// 且**不能**当作"在线成功但库存为空"（那是把不完整数据说成完整，红线）。
    /// </para>
    /// </summary>
    public bool EmptyResult { get; init; }

    /// <summary>是否成功（且结果非可疑空）。</summary>
    public bool Ok => Error is null;

    /// <summary>成功结果。</summary>
    public static SteamOnlineInventoryResult Success(IReadOnlyList<SteamInventoryGame> games) =>
        new() { Games = games };

    /// <summary>失败结果（必须带原因）。</summary>
    public static SteamOnlineInventoryResult Failure(string reason) =>
        new() { Error = reason };

    /// <summary>可疑空结果（HTTP 成功但无数据）。</summary>
    public static SteamOnlineInventoryResult Empty(string reason) =>
        new() { Error = reason, EmptyResult = true };
}
