using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Tests;

/// <summary>
/// 磁盘空间预检工具单测。
/// <para>
/// 立规背景：该工具的设计要点是「**无法判定 ≠ 充足**」——曾经任何异常都 <c>return true</c>，
/// 让备份到网络共享/未就绪驱动器时的预检形同虚设。本次（2026-09-13）为文件互传补预检时
/// 又发现一个同类漏洞：<c>neededBytes</c> 可能来自**对端声明**，荒谬大值会让
/// <c>neededBytes + margin</c> 溢出成负数 → 「剩余空间 &gt; 负数」恒真 = 又变成"永远充足"。
/// </para>
/// </summary>
public class DiskSpaceUtilTests
{
    /// <summary>取本机一个有实际容量的路径（临时目录所在卷）。</summary>
    private static string TempRoot => Path.GetTempPath();

    [Fact]
    public void Check_SmallNeed_IsEnough()
        => Assert.Equal(DiskSpaceCheck.Enough, DiskSpaceUtil.Check(TempRoot, 1024));

    [Fact]
    public void Check_AbsurdlyLargeNeed_IsInsufficient_NotOverflowedToEnough()
    {
        // long.MaxValue + 128MB 会溢出成负数；修复前这条会返回 Enough（预检失效）
        Assert.Equal(DiskSpaceCheck.Insufficient, DiskSpaceUtil.Check(TempRoot, long.MaxValue));
        Assert.Equal(DiskSpaceCheck.Insufficient, DiskSpaceUtil.Check(TempRoot, long.MaxValue - 1024));
    }

    [Fact]
    public void Check_NegativeOrZeroNeed_DoesNotBecomeEternallyEnough()
    {
        // 负数按 0 处理：required 只剩 margin，仍是"有意义的正数"，不会退化成恒真比较
        Assert.NotEqual(DiskSpaceCheck.Unknown, DiskSpaceUtil.Check(TempRoot, -1));
        Assert.Equal(DiskSpaceCheck.Enough, DiskSpaceUtil.Check(TempRoot, 0));
    }

    [Fact]
    public void Check_UnresolvablePath_IsUnknown_NotEnough()
    {
        // 🔴 Unknown 与 Enough 必须严格区分：调用方不得把它当放行信号
        Assert.Equal(DiskSpaceCheck.Unknown, DiskSpaceUtil.Check(string.Empty, 1));
    }

    [Fact]
    public void HasEnoughSpace_CompatibilityWrapper_OnlyFalseWhenDefinitelyInsufficient()
    {
        Assert.False(DiskSpaceUtil.HasEnoughSpace(TempRoot, long.MaxValue));
        Assert.True(DiskSpaceUtil.HasEnoughSpace(TempRoot, 1024));
    }
}
