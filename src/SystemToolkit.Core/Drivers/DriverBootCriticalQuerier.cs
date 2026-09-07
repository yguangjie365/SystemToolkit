using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// 设备安装类「启动关键」属性查询（RAPR 三级判定的类属性级）。
/// 经 CfgMgr32 <c>CM_Get_Class_PropertyW</c> 读取设备类的 DEVPKEY_DeviceClass_BootCritical——
/// 存储控制器/启动类为 true：删除此类驱动可致蓝屏或无法开机，是清理功能的绝对排除项。
/// 常量抄自参考实现 DriverStoreExplorer（GPLv2，仅参考未复制）与 Windows 官方头文件：
/// DEVPKEY_DeviceClass_BootCritical = {6a3433f4-5626-40e8-a9b9-dbd9ecd2884b}, pid 3, DEVPROP_TYPE_BOOLEAN(0x11)。
/// 只读查询无需提权；结果进程内缓存。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverBootCriticalQuerier
{
    private const uint DevpropTypeBoolean = 0x00000011;
    private const uint CrSuccess = 0;

    private static readonly object Gate = new();
    private static Dictionary<string, bool>? _cache;

    /// <summary>DEVPKEY_DeviceClass_BootCritical（fmtid + pid）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid fmtid, uint pid)
    {
        public readonly Guid FmtId = fmtid;
        public readonly uint Pid = pid;
    }

    private static readonly DevPropKey BootCriticalKey =
        new(new Guid(0x6a3433f4, 0x5626, 0x40e8, 0xa9, 0xb9, 0xdb, 0xd9, 0xec, 0xd2, 0x88, 0x4b), 3);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Class_PropertyW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_ClassProperty(
        ref Guid classGuid,
        ref DevPropKey propertyKey,
        out uint propertyType,
        IntPtr buffer,
        ref uint bufferSize,
        uint flags);

    /// <summary>
    /// 查询设备类是否启动关键。
    /// 返回 true/false；GUID 非法或属性不存在（绝大多数类）返回 false——保守起见查询失败不阻断流程，
    /// 防线主体由分类器 + UI 禁选承担，本查询是增强项（与 RAPR ③缺省 false 同语义）。
    /// </summary>
    public static bool IsBootCriticalClass(string? classGuidText)
    {
        if (string.IsNullOrWhiteSpace(classGuidText) || !Guid.TryParse(classGuidText.Trim(), out Guid guid))
        {
            return false;
        }

        string key = guid.ToString("B");

        // 🔴 读写全部置于锁内（审查 2026-09-05 S3）：锁外 TryGetValue 与写入方竞态，
        // 扩容瞬间可抛 InvalidOperationException 或读到脏值——脏值 false 即启动关键防线被绕过。
        // 两线程同时未命中会各查一次互操作（幂等同值），属可接受的良性重复。
        lock (Gate)
        {
            if (_cache is not null && _cache.TryGetValue(key, out bool cached))
            {
                return cached;
            }
        }

        bool flag = QueryClass(guid);

        lock (Gate)
        {
            _cache ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            _cache[key] = flag;
        }

        return flag;
    }

    private static bool QueryClass(Guid classGuid)
    {
        const int BufferSize = 16; // DEVPROP_BOOLEAN 为单字节，留余量
        IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            DevPropKey propKey = BootCriticalKey;
            uint size = BufferSize;
            uint rc = CM_Get_ClassProperty(ref classGuid, ref propKey, out uint propType, buffer, ref size, 0);
            if (rc != CrSuccess || propType != DevpropTypeBoolean || size < 1)
            {
                return false; // 属性不存在/类型异常：按非启动关键处理（RAPR 缺省 false 同思路）
            }

            return Marshal.ReadByte(buffer) != 0; // DEVPROP_TRUE = 0x01
        }
        catch
        {
            return false; // 互操作异常不阻断扫描主流程
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
