using System.Runtime.Versioning;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// 备份目录分类整理器（对齐 RAPR GetDriversBackupFolderName 命名法）：
/// 把导出暂存目录中的驱动包移动为「&lt;设备类别&gt;\&lt;设备名[_版本]&gt;」，无设备关联的归入「无设备关联」组。
/// 同名冲突追加发布名去扩展名，仍冲突追加序号。
/// v2（2026-09-05，D-3 修复）：不再按目录名前缀猜测匹配包——调用方传入「暂存内容目录 ↔ 包元数据」精确对位
/// （pnputil 导出目录名 = 原始 INF 名，前缀猜测对 oem1/oem10 类前缀有错配风险）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverBackupOrganizer
{
    private const int MaxCategoryLength = 50;
    private const int MaxNameLength = 70;
    private const string StagePrefix = "_stage_";

    /// <summary>导出暂存目录：<c>_stage_&lt;发布名去扩展名&gt;</c>。
    /// 按包隔离——pnputil 导出目录名取原始 INF 名，同名原始 INF 的多版本包会相互覆盖。</summary>
    public static string StageDirFor(string driversRoot, string publishedName) =>
        Path.Combine(driversRoot, StagePrefix + Path.GetFileNameWithoutExtension(publishedName));

    /// <summary>整理暂存内容到分类目录。entries =（暂存目录内的包内容目录, 包元数据）精确对位。
    /// 返回 (移动数, 跳过数——目录缺失或移动失败)；移动后清理空的 _stage_* 残壳。</summary>
    public static (int Moved, int Skipped) OrganizeByCategory(
        string driversRoot,
        IReadOnlyList<(string PackageDir, DriverPackage Package)> entries,
        IReadOnlyDictionary<string, List<DriverDeviceMapper.DeviceBinding>> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driversRoot);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(bindings);

        int moved = 0, skipped = 0;
        foreach ((string packageDir, DriverPackage pkg) in entries)
        {
            if (!Directory.Exists(packageDir))
            {
                skipped++;
                continue;
            }

            // 设备名：绑定明细首个设备（存在关联时）；无关联 → 「无设备关联」组
            string? deviceName = null;
            if (TryGetBindings(bindings, pkg, out List<DriverDeviceMapper.DeviceBinding>? list) && list!.Count > 0)
            {
                deviceName = list[0].DeviceName;
            }

            bool withoutDevice = string.IsNullOrWhiteSpace(deviceName);
            string category = Sanitize(
                string.IsNullOrWhiteSpace(pkg.ClassName) ? "未知类别" : pkg.ClassName!,
                MaxCategoryLength);
            string baseName = Sanitize(
                withoutDevice
                    ? (string.IsNullOrWhiteSpace(pkg.OriginalName)
                        ? Path.GetFileNameWithoutExtension(pkg.PublishedName)
                        : Path.GetFileNameWithoutExtension(pkg.OriginalName))
                    : deviceName!,
                MaxNameLength);
            if (!string.IsNullOrWhiteSpace(pkg.Version))
            {
                baseName += $"_{Sanitize(pkg.Version, 40)}"; // 审查 Y10：Version 也须消毒，防分隔符/../越界
            }

            string groupDir = Path.Combine(driversRoot, category);
            Directory.CreateDirectory(groupDir);

            string target = Path.Combine(groupDir, baseName);
            if (Directory.Exists(target))
            {
                // 同名冲突：追加发布名去扩展名；仍冲突追加序号
                string publishedBase = Path.GetFileNameWithoutExtension(pkg.PublishedName);
                string withPublished = Path.Combine(groupDir, $"{baseName}_{Sanitize(publishedBase, 40)}");
                if (!Directory.Exists(withPublished))
                {
                    target = withPublished;
                }
                else
                {
                    int seq = 2;
                    while (Directory.Exists(Path.Combine(groupDir, $"{baseName}_{publishedBase}_{seq}")))
                    {
                        seq++;
                    }

                    target = Path.Combine(groupDir, $"{baseName}_{publishedBase}_{seq}");
                }
            }

            try
            {
                Directory.Move(packageDir, target);
                moved++;
            }
            catch
            {
                // 单目录移动失败（占用等）不阻断其余；内容留在暂存目录内仍可见
                skipped++;
            }
        }

        CleanupStageRemnants(driversRoot);
        return (moved, skipped);
    }

    /// <summary>移走内容后清理空的 _stage_* 壳目录（非空保留——内容移动失败时用户仍可见）。</summary>
    private static void CleanupStageRemnants(string driversRoot)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(driversRoot))
            {
                if (!Path.GetFileName(dir).StartsWith(StagePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch
                {
                    // 残壳清理尽力而为
                }
            }
        }
        catch
        {
            // driversRoot 不可读：无残壳可清
        }
    }

    private static bool TryGetBindings(
        IReadOnlyDictionary<string, List<DriverDeviceMapper.DeviceBinding>> bindings,
        DriverPackage pkg,
        out List<DriverDeviceMapper.DeviceBinding>? list)
    {
        foreach (string key in new[]
                 {
                     pkg.PublishedName,
                     pkg.OriginalName,
                     Path.GetFileNameWithoutExtension(pkg.OriginalName),
                 })
        {
            if (!string.IsNullOrWhiteSpace(key) && bindings.TryGetValue(key, out list))
            {
                return true;
            }
        }

        list = null;
        return false;
    }

    /// <summary>Windows 目录名非法字符替换为下划线 + 长度截断。</summary>
    public static string Sanitize(string name, int maxLength)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.TrimEnd('.', ' ');
        return name.Length <= maxLength ? name : name[..maxLength].TrimEnd('.', ' ');
    }
}
