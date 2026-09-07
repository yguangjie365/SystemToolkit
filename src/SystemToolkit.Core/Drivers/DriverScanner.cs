using System.Runtime.Versioning;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// 驱动扫描编排：pnputil XML 枚举（含官方设备关联/签名/文件数）→ 旧版本分类 → 类名 GUID 中文化。
/// v2.0（2026-09-05）：设备关联改用 pnputil /devices 官方数据（XML 自带，替代注册表遍历）；
/// 类别翻译严格按 ClassGuid 查表（不再从 ClassName 猜 GUID）。
/// 类别翻译三级：方案甲（提权 SetupAPI，中文准）→ 注册表 Control\Class 默认名 → INF [Version].Class。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriverScanner
{
    private readonly IPnpUtilClient _pnputil;
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<Dictionary<string, string>?>>? _queryClassNames;
    private readonly Func<string, bool> _isBootCritical;

    /// <summary>注入 pnputil 客户端、类名查询与启动关键判定（测试可替换）；缺省走 SetupAPI/注册表查表。</summary>
    public DriverScanner(
        IPnpUtilClient pnputil,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<Dictionary<string, string>?>>? queryClassNames = null,
        Func<string, bool>? isBootCritical = null)
    {
        _pnputil = pnputil;
        _queryClassNames = queryClassNames;
        // 启动关键判定注入点（审查 L16：03 测试规范 §四——互操作外部依赖必须可替换）
        _isBootCritical = isBootCritical ?? DriverBootCriticalQuerier.IsBootCriticalClass;
    }

    /// <summary>全量扫描（枚举 + 类名翻译，亚秒到秒级）；进度经 report 回调。</summary>
    public async Task<IReadOnlyList<DriverPackage>> ScanAsync(
        CancellationToken ct = default, IProgress<string>? progress = null)
    {
        IReadOnlyList<DriverPackage> packages = await _pnputil.EnumDriversAsync(ct).ConfigureAwait(false);
        progress?.Report($"已枚举 {packages.Count} 个驱动包，正在翻译类别…");

        // 兜底：文本回退路径（老系统无 XML）没有官方设备关联，走注册表 Mapper 补
        if (packages.Count > 0 && packages.All(p => p.DeviceNames.Count == 0))
        {
            Dictionary<string, List<string>> deviceMap = await Task.Run(DriverDeviceMapper.BuildMap, ct)
                .ConfigureAwait(false);
            foreach (DriverPackage pkg in packages)
            {
                if (deviceMap.TryGetValue(pkg.PublishedName, out List<string>? names)
                    || deviceMap.TryGetValue(pkg.OriginalName, out names))
                {
                    pkg.DeviceNames = names;
                }
            }
        }

        // 🔴 启动关键标记（RAPR BootCritical 防线，类属性级）：存储/启动类驱动删除可致蓝屏或无法开机，
        // 分类器将强制按系统关键处理（UI 禁选）。查询失败按非启动关键处理，不阻断扫描主流程。
        var classGuids = packages
            .Select(p => p.ClassGuid)
            .Where(g => !string.IsNullOrWhiteSpace(g) && g.Contains('{'))
            .Select(g => g!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (classGuids.Count > 0)
        {
            HashSet<string> bootCritical = await Task.Run(
                () => classGuids.Where(_isBootCritical)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                ct).ConfigureAwait(false);
            foreach (DriverPackage pkg in packages)
            {
                if (!string.IsNullOrWhiteSpace(pkg.ClassGuid)
                    && bootCritical.Contains(pkg.ClassGuid.Trim()))
                {
                    pkg.IsBootCritical = true;
                }
            }
        }

        // 方案甲：收集所有 ClassGuid（严格分离：GUID 只来自 ClassGuid 字段）→ 一次提权批量翻译
        Dictionary<string, string>? translatedByGuid = null;
        if (_queryClassNames is not null)
        {
            var guids = packages
                .Select(p => p.ClassGuid)
                .Where(g => !string.IsNullOrWhiteSpace(g) && g.Contains('{'))
                .Select(g => g!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (guids.Count > 0)
            {
                progress?.Report($"正在读取 {guids.Count} 个设备类中文名（首次需确认 UAC）…");
                translatedByGuid = await _queryClassNames(guids, ct).ConfigureAwait(false);
            }
        }

        foreach (DriverPackage pkg in packages)
        {
            ct.ThrowIfCancellationRequested();

            // 类别名中文化三级链：①方案甲翻译表（ClassGuid 查）→ ②注册表默认名 → ③INF [Version].Class
            // 设备关联（DeviceNames）已由 XML 源填充；文本回退路径下由旧逻辑（注册表）在 Mapper 中补
            string? resolved = null;
            if (!string.IsNullOrWhiteSpace(pkg.ClassGuid) && translatedByGuid is not null
                && translatedByGuid.TryGetValue(pkg.ClassGuid.Trim(), out string? viaGuid))
            {
                resolved = viaGuid;
            }

            if (resolved is null)
            {
                resolved = DriverClassResolver.Resolve(pkg.ClassGuid);
            }

            if (resolved is null)
            {
                string? infPath = DriverClassResolver.FindStoreInfPath(
                    pkg.PublishedName,
                    string.IsNullOrEmpty(pkg.OriginalName) ? null : pkg.OriginalName);
                resolved = DriverClassResolver.ResolveFromInf(infPath);
            }

            if (resolved is not null)
            {
                pkg.ClassName = resolved;
            }
            else if (string.IsNullOrWhiteSpace(pkg.ClassName))
            {
                // 翻译失败且无类名：显示 GUID 短形式，避免空列
                pkg.ClassName = pkg.ClassGuid;
            }
        }

        return packages;
    }
}
