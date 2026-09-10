using Microsoft.Extensions.DependencyInjection;

namespace SystemToolkit.Abstractions;

/// <summary>
/// 模块契约：每个模块必须实现（02 分册 §七）。
/// 新增模块须登记 AppRoot.KnownPages 白名单、DependencyGuard 白名单、DependencyInjectionGuard 覆盖。
/// </summary>
public interface IModule
{
    /// <summary>模块标识（小写），如 "overview" / "driver"。</summary>
    string Id { get; }

    /// <summary>导航显示名（中文），如 "本机概览"。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 模块注册序号（仅元数据，各模块唯一）。
    /// <para>
    /// 🔴 <b>本属性不驱动导航顺序</b>——侧栏顺序由 Shell 的
    /// <c>MainWindow.NavGroups</c>（分组表）决定，宿主从未读取此值；
    /// 改这里不会改变界面顺序（2026-09-10 事故：音乐 9→8 / 游戏 8→9 改了但界面纹丝不动）。
    /// 目前唯一消费者是 <c>ModuleContractTests.ModuleNavigationOrders_AreUnique</c>。
    /// </para>
    /// </summary>
    int Order { get; }

    /// <summary>扩展模块（游戏/音乐）为 true，允许在设置中禁用。</summary>
    bool CanDisable { get; }

    /// <summary>向 DI 注册本模块的服务（只注册，不解析）。</summary>
    void RegisterServices(IServiceCollection services);

    /// <summary>
    /// 创建本模块的主视图（宿主导航到该模块时调用一次，之后由宿主缓存）。
    /// <para>
    /// 🔴 <b>为什么返回 <see cref="object"/> 而不是 <c>FrameworkElement</c></b>：
    /// 本工程（Abstractions）目标框架是 <c>net10.0</c>、不引用 WPF，而 WPF 类型只在
    /// <c>net10.0-windows</c> 可用。把视图类型写在契约上会强迫 Core / Infrastructure
    /// 一起切到 Windows TFM——为消一个类型转换付出架构代价不值得。
    /// 实现方（Modules 均为 net10.0-windows）返回 WPF <c>FrameworkElement</c>，
    /// 宿主直接赋给 <c>ContentControl.Content</c>，全程无需 cast。
    /// </para>
    /// <para>返回 null 表示本模块尚未落地，宿主显示「建设中」占位（见 <see cref="ModuleBase.CreateView"/>）。</para>
    /// </summary>
    /// <param name="services">已构建的服务容器（模块从中解析自己的 View）。</param>
    object? CreateView(IServiceProvider services);
}

/// <summary>
/// 可暂停 / 可恢复的页面视图模型（如概览页的实时采样）。
/// <para>
/// 宿主契约（Design/01 §3.1）：窗口失焦或切走该页时 <see cref="Pause"/>；
/// 窗口重新激活且停留在该页时 <see cref="ActivateAsync"/>。
/// 定义在 Abstractions 层，宿主无需知道具体是哪个模块需要生命周期——
/// 概览页不是"特例"，只是第一个实现者。
/// </para>
/// </summary>
public interface IPausableViewModel
{
    /// <summary>停止后台刷新（采样器 / 定时器）。可重复调用。</summary>
    void Pause();

    /// <summary>恢复后台刷新。实现须幂等（宿主可能因 Loaded / Activated 双通道触发）。</summary>
    Task ActivateAsync();
}

/// <summary>模块基类：统一元数据与空实现，模块只覆写需要的部分。</summary>
public abstract class ModuleBase : IModule
{
    /// <summary>模块标识（小写），如 "overview" / "driver"。</summary>
    public abstract string Id { get; }

    /// <summary>导航显示名（中文），如 "本机概览"。</summary>
    public abstract string DisplayName { get; }

    /// <summary>导航顺序，越小越靠前。</summary>
    public abstract int Order { get; }

    /// <summary>是否允许在设置中禁用，核心模块默认 false。</summary>
    public virtual bool CanDisable => false;

    /// <summary>向 DI 注册本模块的服务，默认不注册（只注册，不解析）。</summary>
    public virtual void RegisterServices(IServiceCollection services)
    {
    }

    /// <summary>
    /// 创建本模块主视图。默认返回 null = 尚未落地（宿主显示「建设中」占位）。
    /// <para>
    /// 🔴 已落地的模块<b>必须覆写</b>本方法并返回真实视图：默认返回 null 是为了让
    /// 占位模块（如 V0.x 未开工的音乐/设置/重装助手）不必写无用代码，
    /// 代价是"忘了覆写"会静默退化成占位页——由
    /// <c>tests/SystemToolkit.Tests/Architecture/ModuleViewContractTests.cs</c> 拦截。
    /// </para>
    /// </summary>
    public virtual object? CreateView(IServiceProvider services) => null;
}
