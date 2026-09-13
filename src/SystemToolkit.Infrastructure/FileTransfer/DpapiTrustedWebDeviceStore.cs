using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Infrastructure.FileTransfer;

/// <summary>
/// <see cref="ITrustedWebDeviceStore"/> 的 DPAPI 实现（2026-09-13 批次 P3 ⑲）：
/// 整表以 <see cref="ProtectedData"/>（CurrentUser + 应用级附加熵）加密后写入
/// <c>%LocalAppData%\SystemToolkit\net\trusted-devices.json</c>（<see cref="AtomicFile"/> 原子写）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 红线对齐（AGENTS §2 用户凭据禁明文落库）：表里本来就只有**令牌哈希**，
/// 加密是第二道防线——防的是"文件被别的程序/别的用户翻出来看到设备名与 IP 这类元数据"。
/// </para>
/// <para>
/// 损坏/无法解密一律按「没有记住任何设备」处理并**不抛**：凭据层坏掉最多让用户重扫一次码，
/// 绝不该让 Web 服务起不来（那是"小故障放大成不可用"）。
/// </para>
/// </remarks>
public sealed class DpapiTrustedWebDeviceStore : ITrustedWebDeviceStore
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "net", "trusted-devices.json");

    /// <summary>应用级附加熵（阻止其它程序用纯 DPAPI 默认作用域解密本文件）。</summary>
    private static readonly byte[] Entropy = "SystemToolkit.FileTransfer.TrustedDevices.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>进程内串行闸：Save 是"加密 + 原子写"，并发整表写会互相覆盖。</summary>
    private readonly object _gate = new();

    /// <summary>生产用默认路径；测试可注入独立路径避免污染真实凭据。</summary>
    public DpapiTrustedWebDeviceStore(string? filePath = null)
    {
        _path = filePath ?? DefaultPath;
    }

    private sealed record Payload(List<TrustedWebDevice> Devices);

    /// <inheritdoc/>
    public IReadOnlyList<TrustedWebDevice> Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_path) ? ReadProtected() : Array.Empty<TrustedWebDevice>();
            }
            catch (Exception)
            {
                // 不可读（换机 / 损坏 / profile 变更）＝视为"没记住任何设备"：用户重扫一次码即可
                return Array.Empty<TrustedWebDevice>();
            }
        }
    }

    /// <inheritdoc/>
    public void Save(IReadOnlyList<TrustedWebDevice> devices)
    {
        lock (_gate)
        {
            try
            {
                string json = JsonSerializer.Serialize(new Payload(devices.ToList()), JsonOpts);
                byte[] protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                AtomicFile.WriteAllText(_path, Convert.ToBase64String(protectedBytes));
            }
            catch (Exception)
            {
                // 写失败不抛：最坏结果是"这次记住没生效"，下次配对时用户可再勾一次；
                // 抛出去会把"配对成功"整体变成失败，代价远大于收益。
            }
        }
    }

    /// <summary>读取并解密整表；空文件/解密失败返回空表。</summary>
    private IReadOnlyList<TrustedWebDevice> ReadProtected()
    {
        string base64 = File.ReadAllText(_path).Trim();
        if (base64.Length == 0)
        {
            return Array.Empty<TrustedWebDevice>();
        }

        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);
        Payload? payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(plain), JsonOpts);
        return payload?.Devices ?? (IReadOnlyList<TrustedWebDevice>)Array.Empty<TrustedWebDevice>();
    }
}
