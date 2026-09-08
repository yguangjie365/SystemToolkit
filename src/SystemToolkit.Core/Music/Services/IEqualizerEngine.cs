namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 均衡器能力契约（OM-7，可选能力接口）：播放引擎对用户 EQ 配置的生效入口。
/// </summary>
/// <remarks>
/// <para><b>为何是独立接口而非并入 <see cref="IMusicPlaybackEngine"/></b>：EQ 是可选能力——
/// VM 以 <c>engine as IEqualizerEngine</c> 探测，缺席（如测试假引擎/未来精简引擎）时
/// EQ UI 禁用并提示，播放主功能不受影响（与「引擎可选」同一故障隔离哲学）。</para>
/// <para>实现约定：播放中调用应<b>原地重建滤波链并保持播放位置与状态</b>（不重播不跳秒）；
/// 停止态调用仅记录配置，下次建链时生效。</para>
/// </remarks>
public interface IEqualizerEngine
{
    /// <summary>应用均衡器配置（开关/preamp/10 段增益整体生效）。</summary>
    void ApplyEqualizer(EqProfile profile);
}
