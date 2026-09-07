using System.Text.Json.Serialization;

namespace SystemToolkit.Core.FileTransfer.Services.Protocol;

/// <summary>
/// UDP 广播包格式：设备定期广播此消息宣告自身存在。
/// 接收方据此更新设备列表与在线状态。
/// </summary>
public sealed class DeviceAnnouncement
{
    /// <summary>协议魔数（快速过滤非本协议的 UDP 包）。</summary>
    public const string Magic = "STK-FT/1.0";

    /// <summary>协议标识。</summary>
    public string M { get; init; } = Magic;

    /// <summary>设备 ID。</summary>
    public string Did { get; init; } = string.Empty;

    /// <summary>设备名称。</summary>
    public string Dn { get; init; } = string.Empty;

    /// <summary>TCP 传输端口。</summary>
    public int Tp { get; init; }

    /// <summary>Web 服务端口。</summary>
    public int Wp { get; init; }

    /// <summary>消息时间戳（Unix 毫秒）。</summary>
    public long Ts { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>包有效性（魔数一致才有效，非本协议包直接丢弃）。</summary>
    [JsonIgnore]
    public bool IsValid => string.Equals(M, Magic, StringComparison.Ordinal);
}
