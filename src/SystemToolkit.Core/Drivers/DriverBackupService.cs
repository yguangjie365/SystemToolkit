using System.Runtime.Versioning;

namespace SystemToolkit.Core.Drivers;

/// <summary>一次备份的结果：成功导出的包元数据、未发现导出产物的包名、整理移动/跳过计数、pnputil 原始结果。</summary>
public sealed record DriverBackupResult(
    IReadOnlyList<DriverPackage> Exported,
    IReadOnlyList<string> Missing,
    int MovedDirs,
    int SkippedDirs,
    DriverRunResult Raw);

/// <summary>
/// 驱动备份编排（Design/03 §3 DriverBackupService 落位）：
/// 提权批量导出（按包独立暂存）→ 实证式核对暂存产物 → 设备绑定采集 → RAPR 式分类整理 → 三件套落盘。
/// 🔴 实证式校验（RAPR 经验：pnputil 退出码不可全信）：以暂存目录实际产物为准——
/// 部分失败也保留成功部分清单，缺失包显式上报由调用方告警。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriverBackupService
{
    private readonly IPnpUtilClient _pnputil;
    private readonly Func<IReadOnlyDictionary<string, List<DriverDeviceMapper.DeviceBinding>>> _bindingsProvider;

    /// <summary>注入 pnputil 客户端与设备绑定采集源（测试可替换）；缺省走真实注册表采集。</summary>
    public DriverBackupService(
        IPnpUtilClient pnputil,
        Func<IReadOnlyDictionary<string, List<DriverDeviceMapper.DeviceBinding>>>? bindingsProvider = null)
    {
        ArgumentNullException.ThrowIfNull(pnputil);
        _pnputil = pnputil;
        // 绑定源注入点（03 测试规范 §四：外部依赖必须注入）——默认走真实注册表采集
        _bindingsProvider = bindingsProvider ?? DriverDeviceMapper.CollectDeviceBindings;
    }

    /// <summary>执行备份。knownPackages = 当前扫描所得包元数据（按发布名对位）；
    /// destDir = 调用方确定的目标目录（含时间戳），本服务创建其下 Drivers\。</summary>
    public async Task<DriverBackupResult> BackupAsync(
        IReadOnlyList<string> publishedNames,
        IReadOnlyList<DriverPackage> knownPackages,
        string destDir,
        DriverBackupScope scope,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);
        ArgumentNullException.ThrowIfNull(publishedNames);
        ArgumentNullException.ThrowIfNull(knownPackages);

        string driversRoot = Path.Combine(destDir, "Drivers");
        Directory.CreateDirectory(driversRoot); // pnputil /export-driver 契约：目标目录须已存在

        progress?.Report($"正在导出 {publishedNames.Count} 个驱动包（已弹出 UAC，请确认）…");
        DriverRunResult raw = await _pnputil.ExportManyAsync(publishedNames, driversRoot, ct).ConfigureAwait(false);

        // 实证式核对：暂存目录内层目录 = pnputil 实际产物（其目录名取原始 INF 名）
        var entries = new List<(string PackageDir, DriverPackage Package)>();
        var exported = new List<DriverPackage>();
        var missing = new List<string>();
        foreach (string name in publishedNames)
        {
            DriverPackage? pkg = knownPackages.FirstOrDefault(
                p => p.PublishedName.Equals(name, StringComparison.OrdinalIgnoreCase));
            string stageDir = DriverBackupOrganizer.StageDirFor(driversRoot, name);
            string? contentDir = Directory.Exists(stageDir)
                ? Directory.GetDirectories(stageDir).FirstOrDefault()
                : null;
            if (contentDir is null || pkg is null)
            {
                missing.Add(name);
                continue;
            }

            entries.Add((contentDir, pkg));
            exported.Add(pkg);
        }

        if (exported.Count == 0)
        {
            return new DriverBackupResult(exported, missing, 0, 0, raw);
        }

        progress?.Report("正在采集设备绑定并按设备类别整理目录…");
        IReadOnlyDictionary<string, List<DriverDeviceMapper.DeviceBinding>> bindings =
            await Task.Run(_bindingsProvider, ct).ConfigureAwait(false);
        (int moved, int skippedDirs) = await Task.Run(
            () => DriverBackupOrganizer.OrganizeByCategory(driversRoot, entries, bindings), ct).ConfigureAwait(false);

        progress?.Report("正在生成备份清单与校验文件…");
        await Task.Run(
            () => DriverBackupManifestWriter.Write(destDir, scope, exported, bindings), ct).ConfigureAwait(false);

        return new DriverBackupResult(exported, missing, moved, skippedDirs, raw);
    }
}
