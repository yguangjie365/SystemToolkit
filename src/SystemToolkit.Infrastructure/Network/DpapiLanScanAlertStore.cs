using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.Network.LanScan;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Infrastructure.Network;

/// <summary>
/// <see cref="ILanScanAlertStore"/> 的 DPAPI 实现（落地计划 B3-③）：
/// 整表以 <see cref="ProtectedData"/>（CurrentUser + 应用级附加熵）加密后写入
/// <c>%LocalAppData%\SystemToolkit\net\lan-scan-alert.json</c>（<see cref="AtomicFile"/> 原子写）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 为什么加密一个"配置项"：Webhook URL 里通常嵌着机器人 <c>access_token</c>，
/// 拿到它就能往用户的群里发任意消息。这与 AGENTS §2 的"用户凭据禁明文落库"同口径
/// （同门做法见 <c>DpapiTrustedWebDeviceStore</c> / <c>DpapiSteamApiKeyStore</c>）。
/// </para>
/// <para>
/// 损坏 / 换机 / 解密失败一律回落 <see cref="LanScanAlertConfig.Default"/>（**全关**）并**不抛**：
/// 读不出来最坏是"用户得重填一次 URL"，而抛出去会让整个扫描页起不来——
/// 那是把"小故障放大成不可用"。
/// </para>
/// </remarks>
public sealed class DpapiLanScanAlertStore : ILanScanAlertStore
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "net", "lan-scan-alert.json");

    /// <summary>应用级附加熵（阻止其它程序用纯 DPAPI 默认作用域解密本文件）。</summary>
    private static readonly byte[] Entropy = "SystemToolkit.LanScan.Alert.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>进程内串行闸：Save 是"加密 + 原子写"，并发整表写会互相覆盖。</summary>
    private readonly object _gate = new();

    private readonly string _path;

    /// <summary>生产用默认路径；测试注入独立路径，避免污染真实配置。</summary>
    public DpapiLanScanAlertStore(string? filePath = null)
    {
        _path = filePath ?? DefaultPath;
    }

    /// <inheritdoc/>
    public string FilePath => _path;

    /// <inheritdoc/>
    public LanScanAlertConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return LanScanAlertConfig.Default;
            }

            try
            {
                string base64 = File.ReadAllText(_path).Trim();
                if (base64.Length == 0)
                {
                    return LanScanAlertConfig.Default;
                }

                byte[] plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);
                return LanScanAlertConfig.Normalize(
                    JsonSerializer.Deserialize<LanScanAlertConfig>(Encoding.UTF8.GetString(plain), JsonOpts));
            }
            catch (Exception)
            {
                // 不可读 / 解密失败 / JSON 损坏 / base64 非法 —— 一律视为"没配过"（全关），不抛
                return LanScanAlertConfig.Default;
            }
        }
    }

    /// <inheritdoc/>
    public void Save(LanScanAlertConfig config)
    {
        lock (_gate)
        {
            try
            {
                string json = JsonSerializer.Serialize(LanScanAlertConfig.Normalize(config), JsonOpts);
                byte[] protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                AtomicFile.WriteAllText(_path, Convert.ToBase64String(protectedBytes));
            }
            catch (Exception)
            {
                // 写失败不抛：最坏是"这次改动没存上"，用户再点一次即可；
                // 抛出去会把"勾了一下开关"变成整个操作失败，代价远大于收益。
            }
        }
    }
}
