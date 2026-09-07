using SystemToolkit.Abstractions;

namespace SystemToolkit.Tests;

/// <summary>
/// 骨架冒烟：模块元数据契约完整、Id 唯一、导航顺序无冲突。
/// </summary>
public class ModuleContractTests
{
    [Fact]
    public void ModuleIds_AreUniqueAndNonEmpty()
    {
        IReadOnlyList<IModule> modules = ShellAppModules();
        var ids = modules.Select(m => m.Id).ToList();

        Assert.True(ids.All(id => !string.IsNullOrWhiteSpace(id)), "存在空 Id");
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void ModuleNavigationOrders_AreUnique()
    {
        var orders = ShellAppModules().Select(m => m.Order).ToList();
        Assert.Equal(orders.Count, orders.Distinct().Count());
    }

    [Fact]
    public void ExtendedModules_OnlyGameAndMusic_AreDisableable()
    {
        var disableable = ShellAppModules()
            .Where(m => m.CanDisable)
            .Select(m => m.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["gamemanager", "musicmanager"], disableable);
    }

    private static IReadOnlyList<IModule> ShellAppModules()
    {
        // 直接复用 Shell 的模块清单（静态纯工厂，不触碰 WPF 状态），保证清单改动即被测试覆盖
        return Shell.App.KnownModules();
    }
}
