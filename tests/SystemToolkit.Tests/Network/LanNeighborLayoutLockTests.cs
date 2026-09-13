using System.Runtime.InteropServices;
using SystemToolkit.Core.Network.LanScan;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace SystemToolkit.Tests.Network;

/// <summary>
/// Win32 <c>MIB_IPNET_ROW2</c> 布局回归锁（2026-09-13，源报告 P0-3）。
/// <para>
/// **为什么要这条锁**：NET-6 的手解偏移（<c>LanNeighborProbe</c> 里的 <c>RowSize</c> / <c>Row*Offset</c>）
/// 是两次实机事故后才定下来的 —— `GetIpNetTable2` 多塞参数恒返 87、`SendARP` 的 SrcIP 传值 vs 传引用。
/// 那套常量此前只由「行为测试 + 注释」兜底：一旦有人改歪偏移，行为测试未必变红
/// （读到的往往是同一个字段的邻居字段，症状是"MAC 偶尔错"而不是崩溃）。
/// </para>
/// <para>
/// 本锁把权威来源换成 **CsWin32 从官方 metadata 生成的结构体**（`NativeMethods.txt` 声明），
/// 逐项对比手解常量 —— 手解改歪 → 立刻变红。这也让 AGENTS §二·五「禁凭记忆写 Win32」
/// 从纪律条文变成**编译器/测试保证**。
/// </para>
/// <para>
/// 已由一次性 spike（`Tools/CsWin32Spike`，不入库）验证：0.3.333 在 .NET 10 上生成的
/// `MIB_IPNET_ROW2` 与本仓手解值**逐项一致**（88 / 0 / 28 / 40 / 72 / 76）。
/// </para>
/// </summary>
public class LanNeighborLayoutLockTests
{
    /// <summary>
    /// 结构体总大小与 5 个手解偏移，必须与 CsWin32（官方 metadata）生成值一致。
    /// <para>⚠️ 反向验证：把 <c>LanNeighborProbe</c> 里任一 <c>Row*Offset</c> 改掉 1，本用例即变红。</para>
    /// </summary>
    [Fact]
    public void MibIpNetRow2_Layout_MatchesHandWrittenConstants()
    {
        // 总大小：NeighborEntry 的逐行步长就是它，错了会整表错位
        Assert.Equal(LanNeighborProbe.RowSize, Marshal.SizeOf<MIB_IPNET_ROW2>());

        // 逐字段偏移（手解用 Marshal.Read* 硬偏移读取，所以偏移即契约）
        Assert.Equal(LanNeighborProbe.RowAddressOffset, Offset("Address"));
        Assert.Equal(LanNeighborProbe.RowIfIndexOffset, Offset("InterfaceIndex"));
        Assert.Equal(LanNeighborProbe.RowPhysAddrOffset, Offset("PhysicalAddress"));
        Assert.Equal(LanNeighborProbe.RowPhysLenOffset, Offset("PhysicalAddressLength"));
        Assert.Equal(LanNeighborProbe.RowStateOffset, Offset("State"));
    }

    /// <summary>
    /// 地址联合体内部的偏移也是手解契约：<c>SOCKADDR_INET</c> 大小 28、
    /// IPv4 地址在 <c>SOCKADDR_IN</c> 内偏移 4（<c>sin_family</c> + <c>sin_port</c>）。
    /// <para>反向验证：把 <c>SockAddrIpOffset</c> 改成 0 或 8，本用例变红。</para>
    /// </summary>
    [Fact]
    public void SockAddrInet_SizeAndIpv4Offset_MatchHandWrittenConstants()
    {
        Assert.Equal(LanNeighborProbe.SockAddrInetSize, Marshal.SizeOf<SOCKADDR_INET>());
        Assert.Equal(LanNeighborProbe.SockAddrIpOffset, OffsetIn<SOCKADDR_IN>("sin_addr"));
    }

    private static int Offset(string field) => (int)Marshal.OffsetOf<MIB_IPNET_ROW2>(field);

    private static int OffsetIn<T>(string field)
        where T : struct
        => (int)Marshal.OffsetOf<T>(field);
}
