namespace SystemToolkit.Core.GameManager.Models;

/// <summary>Steam 客户端记住的用户（来源：{SteamDir}\config\loginusers.vdf）。</summary>
public sealed record SteamUser
{
    /// <summary>Steam64 ID（数字字符串，如 "76561197960265728"）。</summary>
    public string SteamId64 { get; init; } = string.Empty;

    /// <summary>登录账号名（账号登录用的 AccountName，非显示名）。</summary>
    public string AccountName { get; init; } = string.Empty;

    /// <summary>上次登录时使用的显示昵称（PersonaName）。</summary>
    public string PersonaName { get; init; } = string.Empty;

    /// <summary>loginusers.vdf 中标记 MostRecent=1 的账户（即下次启动自动登录的那个）。</summary>
    public bool MostRecent { get; init; }

    /// <summary>是否记住密码（loginusers.vdf RememberPassword=1）。</summary>
    public bool RememberPassword { get; init; }

    /// <summary>上次登录时间戳（unix 秒）。</summary>
    public ulong Timestamp { get; init; }

    /// <summary>小头像 URL 或本地 base64 data:image/png。空时 UI 显示占位图标。</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>中等头像 URL。</summary>
    public string? AvatarMediumUrl { get; init; }

    /// <summary>大头像 URL。</summary>
    public string? AvatarFullUrl { get; init; }
}
