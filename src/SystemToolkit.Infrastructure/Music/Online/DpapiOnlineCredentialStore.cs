using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Infrastructure.Music.Online;

/// <summary>
/// <see cref="IOnlineCredentialStore"/> 的 DPAPI 实现：每平台 Cookie 以
/// <see cref="ProtectedData"/>（CurrentUser + 应用级附加熵）加密后存入
/// %LocalAppData%\SystemToolkit\net\online-credentials.json（AtomicFile 原子写）。
/// </summary>
/// <remarks>
/// 🔴 红线对齐：用户凭据禁明文落库（AGENTS §2）。数据损坏/无法解密时按「未存储」处理
/// 返回 null（对应 UI 重新登录流程）——凭据层不阻塞主流程，但删除坏条目避免反复报错。
/// </remarks>
public sealed class DpapiOnlineCredentialStore : IOnlineCredentialStore
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "net", "online-credentials.json");

    /// <summary>应用级附加熵（阻止其它程序用纯 DPAPI 默认作用域解密本文件）。</summary>
    private static readonly byte[] Entropy = "SystemToolkit.OnlineMusic.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>生产用默认路径；测试可注入独立路径避免污染真实凭据。</summary>
    public DpapiOnlineCredentialStore(string? filePath = null)
    {
        _path = filePath ?? DefaultPath;
    }

    private sealed record Payload(Dictionary<string, string> ProtectedCookies);

    /// <inheritdoc />
    public string? GetCookie(OnlineProvider provider)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            Payload? payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(_path), JsonOpts);
            string? key = Key(provider);
            if (payload?.ProtectedCookies is null
                || !payload.ProtectedCookies.TryGetValue(key, out string? protectedBase64))
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // 凭据不可读（换机/损坏/用户 profile 变更）＝视为未登录；删除坏条目避免每次都撞
            try
            {
                Clear(provider);
            }
            catch
            {
                // 清理失败也按未登录继续——不阻塞主流程
            }

            return null;
        }
    }

    /// <inheritdoc />
    public void SetCookie(OnlineProvider provider, string cookie)
    {
        Dictionary<string, string> protectedCookies = LoadProtected();
        byte[] plain = Encoding.UTF8.GetBytes(cookie);
        byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        protectedCookies[Key(provider)] = Convert.ToBase64String(protectedBytes);
        SaveProtected(protectedCookies);
    }

    /// <inheritdoc />
    public void Clear(OnlineProvider provider)
    {
        Dictionary<string, string> protectedCookies = LoadProtected();
        if (protectedCookies.Remove(Key(provider)))
        {
            SaveProtected(protectedCookies);
        }
    }

    private Dictionary<string, string> LoadProtected()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        Payload? payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(_path), JsonOpts);
        return payload?.ProtectedCookies ?? [];
    }

    private void SaveProtected(Dictionary<string, string> protectedCookies)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new Payload(protectedCookies), JsonOpts));
    }

    private static string Key(OnlineProvider provider) => provider switch
    {
        OnlineProvider.NetEase => "netease",
        OnlineProvider.QQMusic => "qqmusic",
        _ => provider.ToString().ToLowerInvariant(),
    };
}
