namespace SystemToolkit.Core.GameManager.Models;

/// <summary>用户头像信息（从本地 avatarcache 或 steamcommunity XML 返回）。</summary>
public sealed record SteamAvatarInfo
{
    /// <summary>对应的 Steam64 ID（与 SteamUser.SteamId64 匹配）。</summary>
    public string SteamId64 { get; init; } = string.Empty;

    /// <summary>小档头像；本地缓存命中时为 base64 data URI。</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>中档头像 URL / data URI。</summary>
    public string? AvatarMediumUrl { get; init; }

    /// <summary>全尺寸头像 URL / data URI。</summary>
    public string? AvatarFullUrl { get; init; }
}
