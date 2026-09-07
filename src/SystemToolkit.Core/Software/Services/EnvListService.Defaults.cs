using System.Runtime.InteropServices;
using SystemToolkit.Core.Software.Models;

namespace SystemToolkit.Core.Software.Services;

public sealed partial class EnvListService
{
    private static List<WingetPackage> DefaultWingetPackages()
    {
        int num = 39;
        var list = new List<WingetPackage>(num);
        CollectionsMarshal.SetCount(list, num);
        Span<WingetPackage> span = CollectionsMarshal.AsSpan(list);
        // 卡片应用优先走 msstore 源（2026-08-29 用户要求）。以下 4 个经实测商店无上架
        // （PowerShell 7 / Google Chrome / Firefox / Sublime Text），保留 winget 源，
        // 其余全部使用商店 Id + msstore 源。
        span[0] = new WingetPackage
        {
            Id = "XP9KHM4BK9FZ7Q",
            Name = "Visual Studio Code",
            Description = "轻量级代码编辑器",
            Category = "开发工具",
            Source = "msstore"
        };
        span[1] = new WingetPackage
        {
            Id = "Microsoft.PowerShell",
            Name = "PowerShell 7",
            Description = "跨平台 Shell 与脚本环境（商店未上架，走 winget 源）",
            Category = "开发工具"
        };
        span[2] = new WingetPackage
        {
            Id = "XPDFF77QZ71XD0",
            Name = "Git",
            Description = "分布式版本控制",
            Category = "开发工具",
            Source = "msstore"
        };
        span[3] = new WingetPackage
        {
            Id = "XPDM3X7FL84X2K",
            Name = "7-Zip",
            Description = "开源压缩解压工具",
            Category = "效率",
            Source = "msstore"
        };
        span[4] = new WingetPackage
        {
            Id = "XP89DCGQ3K6VLD",
            Name = "PowerToys",
            Description = "Windows 效率工具集",
            Category = "效率",
            Source = "msstore"
        };
        span[5] = new WingetPackage
        {
            Id = "Google.Chrome",
            Name = "Google Chrome",
            Description = "谷歌浏览器（商店未上架，走 winget 源）",
            Category = "浏览器"
        };
        span[6] = new WingetPackage
        {
            Id = "Mozilla.Firefox",
            Name = "Firefox",
            Description = "火狐浏览器（商店未上架，走 winget 源）",
            Category = "浏览器"
        };
        span[7] = new WingetPackage
        {
            Id = "XPFCKBRNFZQ62G",
            Name = "微信",
            Description = "腾讯即时通讯（商店名 WeChat）",
            Category = "通讯",
            Source = "msstore"
        };
        span[8] = new WingetPackage
        {
            Id = "XP99SPMSZ082XF",
            Name = "QQ",
            Description = "腾讯即时通讯",
            Category = "通讯",
            Source = "msstore"
        };
        span[9] = new WingetPackage
        {
            Id = "XP8BSBGQW2DKS0",
            Name = "PotPlayer",
            Description = "全能视频播放器",
            Category = "影音",
            Source = "msstore"
        };
        span[10] = new WingetPackage
        {
            Id = "SublimeHQ.SublimeText",
            Name = "Sublime Text",
            Description = "轻量文本编辑器（商店未上架，走 winget 源）",
            Category = "开发工具"
        };
        span[11] = new WingetPackage
        {
            Id = "9WZDNCRFJBMP",
            Name = "应用安装程序",
            Description = "微软应用商店组件（升级）",
            Category = "系统组件",
            Source = "msstore"
        };
        span[12] = new WingetPackage
        {
            Id = "9PLJQ12FQ3CV",
            Name = "WinApp 运行库",
            Description = "Windows 应用运行时（升级）",
            Category = "运行库",
            Source = "msstore"
        };
        span[13] = new WingetPackage
        {
            Id = "9P5Z076K079H",
            Name = "WinApp 运行库",
            Description = "Windows 应用运行时（升级）",
            Category = "运行库",
            Source = "msstore"
        };
        span[14] = new WingetPackage
        {
            Id = "9PCSD6N03BKV",
            Name = "Windows 应用兼容增强",
            Description = "Windows 应用兼容性支持",
            Category = "系统组件",
            Source = "msstore"
        };
        span[15] = new WingetPackage
        {
            Id = "9P9TQF7MRM4R",
            Name = "WSL2",
            Description = "Windows Subsystem for Linux 2",
            Category = "系统自带",
            Source = "msstore"
        };
        span[16] = new WingetPackage
        {
            Id = "9MSMLRH6LZF3",
            Name = "记事本",
            Description = "Windows 记事本",
            Category = "系统自带",
            Source = "msstore"
        };
        span[17] = new WingetPackage
        {
            Id = "9WZDNCRFHVN5",
            Name = "计算器",
            Description = "Windows 计算器",
            Category = "系统自带",
            Source = "msstore"
        };
        span[18] = new WingetPackage
        {
            Id = "9NBLGGH4NNS1",
            Name = "应用安装程序",
            Description = "App Installer 组件",
            Category = "系统自带",
            Source = "msstore"
        };
        span[19] = new WingetPackage
        {
            Id = "9MZ1SNWT0N5D",
            Name = "PowerShell",
            Description = "Windows PowerShell",
            Category = "系统自带",
            Source = "msstore"
        };
        span[20] = new WingetPackage
        {
            Id = "9NSMXC3NB0HN",
            Name = "Windows SandBox",
            Description = "Windows 沙盒",
            Category = "系统自带",
            Source = "msstore"
        };
        span[21] = new WingetPackage
        {
            Id = "9NRMNT6GMZ70",
            Name = "简体中文本地体验包",
            Description = "简体中文语言体验包",
            Category = "系统自带",
            Source = "msstore"
        };
        span[22] = new WingetPackage
        {
            Id = "9N8MHTPHNGVV",
            Name = "Windows 高级设置",
            Description = "Windows 高级系统设置",
            Category = "系统自带",
            Source = "msstore"
        };
        span[23] = new WingetPackage
        {
            Id = "9N0DX20HK701",
            Name = "Windows Terminal",
            Description = "新一代终端模拟器",
            Category = "系统自带",
            Source = "msstore"
        };
        span[24] = new WingetPackage
        {
            Id = "9NTSNMSVCB5L",
            Name = "ScreenBox",
            Description = "屏幕截图与录制工具",
            Category = "工具增强",
            Source = "msstore"
        };
        span[25] = new WingetPackage
        {
            Id = "9N36PPMP8S23",
            Name = "Nahimic",
            Description = "Nahimic 音频增强",
            Category = "工具增强",
            Source = "msstore"
        };
        span[26] = new WingetPackage
        {
            Id = "9N7VHQ989BB7",
            Name = "拾光壁纸",
            Description = "动态壁纸应用",
            Category = "工具增强",
            Source = "msstore"
        };
        span[27] = new WingetPackage
        {
            Id = "9PCWJKS4JSN1",
            Name = "Killer Control Center",
            Description = "Killer 网卡控制中心",
            Category = "工具增强",
            Source = "msstore"
        };
        span[28] = new WingetPackage
        {
            Id = "9NF8H0H7WMLT",
            Name = "Nvidia 控制面板",
            Description = "NVIDIA 显卡控制面板",
            Category = "工具增强",
            Source = "msstore"
        };
        span[29] = new WingetPackage
        {
            Id = "9PDH8M7HF2SQ",
            Name = "RyTuneX",
            Description = "系统优化工具",
            Category = "工具增强",
            Source = "msstore"
        };
        span[30] = new WingetPackage
        {
            Id = "9PLM9XGG6VKS",
            Name = "CodeX",
            Description = "代码开发工具",
            Category = "工具增强",
            Source = "msstore"
        };
        span[31] = new WingetPackage
        {
            Id = "9N5TDP8VCMHS",
            Name = "Web 媒体扩展",
            Description = "Web 媒体格式解码扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        span[32] = new WingetPackage
        {
            Id = "9MVZQVXJBQ9V",
            Name = "AV1 视频扩展",
            Description = "AV1 视频解码扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        span[33] = new WingetPackage
        {
            Id = "9PB0TRCNRHFX",
            Name = "AVC 编码器视频扩展",
            Description = "AVC 编码器扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        span[34] = new WingetPackage
        {
            Id = "9PMMSR1CGPWG",
            Name = "HEIF 图像扩展",
            Description = "HEIF 图像格式扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        span[35] = new WingetPackage
        {
            Id = "9PG2DK419DRG",
            Name = "WebP 映像扩展",
            Description = "WebP 图像格式扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        span[36] = new WingetPackage
        {
            Id = "9N4D0MSMP0PT",
            Name = "VP9 视频扩展",
            Description = "VP9 视频解码扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        span[37] = new WingetPackage
        {
            Id = "9NCTDW2W1BH8",
            Name = "原始图像扩展",
            Description = "RAW 原始图像格式扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        span[38] = new WingetPackage
        {
            Id = "9N95Q1ZZPMH4",
            Name = "MPEG2 视频扩展",
            Description = "MPEG-2 视频解码扩展",
            Category = "解码扩展",
            Source = "msstore"
        };
        return list;
    }

    private static List<ManualSoftware> DefaultManualSoftware()
    {
        int num = 1;
        var list = new List<ManualSoftware>(num);
        CollectionsMarshal.SetCount(list, num);
        CollectionsMarshal.AsSpan(list)[0] = new ManualSoftware
        {
            Name = "RytuneX",
            Description = "系统优化工具：导入优化 Reg 文件、调整系统配置",
            Category = "系统优化",
            Icon = "⚙\ufe0f",
            DownloadUrl = "https://example.com/rytunex"
        };
        return list;
    }

    private List<ManualSoftware> DefaultDriverSoftware()
    {
        int num = 2;
        var list = new List<ManualSoftware>(num);
        CollectionsMarshal.SetCount(list, num);
        Span<ManualSoftware> span = CollectionsMarshal.AsSpan(list);
        span[0] = new ManualSoftware
        {
            Name = "硬件驱动包",
            Description = "显卡/声卡/主板等硬件驱动（建议使用 " + _driverRoot + " 目录下的本地驱动包）",
            Category = "驱动",
            Icon = "\ud83d\udda5\ufe0f",
            LocalInstallerPath = _driverRoot
        };
        span[1] = new ManualSoftware
        {
            Name = "系统优化配置",
            Description = "导入优化 Reg 文件、调整系统配置（建议存放于 " + _driverRoot + "\\Reg）",
            Category = "系统优化",
            Icon = "⚙\ufe0f",
            LocalInstallerPath = _driverRoot + "\\Reg"
        };
        return list;
    }
}
