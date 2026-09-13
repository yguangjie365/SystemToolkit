using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.GameManager.Online;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Infrastructure.Steam;

/// <summary>
/// <see cref="ISteamApiKeyStore"/> 的 DPAPI 实现：API Key 以 <see cref="ProtectedData"/>
/// （CurrentUser + 应用级附加熵）加密后存入
/// <c>%LocalAppData%\SystemToolkit\steam\webapi-key.json</c>（<see cref="AtomicFile"/> 原子写）。
/// </summary>
/// <remarks>
/// 🔴 红线对齐：用户凭据禁明文落盘（AGENTS §2）。数据损坏/无法解密时按「未配置」处理返回 <c>null</c>
/// （UI 回到"未设置 Key"态），并**删除坏条目**避免每次都撞同一条坏数据。
/// 形状与处置对齐既有的 <c>DpapiOnlineCredentialStore</c>（音乐在线凭据）。
/// </remarks>
public sealed class DpapiSteamApiKeyStore : ISteamApiKeyStore
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "steam", "webapi-key.json");

    /// <summary>应用级附加熵（阻止其它程序用纯 DPAPI 默认作用域解密本文件）。</summary>
    private static readonly byte[] Entropy = "SystemToolkit.SteamWebApiKey.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>
    /// 进程内串行闸：三个公开方法都是「读文件 → 改 → 写回」的 Load-Modify-Save，无锁时并发保存会互相覆盖
    /// （同 <c>DpapiOnlineCredentialStore</c> 的处置）。Monitor 可重入，故 <see cref="Get"/> 内嵌的
    /// <see cref="Clear"/> 调用不会自锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>生产用默认路径；测试可注入独立路径避免污染真实凭据。</summary>
    public DpapiSteamApiKeyStore(string? filePath = null)
    {
        _path = filePath ?? DefaultPath;
    }

    private sealed record Payload(string ProtectedApiKey);

    /// <inheritdoc />
    public string? Get()
    {
        lock (_gate)
        {
            Payload? payload = Load();
            if (payload is null || payload.ProtectedApiKey.Length == 0)
            {
                return null;
            }

            try
            {
                byte[] plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(payload.ProtectedApiKey), Entropy, DataProtectionScope.CurrentUser);
                string apiKey = Encoding.UTF8.GetString(plain);
                return apiKey.Length == 0 ? null : apiKey;
            }
            catch (Exception)
            {
                // 不可读（换机 / 用户 profile 变更 / 文件被改坏）= 视为未配置；
                // 删除坏条目避免每次都撞（同线程重入，不会自锁）
                try
                {
                    Clear();
                }
                catch
                {
                    // 清理失败也按未配置继续——不阻塞主流程
                }

                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Set(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Clear();
            return;
        }

        lock (_gate)
        {
            byte[] plain = Encoding.UTF8.GetBytes(apiKey.Trim());
            byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Save(new Payload(Convert.ToBase64String(protectedBytes)));
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception)
            {
                // 删除失败不抛：调用方只关心"之后 Get 返回 null"，下次 Get 会再走损坏清理分支
            }
        }
    }

    private Payload? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        // 坏/截断 JSON 不得让 Set/Clear 抛出（同 DpapiOnlineCredentialStore 的 O15 处置）
        try
        {
            return JsonSerializer.Deserialize<Payload>(File.ReadAllText(_path), JsonOpts);
        }
        catch (Exception)
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // 删失败仍回退 null
            }

            return null;
        }
    }

    private void Save(Payload payload)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(payload, JsonOpts));
    }
}
