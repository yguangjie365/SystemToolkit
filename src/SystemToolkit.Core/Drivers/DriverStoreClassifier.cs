namespace SystemToolkit.Core.Drivers;

/// <summary>
/// Driver Store 驱动包分类器。
/// 旧版本判定——同「原始名称」分组内除最高版本外均为旧版本（RAPR 同思路）；
/// 组内比较键：版本（数值段比较）优先，日期兜底，两者皆缺视为最低。
/// FR-06 四类互斥归并（优先级：系统关键 &gt; 正在使用 &gt; 旧版本 &gt; 无设备关联），
/// 要求调用前 DeviceNames 已由 DriverDeviceMapper 填充。
/// </summary>
public static class DriverStoreClassifier
{
    /// <summary>按"原始名称"分组做新旧版本归类，可叠加 FR-06 清理类改判。</summary>
    /// <param name="packages">待分类的驱动包集合（要求 DeviceNames 已填充）。</param>
    /// <param name="applyCleanupCategories">
    /// FR-06 四类归并开关：true 时按「系统关键 &gt; 正在使用 &gt; 旧版本 &gt; 无设备关联」改判
    ///（要求 DeviceNames 已填充，DriverScanner 链满足）。默认 false 保持旧分类语义（纯版本组比较）。
    /// </param>
    public static void Classify(IEnumerable<DriverPackage> packages, bool applyCleanupCategories = false)
    {
        ArgumentNullException.ThrowIfNull(packages);

        IList<DriverPackage> list = packages as IList<DriverPackage> ?? packages.ToList();

        foreach (IGrouping<string, DriverPackage> group in list.GroupBy(
            p => string.IsNullOrEmpty(p.OriginalName) ? p.PublishedName : p.OriginalName,
            StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                DriverPackage single = group.First();
                single.State = DriverPackageState.Current;
                continue;
            }

            DriverPackage latest = group
                .OrderByDescending(p => p, DriverPackageComparer.Instance)
                .First();

            foreach (DriverPackage pkg in group)
            {
                pkg.State = ReferenceEquals(pkg, latest)
                    ? DriverPackageState.Current
                    : DriverPackageState.OldVersion;
            }
        }

        if (applyCleanupCategories)
        {
            // FR-06 四类归并：系统关键（收件箱）> 正在使用（有关联）> 旧版本 > 无设备关联
            foreach (DriverPackage pkg in list)
            {
                if (!pkg.IsThirdParty)
                {
                    pkg.State = DriverPackageState.SystemCritical;
                }
                else if (pkg.State == DriverPackageState.Current && pkg.DeviceNames.Count == 0)
                {
                    pkg.State = DriverPackageState.NoDeviceAssociation;
                }
            }
        }

        // 🔴 启动关键防线（最高优先级、无条件执行）：第三方启动关键驱动（存储控制器/启动类）
        // 删除可致蓝屏或无法开机——无论何种分类结果一律改判系统关键，UI 禁止勾选（RAPR 默认排除 BootCritical 同思路）
        foreach (DriverPackage pkg in list)
        {
            if (pkg.IsBootCritical)
            {
                pkg.State = DriverPackageState.SystemCritical;
            }
        }
    }
}

/// <summary>同组内取最新：版本（数值）→ 日期 → 原顺序稳定。</summary>
internal sealed class DriverPackageComparer : IComparer<DriverPackage>
{
    public static readonly DriverPackageComparer Instance = new();

    public int Compare(DriverPackage? x, DriverPackage? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int byVersion = DriverVersion.Compare(x.Version, y.Version);
        if (byVersion != 0)
        {
            return byVersion;
        }

        return (x.Date ?? DateTime.MinValue).CompareTo(y.Date ?? DateTime.MinValue);
    }
}
