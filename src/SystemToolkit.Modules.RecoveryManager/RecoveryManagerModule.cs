using SystemToolkit.Abstractions;

namespace SystemToolkit.Modules.RecoveryManager;

/// <summary>
/// 重装助手 模块元数据。业务实现随 V0.x 里程碑落地。
/// </summary>
public sealed class RecoveryManagerModule : ModuleBase
{
    public override string Id => "recoverymanager";

    public override string DisplayName => "重装助手";

    public override int Order => 7;

    public override bool CanDisable => false;
}
