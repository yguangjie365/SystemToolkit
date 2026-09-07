namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 管理员权限检测抽象。接口化以便 ViewModel 测试注入 fake
/// （非提权降级是网络模块的核心交互分支，必须可测）。
/// </summary>
public interface IElevationProvider
{
    /// <summary>当前进程是否以管理员运行。</summary>
    bool IsElevated { get; }
}
