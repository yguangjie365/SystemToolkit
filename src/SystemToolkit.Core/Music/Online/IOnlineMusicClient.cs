namespace SystemToolkit.Core.Music.Online;

/// <summary>
/// 在线音乐平台客户端契约（OM-0 骨架）。
/// </summary>
/// <remarks>
/// 🔴 两平台当前签名尚未统一（网易 <c>GetSongUrlAsync(id, preferredQuality, cookie)</c> vs
/// QQ <c>GetSongUrlAsync(mid, mediaMid, quality, cookie)</c> 形态不同）——统一接口的成员签名
/// 在 OM-4 播放管线接线时随适配层一并定义，避免现在拍脑袋定错。
/// 实现类：<c>Infrastructure.Music.Online.NetEaseOnlineClient</c> /
/// <c>QQMusicOnlineClient</c>（移植自旧工程，已对齐 NexBox 签名）。
/// </remarks>
public interface IOnlineMusicClient
{
    /// <summary>所属平台。</summary>
    OnlineProvider Provider { get; }
}
