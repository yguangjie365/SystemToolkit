using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hardware.Info;
using Microsoft.Win32;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Overview.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Overview.Services;

/// <summary>
/// 本机概览采集服务：硬件信息走 Hardware.Info（Windows 上基于 WMI）+ 系统信息
/// （OS / .NET 运行时 / 已安装程序 / 网络）。
/// 全部为同步方法，调用方应置于后台线程（见 OverviewViewModel）；
/// 各分区独立容错：单个 WMI 查询失败只跳过对应卡片，不影响整体结果。
/// 注意：Windows 上 WMI 首次初始化可能耗时约 20 秒，属正常现象，UI 需先显示加载态。
/// </summary>
/// <remarks>
/// 平台标注：本类读注册表（OS 版本 / 已安装程序），而 Core 为跨平台 TFM（net10.0），
/// 未标注会触发 CA1416；标注后调用方为 net10.0-windows（模块层）时不产生告警。
/// Release 构建「警告即错误」，此标注为必需而非可选。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class OverviewService : IOverviewCollector
{
    private const string WinNtCurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private readonly IHardwareInfo _hw;

    // LibreHardwareMonitor 传感器探测（温度/负载）；注入点便于测试替换
    private readonly HardwareSensorProbe _sensorProbe;

    // 模块日志接入（REVIEW-2026-08-30 P1-5）：分区容错的静默 catch 改为落盘留痕
    private readonly ILogger _logger;

    /// <summary>注入硬件信息源、传感器探测与模块日志（测试可替换）；缺省各取默认实现。</summary>
    public OverviewService(IHardwareInfo? hw = null, HardwareSensorProbe? sensorProbe = null, ILogger? logger = null)
    {
        _hw = hw ?? new HardwareInfo();
        // 探测类持有同一 logger：驱动静默失效（不抛异常）也要落盘可诊断
        _sensorProbe = sensorProbe ?? new HardwareSensorProbe(logger);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public OverviewData Collect()
    {
        var data = new OverviewData();
        // 传感器（CPU/GPU 温度、负载、全量清单）是增强信息：拿不到（非管理员/
        // 驱动被安全软件拦截）返回 null，各统计卡自动回退到无温度形态
        SensorSnapshot? sensors = TryProbeSensors();
        data.Sensors = sensors;
        CollectHardware(data, sensors);
        CollectSystem(data);
        return data;
    }

    private SensorSnapshot? TryProbeSensors()
    {
        try
        {
            return _sensorProbe.Probe();
        }
        catch (Exception ex)
        {
            // 传感器属于增强信息，拿不到不阻断概览页；但「为什么没有温度」
            // 此前完全无从排查（最常见：WinRing0 驱动被安全软件拦截），必须留痕
            _logger.Warn("传感器探测失败（温度/负载卡片将显示回退形态）：" + ex.Message);
            return null;
        }
    }

    private void CollectSystem(OverviewData data)
    {
        // 复用增量（2026-09-04）：按用户确认的概览页参考图，系统信息重组为
        // 「操作系统」+「用户 / 区域」两个面板并补充采集字段（安装日期/工作组/启动模式/
        // 上次登录/键盘布局等）。逐字段 try/catch 回退"不可用"（采集边界允许 catch-all）。
        data.System.Add(BuildOsPanel());
        data.System.Add(BuildUserRegionPanel());

        // ---- 已安装程序 ----
        data.InstalledPrograms.AddRange(ReadInstalledPrograms());
    }

    /// <summary>操作系统面板（版本/安装日期/系统类型/体验包/设备名/工作组/系统目录/启动模式）。</summary>
    private OverviewItem BuildOsPanel()
    {
        string osName;
        try
        {
            _hw.RefreshOperatingSystem();
            OS os = _hw.OperatingSystem;
            osName = string.IsNullOrWhiteSpace(os?.Name)
                ? RuntimeInformation.OSDescription.Trim()
                : OverviewFormat.TrimMicrosoftPrefix(os.Name);
        }
        catch (Exception ex)
        {
            _logger.Warn("操作系统名称采集失败，已回退运行时描述：" + ex.Message);
            osName = RuntimeInformation.OSDescription.Trim();
        }

        var rows = new List<OverviewRow>
        {
            new("操作系统", osName),
            new("版本", ReadDisplayVersion()),
            new("安装日期", ReadInstallDate()),
            new("系统类型", $"{(RuntimeInformation.OSArchitecture == Architecture.X64 ? "64 位" : RuntimeInformation.OSArchitecture.ToString())}，{ArchLabel(RuntimeInformation.ProcessArchitecture)} 处理器"),
            new("体验包", ReadExperiencePack()),
            new("设备名称", System.Environment.MachineName),
            new("工作组", ReadWorkgroup()),
            new("系统目录", System.Environment.SystemDirectory),
            new("启动模式", ReadFirmwareMode()),
            new(".NET 运行时", RuntimeInformation.FrameworkDescription.Trim()),
            new("运行时长", OverviewFormat.Uptime(TimeSpan.FromMilliseconds(System.Environment.TickCount64))),
        };
        return new OverviewItem("\uE770", "操作系统", rows: rows);
    }

    /// <summary>用户 / 区域面板（当前用户/类型/目录/上次登录/语言/区域/时区/键盘布局）。</summary>
    private OverviewItem BuildUserRegionPanel()
    {
        var rows = new List<OverviewRow>
        {
            new("当前用户", System.Environment.UserName),
            new("用户类型", ReadUserType()),
            new("用户目录", System.Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            new("上次登录", ReadLastLogon()),
            new("显示语言", CultureInfo.CurrentUICulture.DisplayName),
            new("系统区域", CultureInfo.CurrentCulture.Name),
            new("时区", TimeZoneInfo.Local.Id),
            new("键盘布局", ReadKeyboardLayout()),
        };
        return new OverviewItem("\uE977", "用户 / 区域", rows: rows);
    }

    /// <summary>DisplayVersion + Build.UBR 组合（如 24H2（Build 26100.3155））。</summary>
    private static string ReadDisplayVersion()
    {
        try
        {
            string? displayVersion = ReadRegistryValue(WinNtCurrentVersion, "DisplayVersion");
            string? buildNumber = ReadRegistryValue(WinNtCurrentVersion, "CurrentBuild");
            string? ubr = ReadRegistryValue(WinNtCurrentVersion, "UBR");
            string build = buildNumber ?? "";
            if (!string.IsNullOrEmpty(ubr))
            {
                build += "." + ubr;
            }

            if (!string.IsNullOrWhiteSpace(displayVersion) && build.Length > 0)
            {
                return $"{displayVersion.Trim()}（Build {build}）";
            }

            return build.Length > 0 ? "Build " + build : "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>系统安装日期（InstallDate 为 1970 epoch 秒）。</summary>
    private static string ReadInstallDate()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(WinNtCurrentVersion);
            if (key?.GetValue("InstallDate") is int epoch && epoch > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("yyyy-MM-dd");
            }

            return "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>Windows Feature Experience Pack（UBR 同源 RevisionID）。</summary>
    private static string ReadExperiencePack()
    {
        try
        {
            string? pack = ReadRegistryValue(WinNtCurrentVersion + "\\FeatureExperiencePack", "Version");
            return string.IsNullOrWhiteSpace(pack) ? "不可用" : "Windows Feature Experience Pack " + pack.Trim();
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>工作组 / 域（非域环境显示 WORKGROUP）。</summary>
    private string ReadWorkgroup()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Domain, PartOfDomain FROM Win32_ComputerSystem");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                bool? partOf = mo["PartOfDomain"] as bool?;
                string? domain = mo["Domain"] as string;
                if (partOf == true)
                {
                    return "域 " + domain;
                }

                return string.IsNullOrWhiteSpace(domain) ? "WORKGROUP" : domain;
            }

            return "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>处理器架构显示名（审查 L12：原硬编码 "x64 处理器"，ARM64 机器自相矛盾）。</summary>
    private static string ArchLabel(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "ARM64",
        _ => architecture.ToString(),
    };

    /// <summary>固件类型（PEFirmwareType：1=BIOS 2=UEFI）+ 快速启动（HiberbootEnabled）。</summary>
    private static string ReadFirmwareMode()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            // 值缺失（键不存在/权限不足）→ "不可用"：Convert.ToInt32(null)=0 不得被当成 BIOS（审查 L10）
            object? firmware = key?.GetValue("PEFirmwareType");
            string mode = firmware is null
                ? "不可用"
                : Convert.ToInt32(firmware) == 2 ? "UEFI" : "BIOS";

            using RegistryKey? power = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            object? hiberboot = power?.GetValue("HiberbootEnabled");
            string fastStartup = hiberboot is null
                ? "不可用"
                : Convert.ToInt32(hiberboot) == 1 ? "已启用" : "已禁用";
            return $"{mode}（快速启动：{fastStartup}）";
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>用户类型：检查当前身份是否在 Administrators 组。</summary>
    private static string ReadUserType()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            bool admin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            return admin ? "管理员（Administrators）" : "标准用户";
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>上次登录：当前用户 Profile 的最近加载时间（Win32_UserProfile）。</summary>
    private static string ReadLastLogon()
    {
        try
        {
            string profileDir = System.Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT LocalPath, LastUseTime FROM Win32_UserProfile");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                if (string.Equals(mo["LocalPath"] as string, profileDir, StringComparison.OrdinalIgnoreCase))
                {
                    if (mo["LastUseTime"] is string wmiTime)
                    {
                        var t = System.Management.ManagementDateTimeConverter.ToDateTime(wmiTime);
                        return t.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                }
            }

            return "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>键盘布局（HKCU Preload 第 1 项，映射常见布局名）。</summary>
    private static string ReadKeyboardLayout()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey("Keyboard Layout\\Preload");
            string? code = key?.GetValue("1") as string;
            return code switch
            {
                "00000804" => "中文（简体，中国）- 美式键盘",
                "00000409" => "英语（美国）- 美式键盘",
                _ => string.IsNullOrWhiteSpace(code) ? "不可用" : "布局代码 " + code,
            };
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>单分区失败仅降级不中断，但必须留痕（审查 2026-09-04 P2：原 catch 全静默）。</summary>
    private void TryRefresh(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Warn("概览分区采集失败（该分区显示回退形态）：" + ex.Message);
        }
    }

    /// <summary>是否为活跃的物理网卡（黑名单/判定下沉 <see cref="NetAdapterFilter"/>，与 NetManager 共用）。</summary>
    private static bool IsActivePhysicalAdapter(NetworkInterface adapter)
        => NetAdapterFilter.IsUserFacingAdapter(
            adapter.NetworkInterfaceType,
            adapter.OperationalStatus == OperationalStatus.Up,
            adapter.IsReceiveOnly,
            adapter.Description,
            adapter.Name);

    /// <summary>
    /// 声卡可见性判定（纯函数，便于单测）：过滤虚拟音频设备。
    /// 实机实证（2026-09-02）：Win32_SoundDevice 会返回 Nahimic（Easy Surround /
    /// mirroring）与 NVIDIA Virtual Audio 等虚拟设备——「声卡五张」的主因；
    /// 只保留真实音频硬件（Realtek / NVIDIA HDMI Output 等）。
    /// </summary>
    internal static bool IsUserFacingSoundDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        string lower = name.ToLowerInvariant();
        string[] blacklist =
        {
            "virtual", "mirroring", "remote audio", "nahimic",
        };
        return !blacklist.Any(lower.Contains);
    }

    private static string? ReadRegistryValue(string subKey, string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    private static List<InstalledProgram> ReadInstalledPrograms()
    {
        var result = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 64 位 + 32 位（WOW6432Node）系统级安装 + 用户级安装
        ScanRoot(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", seen, result);
        ScanRoot(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", seen, result);
        ScanRoot(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", seen, result);

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void ScanRoot(RegistryKey hive, string path, ISet<string> seen, ICollection<InstalledProgram> result)
    {
        try
        {
            using RegistryKey? root = hive.OpenSubKey(path);
            if (root is null)
            {
                return;
            }

            foreach (string subName in root.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? appKey = root.OpenSubKey(subName);
                    if (appKey is null)
                    {
                        continue;
                    }

                    long? sizeBytes = appKey.GetValue("EstimatedSize") is int sizeKb && sizeKb > 0
                        ? (long)sizeKb * 1024L
                        : null;

                    InstalledProgram? program = OverviewFormat.BuildInstalledProgram(
                        appKey.GetValue("DisplayName") as string,
                        appKey.GetValue("DisplayVersion") as string,
                        appKey.GetValue("Publisher") as string,
                        appKey.GetValue("InstallDate") as string,
                        sizeBytes,
                        appKey.GetValue("SystemComponent") as int?,
                        appKey.GetValue("ParentKeyName") as string,
                        appKey.GetValue("ReleaseType") as string);

                    if (program is null)
                    {
                        continue;
                    }
                    if (!seen.Add(program.Name + "|" + program.Version))
                    {
                        continue;
                    }
                    result.Add(program);
                }
                catch
                {
                    // 单个子键异常不影响其余条目
                }
            }
        }
        catch
        {
            // 注册表路径不可访问（权限等）时静默跳过
        }
    }
}
