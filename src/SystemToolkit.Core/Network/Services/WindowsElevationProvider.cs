using System.Runtime.Versioning;
using System.Security.Principal;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="IElevationProvider"/> 的真实实现：WindowsPrincipal 角色判定。
/// 系统边界不做单测（VM 层经接口注入 fake 覆盖两分支）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsElevationProvider : IElevationProvider
{
    /// <inheritdoc cref="IElevationProvider.IsElevated"/>
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
