using SystemToolkit.Abstractions;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理 模块元数据（扩展模块，可在设置中禁用）。业务实现随 V0.x 里程碑落地。
/// </summary>
public sealed class MusicManagerModule : ModuleBase
{
    public override string Id => "musicmanager";

    public override string DisplayName => "音乐管理";

    public override int Order => 9;

    public override bool CanDisable => true;
}
