namespace SystemToolkit.Core.Configuration;

/// <summary>
/// 应用级常量配置（魔法数字集中管理，便于调优和维护）。
/// </summary>
public static class AppConstants
{
    // ── 异常处理 ──

    /// <summary>DispatcherUnhandledException 异常风暴窗口（秒）。</summary>
    public const int ExceptionSwallowWindowSeconds = 5;

    /// <summary>异常风暴窗口内最大容忍异常数，超限后放行以终止进程。</summary>
    public const int ExceptionSwallowLimit = 100;

    // ── 驱动管理超时 ──

    /// <summary>pnputil enum-drivers 超时时间（秒），只读查询但输出可能很大。</summary>
    public const int PnpUtilEnumTimeoutSeconds = 120;

    /// <summary>pnputil 写操作超时时间（分钟），导出单包可能上百 MB。</summary>
    public const int PnpUtilWriteTimeoutMinutes = 20;

    // ── 网络命令超时 ──

    /// <summary>netsh 配置类命令统一超时时间（秒），防进程挂起锁死 UI。</summary>
    public const int NetshCommandTimeoutSeconds = 60;

    // ── 文件传输 ──

    /// <summary>ZIP 解压最大条目数，防 ZipBomb。</summary>
    public const int MaxZipEntries = 1000;

    /// <summary>上传文件大小限制（GB）。</summary>
    public const long UploadLimitBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>文件互传发现端口。</summary>
    public const int FileTransferDiscoveryPort = 18888;

    /// <summary>文件互传传输端口。</summary>
    public const int FileTransferTransferPort = 18889;

    // ── 日志 ──

    /// <summary>日志最大行数，超出后滚动清理。</summary>
    public const int MaxLogLines = 500;

    // ── VSS 卷影 ──

    /// <summary>VSS 快照创建/删除的单次超时（分钟），VSS 常规秒级，预留网络盘/慢盘余量。</summary>
    public const int VssTimeoutMinutes = 3;

    // ── 网络诊断 ──

    /// <summary>持续 ping 单次测试时长（分钟）。</summary>
    public const int ContinuousPingDurationMinutes = 2;

    // ── 备份引擎 ──

    /// <summary>备份空间检查余量系数（20%）。</summary>
    public const double BackupSpaceMarginFactor = 1.2;
}
