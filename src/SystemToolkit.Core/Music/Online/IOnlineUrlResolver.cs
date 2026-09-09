namespace SystemToolkit.Core.Music.Online;

/// <summary>
/// 在线曲目播放地址解析器契约（OM-4）：把 <see cref="OnlineTrack"/> 解析为可播放的直链。
/// </summary>
/// <remarks>
/// <para><b>职责边界</b>：实现负责①按平台分发到对应客户端②注入该平台的登录 Cookie
/// （<see cref="IOnlineCredentialStore"/>）③音质降级链（网易客户端内置五级链
/// jymaster→hires→lossless→exhigh→standard + 试听兜底；QQ vkey 接口当前固定 standard）。
/// 返回的是<b>平台直链</b>——防盗链代理（<see cref="IAudioProxyService"/>）由调用方叠加。</para>
/// <para>实现位于 Infrastructure（依赖平台客户端），经模块 DI 注册；解析失败不抛异常，
/// 以 <see cref="OnlineSongUrlResult.Playable"/>=false + <see cref="OnlineSongUrlResult.Reason"/> 表达。</para>
/// </remarks>
public interface IOnlineUrlResolver
{
    /// <summary>
    /// 解析在线曲目的播放直链。
    /// </summary>
    /// <param name="track">在线曲目（Provider 决定走哪个平台客户端）。</param>
    /// <param name="preferredQuality">
    /// 期望音质（网易：jymaster/hires/lossless/exhigh/standard，未知值回退 exhigh；
    /// QQ：同档语义，按 RS01/F000/M800/M500/C400 模板从请求档起降级，实际可播档受登录态/VIP 限制）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    Task<OnlineSongUrlResult> ResolveAsync(OnlineTrack track, string preferredQuality, CancellationToken ct = default);
}

/// <summary>
/// 在线播放失败的处理策略（纯函数，OM-4）：原因分类 + 连续跳过上限。
/// </summary>
/// <remarks>
/// <para>规则来源：阶段二计划 §OM-4「不可播自动跳过（上限 10 次 + 原因分类）」，
/// 对照 NexBox music-store 的连续不可播上限防死循环设计。</para>
/// <para>成功起播（引擎 StateChanged=Playing）必须把连续失败计数归零——
/// 只统计<b>连续</b>失败，跳过几首后正常曲目出现即恢复信任。</para>
/// </remarks>
public static class OnlineSkipPolicy
{
    /// <summary>连续不可播跳过的上限：达到即停止自动切曲并给出用户可见汇总。</summary>
    public const int MaxConsecutiveSkips = 10;

    /// <summary>把解析结果映射为用户可读原因（🔴 不静默：每一跳都要说清为什么）。</summary>
    public static string Describe(OnlineSongUrlResult result)
    {
        if (result.Playable)
        {
            return string.Empty;
        }

        return result.Reason switch
        {
            "url_unavailable" when result.Fee == 1 => "VIP 歌曲：需要登录会员账号",
            "url_unavailable" => "无法获取播放地址（版权限制或需要登录）",
            "trial_only" => "仅提供试听片段且不可用",
            "invalid_provider" => "未知音乐来源",
            "error" => result.Message,
            _ => string.IsNullOrEmpty(result.Message) ? "未知原因" : result.Message,
        } ?? "未知原因";
    }

    /// <summary>
    /// 判断连续失败次数下是否应停止自动跳过。
    /// </summary>
    /// <param name="consecutiveFailures">当前连续失败计数（含本次）。</param>
    /// <param name="stopMessage">应停止时的用户可见汇总（否则 null）。</param>
    /// <returns>true = 达到上限应停止自动切曲。</returns>
    public static bool ShouldStop(int consecutiveFailures, out string? stopMessage)
    {
        if (consecutiveFailures < MaxConsecutiveSkips)
        {
            stopMessage = null;
            return false;
        }

        stopMessage = $"连续 {consecutiveFailures} 首在线曲目不可播放，已停止自动切曲（可能是登录态过期或网络异常，请检查后重试）";
        return true;
    }
}
