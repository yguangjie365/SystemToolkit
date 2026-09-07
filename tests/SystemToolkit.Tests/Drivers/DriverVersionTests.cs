using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 驱动版本数值段比较守卫：杜绝字典序误判（"8.10" &gt; "8.9"、"31.0.101.2115" 数值可比）。
/// 升级判定直接采信 winget/pnputil 的结论，本比较器只用于"旧版本"分组。
/// </summary>
public class DriverVersionTests
{
    [Theory]
    [InlineData("8.10", "8.9", 1)]
    [InlineData("8.9", "8.10", -1)]
    [InlineData("31.0.101.2115", "31.0.101.2115", 0)]
    [InlineData("560.94", "552.44", 1)]
    [InlineData("6.0.10007.1", "6.0.9999.9", 1)]
    [InlineData("10.0.26100.1", "10.0.26100", 1)]
    public void Compare_FourSegmentNumericComparison_NotLexicographic(string a, string b, int expectedSign)
    {
        int actual = DriverVersion.Compare(a, b);

        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Theory]
    [InlineData(null, "1.0.0.0", -1)]
    [InlineData("", "1.0.0.0", -1)]
    [InlineData("abc.def", "1.0.0.0", -1)]
    [InlineData(null, null, 0)]
    [InlineData("abc", "abc", 0)]
    public void Compare_NullOrInvalidValues_TreatedAsLowest(string? a, string? b, int expectedSign)
    {
        int actual = DriverVersion.Compare(a, b);

        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Fact]
    public void Compare_OverlongSegments_DoNotOverflow()
    {
        // ulong 段：Windows 驱动版本第四段最大约 65535*65536，普通 ulong 比较余量充足
        Assert.Equal(1, Math.Sign(DriverVersion.Compare("4294967295.0.0.0", "4294967294.99999.99999.99999")));
    }
}
