using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// 驱动类别 GUID → 中文/英文类名 解析。
/// 主路径：HKLM\SYSTEM\CurrentControlSet\Control\Class\{guid} 默认值（覆盖绝大多数系统类）。
/// Fallback：经 SetupAPI 定位 Store INF 读 [Version].Class=（实测发现 {4d36e96c MEDIA}
/// 等多个系统类键**没有默认值**，但对应 INF 自带 Class=MEDIA 文本）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverClassResolver
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupGetInfDriverStoreLocation(
        string fileName, nint altPlatformInfo, string? localeName,
        StringBuilder? returnBuffer, uint bufferSize, out uint requiredSize);

    /// <summary>GUID（任意大小写/带花括号）→ 类名；未收录返回 null。
    /// 调用方（DriverScanner）承担"取 INF 路径"的成本——本类不缓存路径，只查注册表 + 读 INF。</summary>
    public static string? Resolve(string? classNameOrGuid)
    {
        if (string.IsNullOrWhiteSpace(classNameOrGuid) || !classNameOrGuid.Contains('{'))
        {
            return null;
        }

        EnsureCache();
        return _cache!.TryGetValue(classNameOrGuid.Trim(), out string? name) ? name : null;
    }

    /// <summary>注册表无默认值时，由 DriverScanner 传入 Store INF 路径，读取 [Version].Class= 行解析类名。
    /// 若 INF 不存在/无 Class= 行/读取失败返回 null，调用方按原值（GUID）显示。</summary>
    public static string? ResolveFromInf(string? infPath)
    {
        if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
        {
            return null;
        }

        try
        {
            // 仅读 [Version] 段 ~20 行（标准 INF 该段在文件最前）
            foreach (string line in EnumerateLines(infPath, 64))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("[") || trimmed.StartsWith(";") || trimmed.StartsWith("//"))
                {
                    continue;
                }

                int eq = trimmed.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = trimmed[..eq].Trim();
                if (!key.Equals("Class", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = trimmed[(eq + 1)..].Trim().TrimEnd(',', ';');
                // 跳过占位符 %ClassName% / 注释
                if (value.Length > 0 && !value.StartsWith("%") && !value.StartsWith("$"))
                {
                    return value;
                }
            }
        }
        catch
        {
            // 单个 INF 解析失败不影响其它
        }

        return null;
    }

    /// <summary>枚举器：避免一次读大文件（部分 INF 数百 KB）</summary>
    private static IEnumerable<string> EnumerateLines(string path, int max)
    {
        int n = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (++n > max)
            {
                yield break;
            }

            yield return line;
        }
    }

    /// <summary>按发布名/原始名查 Store INF 路径（DriverScanner 调用）。失败返回 null。</summary>
    public static string? FindStoreInfPath(string? publishedName, string? originalName)
    {
        foreach (string? name in new[] { publishedName, originalName })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            _ = SetupGetInfDriverStoreLocation(name, 0, null, null, 0, out uint required);
            if (required == 0)
            {
                continue;
            }

            var buf = new StringBuilder((int)required);
            if (SetupGetInfDriverStoreLocation(name, 0, null, buf, required, out _))
            {
                return buf.ToString();
            }
        }

        return null;
    }

    private static void EnsureCache()
    {
        if (_cache is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_cache is not null)
            {
                return;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
                if (root is not null)
                {
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        if (!sub.StartsWith('{'))
                        {
                            continue;
                        }

                        try
                        {
                            using RegistryKey? key = root.OpenSubKey(sub);
                            if (key?.GetValue(null) is string name && name.Length > 0)
                            {
                                map[sub] = name;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            _cache = map;
        }
    }
}
