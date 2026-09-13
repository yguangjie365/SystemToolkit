using System.Net;

namespace SystemToolkit.Core.Network.Connections;

/// <summary>
/// TCP 连接状态。
/// <para>
/// 🔴 这是本仓**自有**枚举，刻意不复用 CsWin32 生成的 <c>MIB_TCP_STATE</c>：后者是 <c>internal</c>，
/// 一旦出现在 public API 上就会把内部类型泄漏出去（跨程序集的调用方根本用不了）。
/// </para>
/// </summary>
public enum TcpConnectionState
{
    /// <summary>未识别（Windows 后续版本新增状态值，或读取到 0）。</summary>
    Unknown = 0,

    /// <summary>已关闭。</summary>
    Closed,

    /// <summary>监听中（等待连接）。</summary>
    Listen,

    /// <summary>已发 SYN，等待匹配。</summary>
    SynSent,

    /// <summary>已收 SYN 并回发 SYN-ACK。</summary>
    SynReceived,

    /// <summary>已建立（数据可双向传输）。</summary>
    Established,

    /// <summary>主动关闭第一段。</summary>
    FinWait1,

    /// <summary>主动关闭第二段。</summary>
    FinWait2,

    /// <summary>对端已发 FIN，本地尚未关闭。</summary>
    CloseWait,

    /// <summary>双方同时关闭。</summary>
    Closing,

    /// <summary>等待最后 ACK。</summary>
    LastAck,

    /// <summary>等待 2MSL 超时。</summary>
    TimeWait,

    /// <summary>等待删除 TCB。</summary>
    DeleteTcb,
}

/// <summary>一个 TCP 端点（地址 + 端口）。</summary>
/// <param name="Address">地址（IPv4）。</param>
/// <param name="Port">端口（主机序，0 表示未设置）。</param>
public readonly record struct TcpEndpoint(IPAddress Address, int Port)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Address}:{Port}";
}

/// <summary>一条 IPv4 TCP 端点记录（含归属进程 PID）。监听项与已连接项都在这张表里，靠 <see cref="State"/> 区分。</summary>
/// <param name="Local">本地端点（监听项的远端地址为 0.0.0.0:0）。</param>
/// <param name="Remote">远端端点。</param>
/// <param name="State">连接状态。</param>
/// <param name="Pid">发起该端点上下文绑定的进程 ID（不为 0）。</param>
public sealed record TcpConnectionRow(TcpEndpoint Local, TcpEndpoint Remote, TcpConnectionState State, int Pid)
{
    /// <summary>本地端口（列表展示常只用端口，故单独暴露）。</summary>
    public int LocalPort => Local.Port;

    /// <summary>是否处于监听态。</summary>
    public bool IsListening => State == TcpConnectionState.Listen;

    /// <summary>是否处于已建立态。</summary>
    public bool IsEstablished => State == TcpConnectionState.Established;
}
