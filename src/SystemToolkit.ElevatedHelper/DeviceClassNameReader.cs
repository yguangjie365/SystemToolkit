using System.Runtime.InteropServices;
using System.Text;

namespace SystemToolkit.ElevatedHelper;

/// <summary>
/// 设备类 GUID → 中文类名（SetupAPI SetupDiGetClassDescriptionW）。
/// 需提权读取系统设备类描述（中文系统返回中文）。方案甲：每会话首次一次 UAC，
/// 批量翻译当前所有驱动包涉及的去重 GUID，结果写回供主进程缓存。
/// 用 System.Guid 参数，由 .NET 运行时自动 marshaling 为原生 GUID。
/// </summary>
internal static class DeviceClassNameReader
{
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetClassDescriptionW(
        ref Guid classGuid,
        StringBuilder? classDescription,
        uint classDescriptionSize,
        out uint requiredSize);

    /// <summary>翻译单个 GUID（形如 {xxx...}），成功返回中文/本地类名；失败返回 null。</summary>
    public static string? GetClassName(string guidText)
    {
        if (!Guid.TryParse(guidText.Trim(), out Guid guid))
        {
            return null;
        }

        // 两段式：先取所需长度
        _ = SetupDiGetClassDescriptionW(ref guid, null, 0, out uint required);
        if (required == 0)
        {
            return null;
        }

        var buffer = new StringBuilder((int)required);
        return SetupDiGetClassDescriptionW(ref guid, buffer, required, out _)
            ? buffer.ToString()
            : null;
    }
}
