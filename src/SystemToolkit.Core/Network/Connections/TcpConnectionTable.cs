using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace SystemToolkit.Core.Network.Connections;

/// <summary>
/// 本机 IPv4 TCP 端点表：<c>GetExtendedTcpTable</c> 的封装，用于回答「哪个进程在占哪些端口」。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **全部 Win32 互操作由 CsWin32 从官方 metadata 生成**（本仓《架构与依赖规范》§9.5、
/// AGENTS §二·五）：本类只负责"取缓冲区 + 解析"，签名、结构体布局、枚举值一个都不手写。
/// 本工程是首个引入该生成器的**产品工程**，登记见 ADR-002 §5.3 与 NOTICE.md §4.3。
/// </para>
/// <para>
/// **免提权**：<c>GetExtendedTcpTable</c> 不需要管理员权限（与 NET-6 的 ARP 表、NET-7 的路由表不同），
/// 因此本能力**不新增提权通道**。
/// </para>
/// <para>
/// **本批只做 IPv4 TCP**：IPv6（<c>MIB_TCP6TABLE_OWNER_PID</c>）与 UDP（<c>MIB_UDPTABLE_OWNER_PID</c>）
/// 见 TASKS.md OL-B7 备注，按后续批次补。
/// </para>
/// <para>
/// **缓冲区由调用方分配**，故不需要 <c>FreeMibTable</c>（那是给 GetIfTable2 这类系统分配的表用的）。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public sealed class TcpConnectionTable
{
    /// <summary>Win32 成功返回 0（NO_ERROR）。</summary>
    private const uint NoError = 0;

    /// <summary>
    /// 读取本机全部 IPv4 TCP 端点（监听 + 已连接）；任何失败折叠为空列表，**绝不抛出**
    /// （与概览页其它采样器同一约定：瞬时失败不该把界面打崩）。
    /// </summary>
    public IReadOnlyList<TcpConnectionRow> Read()
    {
        try
        {
            uint size = 0;
            // 第一趟只问大小：返回 ERROR_INSUFFICIENT_BUFFER 是**预期**行为，size 会被填成所需字节数
            _ = PInvoke.GetExtendedTcpTable(
                Span<byte>.Empty,
                ref size,
                (BOOL)0,
                (uint)ADDRESS_FAMILY.AF_INET,
                TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL,
                0);

            if (size == 0)
            {
                return Array.Empty<TcpConnectionRow>();
            }

            byte[] buffer = new byte[size];
            uint result = PInvoke.GetExtendedTcpTable(
                buffer,
                ref size,
                (BOOL)0,
                (uint)ADDRESS_FAMILY.AF_INET,
                TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL,
                0);

            if (result != NoError || size == 0)
            {
                return Array.Empty<TcpConnectionRow>();
            }

            return Parse(buffer.AsSpan(0, (int)Math.Min(size, (uint)buffer.Length)));
        }
        catch
        {
            // 表在两趟调用之间变化等情况：本拍无数据，下一拍刷新即可
            return Array.Empty<TcpConnectionRow>();
        }
    }

    /// <summary>
    /// 表头之后第一行的字节偏移（即 <c>dwNumEntries</c> 字段之后）。
    /// 🔴 由 CsWin32 生成的布局给出，**不手算**。
    /// </summary>
    internal static int RowOffset =>
        (int)Marshal.OffsetOf<MIB_TCPTABLE_OWNER_PID>(nameof(MIB_TCPTABLE_OWNER_PID.table));

    /// <summary>
    /// 一行 <c>MIB_TCPROW_OWNER_PID</c> 的字节步长。
    /// 🔴 用生成器给出的变长数组容量反推（<c>SizeOf(2) − SizeOf(1)</c>），**不写 <c>sizeof</c>**：
    /// MSDN 明确提示该表在表头与行之间、以及行之间**可能存在对齐填充**，手算步长必然埋雷。
    /// </summary>
    internal static int RowStride => MIB_TCPTABLE_OWNER_PID.SizeOf(2) - MIB_TCPTABLE_OWNER_PID.SizeOf(1);

    /// <summary>
    /// 解析 <c>GetExtendedTcpTable</c> 返回的原始缓冲区。
    /// <para>
    /// 🔴 行数取「表头声明值」与「缓冲区实际放得下的行数」中的**较小者**：表头的
    /// <c>dwNumEntries</c> 不可全信（表可能在两趟调用之间被截断或变化），
    /// 盲目按它遍历会越界读到垃圾数据。
    /// </para>
    /// </summary>
    /// <param name="table">原始缓冲区。</param>
    internal static IReadOnlyList<TcpConnectionRow> Parse(ReadOnlySpan<byte> table)
    {
        int stride = RowStride;
        if (stride <= 0 || table.Length < RowOffset + stride)
        {
            // 连一行都放不下：空表或缓冲区被截断
            return Array.Empty<TcpConnectionRow>();
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(table);
        int capacity = (table.Length - RowOffset) / stride;
        int rows = (int)Math.Min(declared, (uint)capacity);

        var result = new List<TcpConnectionRow>(rows);
        for (int i = 0; i < rows; i++)
        {
            MIB_TCPROW_OWNER_PID row = MemoryMarshal.Read<MIB_TCPROW_OWNER_PID>(
                table[(RowOffset + (i * stride))..]);
            result.Add(new TcpConnectionRow(
                ReadEndpoint(row.dwLocalAddr, row.dwLocalPort),
                ReadEndpoint(row.dwRemoteAddr, row.dwRemotePort),
                MapState(row.dwState),
                (int)row.dwOwningPid));
        }

        return result;
    }

    /// <summary>
    /// 把一个「网络字节序的 DWORD」读成端点。
    /// <para>
    /// 该 DWORD 在结构体里的内存布局与 <c>in_addr</c> / 网络序端口完全一致，所以这里
    /// **直接按内存字节读**（地址取 4 字节、端口按大端读前 2 字节），而不是手写移位与字节反转 ——
    /// 让字节序问题在类型层面无处藏身。
    /// </para>
    /// </summary>
    private static TcpEndpoint ReadEndpoint(uint rawAddress, uint rawPort)
    {
        ReadOnlySpan<byte> addressBytes =
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref rawAddress, 1));
        ReadOnlySpan<byte> portBytes =
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref rawPort, 1));
        return new TcpEndpoint(new IPAddress(addressBytes), BinaryPrimitives.ReadUInt16BigEndian(portBytes));
    }

    /// <summary>把生成的 <c>MIB_TCP_STATE</c> 映射为本仓公开枚举（映射表即唯一判据，不外散）。</summary>
    private static TcpConnectionState MapState(MIB_TCP_STATE state) => state switch
    {
        MIB_TCP_STATE.MIB_TCP_STATE_CLOSED => TcpConnectionState.Closed,
        MIB_TCP_STATE.MIB_TCP_STATE_LISTEN => TcpConnectionState.Listen,
        MIB_TCP_STATE.MIB_TCP_STATE_SYN_SENT => TcpConnectionState.SynSent,
        MIB_TCP_STATE.MIB_TCP_STATE_SYN_RCVD => TcpConnectionState.SynReceived,
        MIB_TCP_STATE.MIB_TCP_STATE_ESTAB => TcpConnectionState.Established,
        MIB_TCP_STATE.MIB_TCP_STATE_FIN_WAIT1 => TcpConnectionState.FinWait1,
        MIB_TCP_STATE.MIB_TCP_STATE_FIN_WAIT2 => TcpConnectionState.FinWait2,
        MIB_TCP_STATE.MIB_TCP_STATE_CLOSE_WAIT => TcpConnectionState.CloseWait,
        MIB_TCP_STATE.MIB_TCP_STATE_CLOSING => TcpConnectionState.Closing,
        MIB_TCP_STATE.MIB_TCP_STATE_LAST_ACK => TcpConnectionState.LastAck,
        MIB_TCP_STATE.MIB_TCP_STATE_TIME_WAIT => TcpConnectionState.TimeWait,
        MIB_TCP_STATE.MIB_TCP_STATE_DELETE_TCB => TcpConnectionState.DeleteTcb,
        _ => TcpConnectionState.Unknown,
    };
}
