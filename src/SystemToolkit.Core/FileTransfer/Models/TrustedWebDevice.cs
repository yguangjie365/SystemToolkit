namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 已记住的手机设备（「30 天免配对」的长期凭据，2026-09-13 批次 P3 ⑲）。
/// <para>
/// 🔴 **只存令牌的哈希，不存明文**：明文令牌落在磁盘上等于把"随时可用的钥匙"抄了一份；
/// 哈希足以做校验，且文件被翻出来也不能直接拿来访问。
/// </para>
/// <para>
/// 与 <see cref="WebSessionInfo"/> 的关系：会话是**内存里的运行时状态**（可踢出、会过期），
/// 本记录是**跨重启的长期凭据**。撤销时必须两边一起删——只删一边会出现
/// "看起来踢掉了，下次扫码又免配对"（用户会以为撤销没生效）。
/// </para>
/// </summary>
public sealed record TrustedWebDevice
{
    /// <summary>记录标识（随机；用于把内存会话与长期记录对上）。</summary>
    public required string Id { get; init; }

    /// <summary>令牌哈希（SHA-256 十六进制小写）。</summary>
    public required string TokenHash { get; init; }

    /// <summary>设备标签（由 User-Agent 归纳；界面显示用）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>最近一次看到的来源 IP。</summary>
    public string Ip { get; init; } = string.Empty;

    /// <summary>
    /// 设备指纹（来源 IP + User-Agent 的哈希，2026-09-14 加）。
    /// <para>
    /// 存在的唯一理由是**去重**：同一台手机反复扫码配对时，旧记录此前从不清理，
    /// 列表里会堆出一串一模一样的条目（主人实测：21 条 "Android · Chrome"）。
    /// </para>
    /// <para>
    /// 空串 = 本字段引入之前写入的旧记录。去重时退回用 <see cref="Ip"/> + <see cref="Label"/> 认人
    /// ——否则用户升级前攒下的那批重复项永远清不掉。
    /// </para>
    /// </summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>首次记住的时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>过期时间（到期后与未记住一样，需重新配对）。</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>是否仍然有效。</summary>
    public bool IsValid(DateTimeOffset now) => now < ExpiresAt;

    /// <summary>剩余有效天数（界面显示"剩余 N 天"；已过期为 0）。</summary>
    public int RemainingDays(DateTimeOffset now)
    {
        TimeSpan left = ExpiresAt - now;
        return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalDays);
    }
}
