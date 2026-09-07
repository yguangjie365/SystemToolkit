using System.Runtime.Versioning;
using SystemToolkit.Core.GameManager.Models;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 客户端管理服务（聚合 14 条 API，对应 NexBox steam.rs 14 条 Tauri 命令）。
/// <para>所有操作均为纯本地 + 少量可选 HTTP（头像在线兜底），与 Blazor/WPF 模块零耦合。</para>
/// <para>本服务本身无 Windows 互操作以外的平台敏感代码；为避免调用方误误用，
/// 在模块层注册时自行加 [SupportedOSPlatform("windows")] 断言即可。</para>
/// </summary>
public sealed partial class SteamService
{
    // =============== S6 头像（本地优先 + 在线兜底） ===============
    /// <summary>
    /// 取指定用户的本地头像 PNG 绝对路径（纯本地，不外呼）。
    /// 实测（2026-09-05）：缓存在 `Steam安装目录\config\avatarcache\{steamId64}.png`；
    /// 部分用户为 `{steamId64}_medium.png`。缺失返回 null（UI 显示占位）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public string? GetAvatarPath(string steamId64)
    {
        if (string.IsNullOrWhiteSpace(steamId64))
            return null;
        return FindLocalAvatarPng(FindAvatarCacheDir(ExtractSteamInstallPath()), steamId64);
    }

    /// <summary>批量取头像：本地 avatarcache 优先，可选在线兜底（steamcommunity XML）。</summary>
    public async Task<SteamAvatarInfo[]> FetchUserAvatarsAsync(IReadOnlyList<SteamUser> users, bool onlineFallback, CancellationToken ct = default)
    {
        var results = new List<SteamAvatarInfo>(users.Count);
        foreach (SteamUser u in users)
        {
            string? localPng = FindLocalAvatarPng(FindAvatarCacheDir(ExtractSteamInstallPath()), u.SteamId64);
            string? base64 = null;
            if (localPng is not null)
            {
                try
                {
                    byte[] bytes = await File.ReadAllBytesAsync(localPng, ct).ConfigureAwait(false);
                    base64 = "data:image/png;base64," + Convert.ToBase64String(bytes);
                }
                catch (Exception e) { _logger.Warn($"读取本地头像失败 {localPng} — {e.Message}"); }
            }
            string? a1 = base64 ?? u.AvatarUrl;
            string? a2 = base64 ?? u.AvatarMediumUrl;
            string? a3 = base64 ?? u.AvatarFullUrl;
            if (onlineFallback && string.IsNullOrEmpty(a1))
            {
                (a1, a2, a3) = await FetchAvatarOnlineAsync(u.SteamId64, ct).ConfigureAwait(false);
            }
            results.Add(new SteamAvatarInfo
            {
                SteamId64 = u.SteamId64,
                AvatarUrl = a1,
                AvatarMediumUrl = a2,
                AvatarFullUrl = a3,
            });
        }
        return results.ToArray();
    }

}
