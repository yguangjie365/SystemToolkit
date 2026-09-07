using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 启动关键类属性查询守卫（D-2 修复）。互操作本体无法在测试中造数（依赖真机设备类注册状态），
/// 这里守卫纯逻辑边界：非法输入一律 false、重复查询缓存一致；真机语义由 UI 实测验收。
/// </summary>
public class DriverBootCriticalQuerierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("{4d36e968-e325-11ce-bfc1-08002be10318")] // 缺右花括号
    public void IsBootCriticalClass_InvalidInput_ReturnsFalseDoesNotThrow(string? guid)
    {
        Assert.False(DriverBootCriticalQuerier.IsBootCriticalClass(guid));
    }

    [Fact]
    public void IsBootCriticalClass_ValidGuid_RepeatQueriesWithConsistentCache()
    {
        // 显示适配器类 {4d36e968-e325-11ce-bfc1-08002be10318}——非启动关键类，真机上应返回 false；
        // 但断言只锁"确定性"（两次一致），不锁机器相关取值
        bool first = DriverBootCriticalQuerier.IsBootCriticalClass("{4d36e968-e325-11ce-bfc1-08002be10318}");
        bool second = DriverBootCriticalQuerier.IsBootCriticalClass("{4D36E968-E325-11CE-BFC1-08002BE10318}");

        Assert.Equal(first, second);
    }
}
