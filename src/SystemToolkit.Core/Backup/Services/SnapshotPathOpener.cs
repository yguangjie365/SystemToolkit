using System.Diagnostics;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>打开快照路径的结果分类。</summary>
public enum SnapshotOpenResultKind
{
    /// <summary>目录已用 shell 打开，或文件已在资源管理器中定位。</summary>
    Opened,

    /// <summary>路径规范化后不存在（目录与文件都不是）。</summary>
    NotFound,

    /// <summary>路径为空或无法规范化（快照记录里的脏数据）。</summary>
    InvalidPath,

    /// <summary>未通过快照目录范围校验（疑似配置被篡改或数据损坏）。</summary>
    Denied,
}

/// <summary>打开快照路径的结果。NotFound / Opened 携带规范化后的完整路径，InvalidPath / Denied 携带原始输入。</summary>
public sealed record SnapshotOpenResult(SnapshotOpenResultKind Kind, string FullPath)
{
    /// <summary>构造「已打开」结果（目录 shell 打开或文件已定位），携带规范化后的完整路径。</summary>
    public static SnapshotOpenResult Opened(string fullPath) => new(SnapshotOpenResultKind.Opened, fullPath);

    /// <summary>构造「路径不存在」结果，携带规范化后的完整路径。</summary>
    public static SnapshotOpenResult NotFound(string fullPath) => new(SnapshotOpenResultKind.NotFound, fullPath);

    /// <summary>构造「路径无效」结果，携带原始输入。</summary>
    public static SnapshotOpenResult InvalidPath(string rawPath) => new(SnapshotOpenResultKind.InvalidPath, rawPath);

    /// <summary>构造「越界拒绝」结果，携带原始输入。</summary>
    public static SnapshotOpenResult Denied(string rawPath) => new(SnapshotOpenResultKind.Denied, rawPath);
}

/// <summary>
/// 快照路径打开服务（REVIEW-2026-08-30 P1-4）：自 FileBackupView 迁入，
/// 收敛视图层仅有的两处 <c>Process.Start</c> 暴露面。规则：
/// ① 目录 → shell 打开；② 文件 → explorer /select 定位；③ 规范化失败 / 范围越界 →
/// 拒绝并落盘 Warn。打开动作经委托注入，测试无需真的启动资源管理器。
/// </summary>
public sealed class SnapshotPathOpener
{
    private readonly ILogger _logger;
    private readonly Action<string> _openDirectory;
    private readonly Action<string> _revealFile;

    /// <summary>创建服务；打开动作可注入（测试用），缺省为 shell 打开目录、explorer /select 定位文件。</summary>
    public SnapshotPathOpener(ILogger? logger = null, Action<string>? openDirectory = null, Action<string>? revealFile = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _openDirectory = openDirectory ?? (p => Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true }));
        _revealFile = revealFile ?? (p => Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + p + "\"") { UseShellExecute = true }));
    }

    /// <summary>
    /// 校验并打开快照路径。
    /// </summary>
    /// <param name="path">快照路径（视图中超链接 Tag 的原始值）。</param>
    /// <param name="isAllowed">
    /// 可选的范围校验（快照目录白名单，来自 SnapshotManager.IsSnapshotDirAllowed）。
    /// 传入 null 表示跳过该校验——沿用原视图「未选中规则时无规则上下文」的语义。
    /// </param>
    public SnapshotOpenResult Open(string path, Func<string, bool>? isAllowed = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SnapshotOpenResult.InvalidPath(path);
        }
        // 范围校验先于规范化：与原视图一致，白名单检查直接面对快照记录的原始值
        if (isAllowed is not null && !isAllowed(path))
        {
            // 安全信号必须落盘：越界路径意味着配置被篡改或数据损坏，
            // 弹窗关掉后就再无痕迹，排查时无从下手。
            _logger.Warn("快照路径越界，已拒绝打开（疑似数据损坏）：" + path);
            return SnapshotOpenResult.Denied(path);
        }
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            // 用 Warn 而非 Error：这不是本次操作失败，而是快照记录里的路径本身就是脏数据
            //（配置损坏 / 被手工改过 / 跨机器迁移遗留），是需要被看见的数据质量问题。
            _logger.Warn("快照路径无效，已拒绝打开：" + path + " —— " + ex.Message);
            return SnapshotOpenResult.InvalidPath(path);
        }
        if (Directory.Exists(fullPath))
        {
            _openDirectory(fullPath);
            return SnapshotOpenResult.Opened(fullPath);
        }
        if (File.Exists(fullPath))
        {
            _revealFile(fullPath);
            return SnapshotOpenResult.Opened(fullPath);
        }
        return SnapshotOpenResult.NotFound(fullPath);
    }
}
