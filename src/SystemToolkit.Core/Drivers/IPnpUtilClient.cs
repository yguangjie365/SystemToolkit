namespace SystemToolkit.Core.Drivers;

/// <summary>pnputil 操作执行结果（语义与 WingetRunResult 对齐）。</summary>
public readonly record struct DriverRunResult(bool Success, int ExitCode, string Output)
{
    /// <summary>构造失败结果（Success=false），供短路路径统一返回。</summary>
    public static DriverRunResult Fail(int exitCode, string output) => new(false, exitCode, output);
}

/// <summary>
/// pnputil 官方机制封装（枚举 / 导出 / 删除 / 添加）。
/// 🔴 设计红线（03 模块设计 §1）：不直删 DriverStore 文件、不自研驱动格式——
/// 一律经 pnputil 子命令；删除/导出/添加为特权操作，运行期由 Elevated Helper 通道承载
/// （V0.3-B/C 接线；本层只负责参数构建、进程执行与输出解析）。
/// </summary>
public interface IPnpUtilClient
{
    /// <summary>枚举 Driver Store 全部驱动包（pnputil /enum-drivers）。</summary>
    Task<IReadOnlyList<DriverPackage>> EnumDriversAsync(CancellationToken ct = default);

    /// <summary>导出驱动包（pnputil /export-driver），destinationDir 必须已存在。</summary>
    Task<DriverRunResult> ExportAsync(string publishedName, string destinationDir, CancellationToken ct = default);

    /// <summary>批量导出：实现方必须保证单次提权覆盖整批（禁止逐包弹 UAC）。
    /// 🔴 契约（2026-09-05）：实现按包写入 destinationDir 下 <c>_stage_&lt;发布名基&gt;</c> 独立暂存目录——
    /// pnputil 导出目录名取原始 INF 名，同名原始 INF 的多版本包会相互覆盖；调用方按暂存目录精确对位整理
    /// （见 <see cref="DriverBackupOrganizer.StageDirFor"/>）。</summary>
    Task<DriverRunResult> ExportManyAsync(IReadOnlyList<string> publishedNames, string destinationDir, CancellationToken ct = default);

    /// <summary>删除驱动包（pnputil /delete-driver）；force = 连同设备关联强制删除。
    /// 🔴 目标白名单：仅第三方 oem 包发布名（<see cref="PnpUtilTokenRules.IsDeletablePublishedName"/>）。</summary>
    Task<DriverRunResult> DeleteAsync(string publishedName, bool force, CancellationToken ct = default);

    /// <summary>批量删除：实现方必须保证单次提权覆盖整批（禁止逐包弹 UAC）。目标白名单同 <see cref="DeleteAsync"/>。</summary>
    Task<DriverRunResult> DeleteManyAsync(IReadOnlyList<string> publishedNames, bool force, CancellationToken ct = default);

    /// <summary>添加驱动包到 Store（pnputil /add-driver）；install = 同时安装到匹配设备。
    /// 成功判定为双重式（退出码 + 输出计数比对，RAPR 经验）。</summary>
    Task<DriverRunResult> AddDriverAsync(string infPath, bool install, CancellationToken ct = default);

    /// <summary>批量添加：实现方必须保证单次提权覆盖整批（禁止逐个弹 UAC）。
    /// 批量路径成功判定基于逐段退出码（多段输出合并且无法按段拆计数），失败明细用
    /// <see cref="PnpUtilOutputAnalyzer.ExtractFailedSegments"/> 提取。</summary>
    Task<DriverRunResult> AddManyAsync(IReadOnlyList<string> infPaths, bool install, CancellationToken ct = default);
}
