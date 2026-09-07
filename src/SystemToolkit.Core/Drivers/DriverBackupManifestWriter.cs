using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Drivers;

/// <summary>备份范围。</summary>
public enum DriverBackupScope
{
    /// <summary>全部驱动包。</summary>
    All = 0,

    /// <summary>仅第三方（oem 前缀）驱动包。</summary>
    ThirdPartyOnly = 1,
}

/// <summary>
/// 驱动备份产物三件套写入器（FR-02 验收：manifest.json + devices.json + checksum.json）。
/// 纯文件生成，不依赖数据库；失败抛异常由调用方决定是否阻断。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverBackupManifestWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>写三件套到 destDir 根目录。packages 为本次导出的包集合；bindings 为设备绑定明细。</summary>
    public static void Write(
        string destDir,
        DriverBackupScope scope,
        IReadOnlyList<DriverPackage> packages,
        IReadOnlyDictionary<string, List<DriverDeviceMapper.DeviceBinding>> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(bindings);
        Directory.CreateDirectory(destDir); // 测试/调用方可能传不存在的目录

        // manifest.json：备份元信息 + 包明细（机器信息绑定起步字段；CPU/GPU/主板指纹属 V0.4 恢复匹配范畴）
        var manifest = new
        {
            SchemaVersion = 1,
            CreatedAt = DateTime.Now.ToString("O"),
            MachineName = Environment.MachineName,
            WindowsVersion = System.Environment.OSVersion.VersionString,
            OsArchitecture = System.Environment.Is64BitOperatingSystem ? "x64" : "x86",
            Scope = scope.ToString(),
            PackageCount = packages.Count,
            Packages = packages.Select(p => new
            {
                p.PublishedName,
                p.OriginalName,
                p.Type,
                p.ClassName,
                p.ClassGuid,
                p.Provider,
                Date = p.Date?.ToString("O"),
                p.Version,
                p.IsThirdParty,
            }).ToList(),
        };
        AtomicFile.WriteAllText(
            Path.Combine(destDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOpts));

        // devices.json：每包关联设备（含 HardwareId=恢复匹配主键）
        var devices = packages.SelectMany(p =>
        {
            List<DriverDeviceMapper.DeviceBinding>? list = null;
            if (!bindings.TryGetValue(p.PublishedName, out list)
                && !bindings.TryGetValue(p.OriginalName, out list))
            {
                return Enumerable.Empty<object>();
            }

            return list.Select(b => new
            {
                Inf = p.PublishedName,
                DeviceName = b.DeviceName,
                HardwareId = b.HardwareId,
            });
        }).ToList();
        AtomicFile.WriteAllText(
            Path.Combine(destDir, "devices.json"),
            JsonSerializer.Serialize(devices, JsonOpts));

        // checksum.json：Drivers\ 下全部文件的 SHA-256（相对路径 → 哈希）
        string driversRoot = Path.Combine(destDir, "Drivers");
        var checksums = new Dictionary<string, string>();
        if (Directory.Exists(driversRoot))
        {
            foreach (string file in Directory.EnumerateFiles(driversRoot, "*", SearchOption.AllDirectories))
            {
                checksums[Path.GetRelativePath(destDir, file).Replace('\\', '/')] = ComputeSha256(file);
            }
        }

        AtomicFile.WriteAllText(
            Path.Combine(destDir, "checksum.json"),
            JsonSerializer.Serialize(checksums, JsonOpts));
    }

    /// <summary>流式 SHA-256（驱动包文件可达数百 MB，禁止一次性读入内存）。</summary>
    public static string ComputeSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
