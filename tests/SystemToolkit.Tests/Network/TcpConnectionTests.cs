using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using SystemToolkit.Core.Network.Connections;
using Windows.Win32.NetworkManagement.IpHelper;

namespace SystemToolkit.Tests.Network;

/// <summary>
/// TCP 端点表解析与进程归属聚合（B7b）。
/// <para>
/// 本批**首次把 CsWin32 引进产品工程**，因此这些用例承担两重职责：
/// ① 钉住"解析用的是**生成器给的**偏移/步长"——若有人改回手写常量，多行/单行用例立刻变红；
/// ② 钉住字节序（网络序 DWORD → 主机序地址/端口）、表头计数不可全信、状态映射、聚合稳定序。
/// </para>
/// <para>
/// 真实读取只断言"不抛 + 端口合法 + 有监听项"，**不钉具体进程名/端口**（本机连接随时在变，
/// 钉死的值就是定时炸弹）。
/// </para>
/// </summary>
public class TcpConnectionTests
{
    [Fact]
    public void Parse_SingleRow_DecodesAddressPortStateAndPid()
    {
        // 127.0.0.1:80，已建立，PID 4321。
        // 注意构造值：结构体里的这两个 DWORD 是**网络序**——
        // 127.0.0.1 的 in_addr 字节为 {7F,00,00,01}，按小端解释成 DWORD 就是 0x0100007F；
        // 端口 80 的网络序 16 位 {00,50} 存于 DWORD 低 16 位 → 0x00005000。
        byte[] table = BuildTable(Row(
            localAddress: 0x0100007Fu,
            localPort: 0x00005000u,
            state: MIB_TCP_STATE.MIB_TCP_STATE_ESTAB,
            pid: 4321u));

        IReadOnlyList<TcpConnectionRow> rows = TcpConnectionTable.Parse(table);

        Assert.Single(rows);
        Assert.Equal("127.0.0.1", rows[0].Local.Address.ToString());
        Assert.Equal(80, rows[0].Local.Port); // 🔴 网络序 → 主机序（改成小端读会得 20480）
        Assert.Equal(TcpConnectionState.Established, rows[0].State);
        Assert.True(rows[0].IsEstablished);
        Assert.Equal(4321, rows[0].Pid);
    }

    [Fact]
    public void Parse_MultipleRows_UsesGeneratedStride()
    {
        // 步长取自 CsWin32 的变长数组容量（SizeOf(2) − SizeOf(1)）；若换成手写 sizeof 或用错偏移，
        // 第二行起就会解析出垃圾 → 本用例变红
        byte[] table = BuildTable(Row(pid: 111u), Row(pid: 222u), Row(pid: 333u));

        IReadOnlyList<TcpConnectionRow> rows = TcpConnectionTable.Parse(table);

        Assert.Equal(new[] { 111, 222, 333 }, rows.Select(r => r.Pid).ToArray());
    }

    [Fact]
    public void Parse_DeclaredCountExceedsBuffer_CapsAtActualCapacity()
    {
        byte[] table = BuildTable(Row(pid: 7u));
        // 篡改表头计数成荒谬值：解析必须只按真实容量出结果（不信任 dwNumEntries、不越界）
        BinaryPrimitives.WriteUInt32LittleEndian(table, 9999u);

        IReadOnlyList<TcpConnectionRow> rows = TcpConnectionTable.Parse(table);

        Assert.Single(rows);
        Assert.Equal(7, rows[0].Pid);
    }

    [Fact]
    public void Parse_EmptyOrTruncatedBuffer_ReturnsEmptyWithoutThrowing()
    {
        byte[] full = BuildTable(Row(pid: 1u));

        Assert.Empty(TcpConnectionTable.Parse(ReadOnlySpan<byte>.Empty));
        Assert.Empty(TcpConnectionTable.Parse(full.AsSpan(0, TcpConnectionTable.RowOffset)));
        Assert.Empty(TcpConnectionTable.Parse(full.AsSpan(0, TcpConnectionTable.RowOffset + TcpConnectionTable.RowStride - 1)));
    }

    // 参数用 int 而不是 MIB_TCP_STATE：后者是 CsWin32 生成的 internal 类型，
    // public 测试方法带 internal 参数会触发 CS0051（可访问性不一致）。
    // 但取值仍来自生成器枚举（(int)MIB_TCP_STATE.X 是常量表达式），不存在"手写魔法数"。
    [Theory]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_CLOSED, TcpConnectionState.Closed)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_LISTEN, TcpConnectionState.Listen)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_SYN_SENT, TcpConnectionState.SynSent)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_SYN_RCVD, TcpConnectionState.SynReceived)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_ESTAB, TcpConnectionState.Established)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_FIN_WAIT1, TcpConnectionState.FinWait1)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_FIN_WAIT2, TcpConnectionState.FinWait2)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_CLOSE_WAIT, TcpConnectionState.CloseWait)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_CLOSING, TcpConnectionState.Closing)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_LAST_ACK, TcpConnectionState.LastAck)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_TIME_WAIT, TcpConnectionState.TimeWait)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_DELETE_TCB, TcpConnectionState.DeleteTcb)]
    [InlineData((int)MIB_TCP_STATE.MIB_TCP_STATE_RESERVED, TcpConnectionState.Unknown)]
    public void Parse_MapsEveryWin32State(int rawState, TcpConnectionState expected)
    {
        byte[] table = BuildTable(Row(state: (MIB_TCP_STATE)rawState, pid: 9u));

        Assert.Equal(expected, TcpConnectionTable.Parse(table)[0].State);
    }

    [Fact]
    public void Build_GroupsByPid_AndSkipsNonProcessPids()
    {
        TcpConnectionRow[] rows =
        [
            Connection(pid: 100, TcpConnectionState.Established, localPort: 5000),
            Connection(pid: 100, TcpConnectionState.Listen, localPort: 8080),
            Connection(pid: 200, TcpConnectionState.Listen, localPort: 445),
            Connection(pid: 0, TcpConnectionState.Listen, localPort: 999), // 保留项：不是进程
        ];

        IReadOnlyList<ProcessConnectionSummary> top = ProcessConnectionSummaryBuilder.Build(rows, 10);

        Assert.Equal(2, top.Count);
        Assert.Equal(100, top[0].Pid);
        Assert.Equal(2, top[0].Total);
        Assert.Equal(1, top[0].Established);
        Assert.Equal(1, top[0].Listening);
        Assert.Equal([8080], top[0].ListenPorts);
        Assert.Equal(200, top[1].Pid);
        Assert.Equal(1, top[1].Total);
    }

    [Fact]
    public void Build_TieOnTotal_IsStableByPid()
    {
        TcpConnectionRow[] rows =
        [
            Connection(pid: 300, TcpConnectionState.Listen, localPort: 1),
            Connection(pid: 100, TcpConnectionState.Listen, localPort: 2),
        ];

        IReadOnlyList<ProcessConnectionSummary> top = ProcessConnectionSummaryBuilder.Build(rows, 10);

        Assert.Equal(new[] { 100, 300 }, top.Select(s => s.Pid).ToArray());
    }

    [Fact]
    public void Build_TopCountZeroOrNegative_ReturnsEmpty()
    {
        TcpConnectionRow[] rows = [Connection(pid: 5, TcpConnectionState.Listen, localPort: 80)];

        Assert.Empty(ProcessConnectionSummaryBuilder.Build(rows, 0));
        Assert.Empty(ProcessConnectionSummaryBuilder.Build(rows, -3));
    }

    [Fact]
    public void Build_TopCountLimitsList()
    {
        TcpConnectionRow[] rows = Enumerable.Range(1, 20)
            .Select(i => Connection(pid: i, TcpConnectionState.Listen, localPort: i))
            .ToArray();

        Assert.Equal(5, ProcessConnectionSummaryBuilder.Build(rows, 5).Count);
    }

    [Fact]
    public void Build_ListenPorts_AreSortedAscending()
    {
        TcpConnectionRow[] rows =
        [
            Connection(pid: 42, TcpConnectionState.Listen, localPort: 8080),
            Connection(pid: 42, TcpConnectionState.Listen, localPort: 135),
            Connection(pid: 42, TcpConnectionState.Listen, localPort: 445),
        ];

        ProcessConnectionSummary summary = ProcessConnectionSummaryBuilder.Build(rows, 10)[0];

        Assert.Equal([135, 445, 8080], summary.ListenPorts);
    }

    [Fact]
    public void Read_RealSystem_ReturnsPlausibleRows()
    {
        IReadOnlyList<TcpConnectionRow> rows = new TcpConnectionTable().Read();

        // 任何在运行的 Windows 都至少有若干 TCP 监听项（RPC / DNS 客户端等）
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.IsListening);
        Assert.All(rows, r => Assert.InRange(r.Local.Port, 1, 65535));
        Assert.All(rows, r => Assert.True(r.Pid >= 0));
    }

    /// <summary>按生成器给出的表头偏移与行步长拼一个合法缓冲区（不手写 4 / 24 这类常量）。</summary>
    private static byte[] BuildTable(params MIB_TCPROW_OWNER_PID[] rows)
    {
        byte[] buffer = new byte[TcpConnectionTable.RowOffset + (rows.Length * TcpConnectionTable.RowStride)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            MemoryMarshal.Write(
                buffer.AsSpan(TcpConnectionTable.RowOffset + (i * TcpConnectionTable.RowStride)),
                in rows[i]);
        }

        return buffer;
    }

    private static MIB_TCPROW_OWNER_PID Row(
        uint localAddress = 0,
        uint localPort = 0,
        uint remoteAddress = 0,
        uint remotePort = 0,
        MIB_TCP_STATE state = MIB_TCP_STATE.MIB_TCP_STATE_LISTEN,
        uint pid = 1u) => new()
        {
            dwLocalAddr = localAddress,
            dwLocalPort = localPort,
            dwRemoteAddr = remoteAddress,
            dwRemotePort = remotePort,
            dwState = state,
            dwOwningPid = pid,
        };

    private static TcpConnectionRow Connection(int pid, TcpConnectionState state, int localPort) =>
        new(
            new TcpEndpoint(IPAddress.Loopback, localPort),
            new TcpEndpoint(IPAddress.Any, 0),
            state,
            pid);
}
