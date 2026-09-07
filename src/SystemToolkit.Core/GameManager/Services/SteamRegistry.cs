using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 注册表信息读。对标 NexBox steam.rs <c>get_steam_install_info_inner + ActiveUser</c>。
/// <para>只在 Windows 平台有效；非 Windows 直接返回空。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SteamRegistry
{
    private const string HKCU_SteamPath = @"Software\Valve\Steam";
    private const string HKLM_Steam64 = @"SOFTWARE\Valve\Steam";
    private const string HKLM_Steam32 = @"SOFTWARE\WOW6432Node\Valve\Steam";

    /// <summary>读取 Steam 安装路径。优先 HKCU SteamPath/InstallPath，再回退 HKLM 64 位 + 32 位 InstallPath。</summary>
    public static string? GetInstallPath()
    {
        try
        {
            using RegistryKey? hkcu = Registry.CurrentUser.OpenSubKey(HKCU_SteamPath, writable: false);
            if (hkcu is not null)
            {
                string? v = hkcu.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(v))
                    return NormalizePath(v);
                v = hkcu.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(v))
                    return NormalizePath(v);
            }
            using RegistryKey? hklm64 = Registry.LocalMachine.OpenSubKey(HKLM_Steam64, writable: false);
            if (TryString(hklm64, "InstallPath", out string? p64))
                return NormalizePath(p64!);
            using RegistryKey? hklm32 = Registry.LocalMachine.OpenSubKey(HKLM_Steam32, writable: false);
            if (TryString(hklm32, "InstallPath", out string? p32))
                return NormalizePath(p32!);
        }
        catch (System.Security.SecurityException) { /* 无权限访问整棵 HKLM/SOFTWARE 的父节点；忽略 */ }
        catch (UnauthorizedAccessException) { }
        catch (ObjectDisposedException) { }
        return null;
    }

    /// <summary>HKCU 的 ActiveProcess ActiveUser（Steam 运行时写入的当前登录用户 Steam64）。</summary>
    public static string? GetActiveProcessUserId()
    {
        try
        {
            using RegistryKey? hkcu = Registry.CurrentUser.OpenSubKey(HKCU_SteamPath + @"\ActiveProcess", writable: false);
            if (TryString(hkcu, "ActiveUser", out string? v))
                return v;
        }
        catch { /* 无 key 或无权限，安全返回 null */ }
        return null;
    }

    /// <summary>写 HKCU\...\Steam\AutoLoginUser + RememberPassword。供 switch_account 最后一步用。</summary>
    public static void SetAutoLoginUser(string accountName, bool rememberPassword)
    {
        using RegistryKey hkcu = Registry.CurrentUser.CreateSubKey(HKCU_SteamPath, writable: true);
        hkcu.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
        hkcu.SetValue("RememberPassword", rememberPassword ? 1 : 0, RegistryValueKind.DWord);
    }

    private static bool TryString(RegistryKey? key, string name, [NotNullWhen(true)] out string? value)
    {
        value = key?.GetValue(name) as string;
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>
    /// Steam 注册表路径有时是正斜杠（如 "C:/Program Files (x86)/Steam"），统一改为反斜杠、
    /// 去除尾部分隔符（除了盘符根）。
    /// </summary>
    internal static string NormalizePath(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;
        string p = raw.Replace('/', '\\');
        if (p.Length > 1 && (p[^1] == '\\' || p[^1] == '/'))
            p = p.Substring(0, p.Length - 1);
        return p;
    }
}
