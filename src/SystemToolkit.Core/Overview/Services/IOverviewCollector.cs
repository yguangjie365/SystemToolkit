namespace SystemToolkit.Core.Overview.Services;

/// <summary>
/// 本机概览采集抽象。目的：让概览页 ViewModel 依赖接口而非
/// 具体实现，单元测试可注入 fake 返回构造数据，摆脱对 WMI / 注册表 / 网卡的依赖。
/// 实现为 <see cref="OverviewService"/>（同步方法，调用方应置于后台线程）。
/// </summary>
public interface IOverviewCollector
{
    /// <summary>采集一次完整的概览数据（硬件 / 系统 / 已安装程序）。</summary>
    OverviewData Collect();
}
