namespace SystemToolkit.Modules.AppManager;

/// <summary>环境档案卡片（MVP：按分类聚合清单）。</summary>
public sealed class EnvironmentArchiveVm
{
    public EnvironmentArchiveVm(string name, int count, IReadOnlyList<WingetPackageVm> packages)
    {
        Name = name;
        Count = count;
        Packages = packages;
    }

    public string Name { get; }

    public int Count { get; }

    public IReadOnlyList<WingetPackageVm> Packages { get; }
}
