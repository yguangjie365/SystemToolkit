using SystemToolkit.Core.FileTransfer.Models;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 文件传输服务：基于 WatsonTcp 实现 TCP 可靠分片传输，支持断点续传。
/// </summary>
public interface IFileTransferService : IAsyncDisposable
{
    /// <summary>当前活跃的传输任务。</summary>
    IReadOnlyList<TransferTask> ActiveTasks { get; }

    /// <summary>传输任务状态变化时触发。</summary>
    event EventHandler<TransferTask>? TaskUpdated;

    /// <summary>传输任务完成时触发。</summary>
    event EventHandler<TransferTask>? TaskCompleted;

    /// <summary>
    /// 收到需要本机确认的传输请求时触发（仅当 <see cref="TransferSettings.RequireReceiveConfirmation"/>
    /// 开启）。UI 必须响应——调用 <see cref="RespondTransferAsync"/>；超时未响应按拒绝处理。
    /// </summary>
    event EventHandler<TransferRequestEventArgs>? TransferRequested;

    /// <summary>
    /// 回复接收确认。<paramref name="accept"/> = true 放行传输；false 拒绝并通知对端。
    /// 任务不存在或已超时/已处理时为安全空操作（记日志）。
    /// </summary>
    Task RespondTransferAsync(string taskId, bool accept);

    /// <summary>
    /// 启动传输服务端（监听 TCP 连接）。
    /// </summary>
    Task StartAsync(TransferSettings settings, CancellationToken ct = default);

    /// <summary>停止传输服务。</summary>
    Task StopAsync();

    /// <summary>
    /// 发送文件到指定设备。
    /// </summary>
    /// <param name="filePath">本地文件路径。</param>
    /// <param name="peerIp">对端 IP。</param>
    /// <param name="peerPort">对端 TCP 端口。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>传输任务对象。</returns>
    Task<TransferTask> SendFileAsync(string filePath, string peerIp, int peerPort, CancellationToken ct = default);

    /// <summary>
    /// 发送文件到指定设备（带一次性配对码，2026-09-06 批次二）。接收端开启 RequirePairing 时，
    /// 首个文件须携带从接收端获取的配对码；同批次后续文件免码（接收端已记忆发送方 IP）。
    /// </summary>
    Task<TransferTask> SendFileAsync(string filePath, string peerIp, int peerPort, string? pairCode, CancellationToken ct = default);

    /// <summary>
    /// 取消传输任务。
    /// </summary>
    Task CancelAsync(string taskId);

    /// <summary>
    /// 设置接收文件保存目录。
    /// </summary>
    void SetReceiveDirectory(string directory);

    /// <summary>
    /// 运行时切换「只接受已发现设备」的来源白名单。
    /// <para>
    /// 用途：UDP 广播在部分网络（企业 VLAN / AP 隔离）不通时，对端无法进入设备列表，
    /// 严格的白名单会让手动指定 IP 的直连被拒。关闭它即可接收来自任意 IP 的传输。
    /// </para>
    /// </summary>
    void SetRequireKnownPeer(bool require);
}

/// <summary>接收确认请求事件参数（接收端 UI 收到后必须调用 <see cref="IFileTransferService.RespondTransferAsync"/>）。</summary>
public sealed record TransferRequestEventArgs(string TaskId, string FileName, long FileSize, string PeerEndpoint);
