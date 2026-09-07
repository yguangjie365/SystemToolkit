using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services.Protocol;
using SystemToolkit.Core.Utilities;
using WatsonTcp;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 文件传输服务实现：基于 WatsonTcp 的 TCP 可靠分片传输，支持断点续传与 SHA-256 校验。
/// <para>
/// 服务端监听 <see cref="TransferSettings.TransferPort"/> 接收连接；发送方建立 TCP 连接后按
/// <see cref="TransferTask.ChunkSize"/> 分片写入对端，通过 <see cref="TaskUpdated"/> 事件实时上报进度。
/// 两侧均边发/边收边算 SHA-256（单遍 IO），发送方在 <see cref="TransferMessageType.Complete"/> 消息中
/// 携带全文件哈希，接收方比对一致后回 <see cref="TransferMessageType.CompleteAck"/>。
/// </para>
/// <para>安全边界：握手时校验对端 IP 是否在设备发现在线列表（<see cref="TransferSettings.RequireKnownPeer"/>），
/// 限制并发接收数（<see cref="TransferSettings.MaxConcurrentReceives"/>），分片写入前校验
/// Offset / 长度 / ChunkSize；接收一律写临时文件（.part），校验通过后原子改名，避免同名旧文件被写坏或覆盖。</para>
/// <para>传输协议 <see cref="TransferMessage"/> 经 WatsonTcp 的 metadata 通道传递，二进制文件数据走消息体。</para>
/// <para>线程安全：传输任务以 <see cref="ConcurrentDictionary{TKey,TValue}"/> 存储，可被多线程并发读取。</para>
/// </summary>
public sealed class FileTransferService : IFileTransferService, IDisposable
{
    /// <summary>WatsonTcp metadata 中承载 <see cref="TransferMessage"/> JSON 的键。</summary>
    private const string MetaKey = "m";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly ConcurrentDictionary<string, TransferTask> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sendCts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ReceiveContext> _receiveContexts = new();

    private readonly IDeviceDiscoveryService? _discovery;
    private readonly PairingService? _pairing;
    private readonly ILogger _logger;
    private readonly HashSet<string> _pairedIps = new(StringComparer.Ordinal);
    private readonly object _pairGate = new();

    private WatsonTcpServer? _server;
    private SemaphoreSlim? _sendGate;
    private int _chunkSize = 2 * 1024 * 1024;
    private int _maxConcurrentReceives = 8;
    // volatile：SetRequireKnownPeer 可在 UI 线程运行时修改，握手校验在 TCP 回调线程读取
    private volatile bool _requireKnownPeer = true;
    private bool _requireReceiveConfirmation;
    private bool _requirePairing;
    private TimeSpan _receiveConfirmTimeout = TimeSpan.FromSeconds(30);
    private string _receiveDirectory = string.Empty;
    private readonly ConcurrentDictionary<string, PendingConfirm> _pendingConfirms = new(StringComparer.Ordinal);

    /// <summary>发送方等待握手确认的超时（含接收端确认门等待 + 人工点击延迟，2026-09-06 由 20s 放宽）。</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(120);

    /// <inheritdoc/>
    public IReadOnlyList<TransferTask> ActiveTasks => _tasks.Values
        .Where(t => t.Status is TransferStatus.Pending
            or TransferStatus.Negotiating
            or TransferStatus.Transferring
            or TransferStatus.Paused)
        .ToArray();

    /// <inheritdoc/>
    public event EventHandler<TransferTask>? TaskUpdated;

    /// <inheritdoc/>
    public event EventHandler<TransferTask>? TaskCompleted;

    /// <inheritdoc/>
    public event EventHandler<TransferRequestEventArgs>? TransferRequested;

    /// <summary>
    /// 构造传输服务。<paramref name="discovery"/> 用于接收侧来源白名单校验（可为 null，
    /// 此时 <see cref="TransferSettings.RequireKnownPeer"/> 实际不生效——测试场景用）。
    /// </summary>
    public FileTransferService(IDeviceDiscoveryService? discovery = null, ILogger? logger = null, PairingService? pairing = null)
    {
        _discovery = discovery;
        _pairing = pairing;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task StartAsync(TransferSettings settings, CancellationToken ct = default)
    {
        if (_server is not null)
            throw new InvalidOperationException("传输服务已在运行，请先调用 StopAsync。");

        _chunkSize = Math.Max(1024, settings.ChunkSize);
        _maxConcurrentReceives = Math.Max(1, settings.MaxConcurrentReceives);
        _requireKnownPeer = settings.RequireKnownPeer;
        _requireReceiveConfirmation = settings.RequireReceiveConfirmation;
        _requirePairing = settings.RequirePairing;
        _receiveConfirmTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.ReceiveConfirmTimeoutSeconds));
        lock (_pairGate)
        {
            _pairedIps.Clear();
        }
        _sendGate = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentTransfers));
        // 2026-09-02：默认由「桌面\Received」改为「下载\Received」——桌面是用户最不想被塞满的地方。
        // 与共享根目录保持同一套解析（支持下载夹被重定向）。
        _receiveDirectory = string.IsNullOrWhiteSpace(settings.ReceiveDirectory)
            ? Path.Combine(UserFolders.GetDownloadsFolder(), "Received")
            : settings.ReceiveDirectory!;
        Directory.CreateDirectory(_receiveDirectory);

        // ip 传 null 表示监听任意 IP（WatsonTcpServer 文档约定）
        _server = new WatsonTcpServer(null!, settings.TransferPort);
        _server.Events.MessageReceived += OnServerMessageReceived;
        _server.Events.ClientDisconnected += OnClientDisconnected;
        _server.Start();

        _logger.Info($"文件传输服务已启动（TCP {settings.TransferPort}，分片 {_chunkSize / 1024}KB，" +
                     $"来源白名单={(_requireKnownPeer ? "开" : "关")}，接收确认门={(_requireReceiveConfirmation ? "开" : "关")}，" +
                     $"配对码={(_requirePairing ? "开" : "关")}，并发接收上限 {_maxConcurrentReceives}）。");
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        WatsonTcpServer? server = _server;
        if (server is null)
            return;

        // 先取消所有进行中的发送任务
        foreach (CancellationTokenSource cts in _sendCts.Values)
        {
            try
            { await cts.CancelAsync().ConfigureAwait(false); }
            catch { }
        }

        // 确认门等待方全部取消（服务器关闭后握手无法继续）
        foreach (PendingConfirm pc in _pendingConfirms.Values)
            pc.Cancel();
        _pendingConfirms.Clear();
        lock (_pairGate)
        {
            _pairedIps.Clear();
        }

        try
        { server.Stop(); }
        catch { }
        server.Events.MessageReceived -= OnServerMessageReceived;
        server.Events.ClientDisconnected -= OnClientDisconnected;
        _server = null;

        foreach (ReceiveContext ctx in _receiveContexts.Values)
        {
            ctx.CloseStream();
            ctx.Dispose();
        }
        _receiveContexts.Clear();

        // SemaphoreSlim 不 Dispose（随实例生命周期，避免与在途发送任务 finally 的 Release 竞态）
        // 留 50ms 给发送任务响应取消后收尾（Release / Dispose CTS / Remove task）
        await Task.Delay(50).ConfigureAwait(false);

        _logger.Info("文件传输服务已停止。");
        _tasks.Clear();
    }

    /// <inheritdoc/>
    public Task<TransferTask> SendFileAsync(string filePath, string peerIp, int peerPort, CancellationToken ct = default)
        => SendFileAsync(filePath, peerIp, peerPort, pairCode: null, ct);

    /// <inheritdoc/>
    public async Task<TransferTask> SendFileAsync(string filePath, string peerIp, int peerPort, string? pairCode, CancellationToken ct = default)
    {
        if (_sendGate is null)
            throw new InvalidOperationException("传输服务未启动，请先调用 StartAsync。");
        if (!File.Exists(filePath))
            throw new FileNotFoundException("要发送的文件不存在。", filePath);

        var info = new FileInfo(filePath);
        var task = new TransferTask
        {
            FileName = info.Name,
            FilePath = filePath,
            FileSize = info.Length,
            Direction = TransferDirection.Send,
            PeerEndpoint = $"{peerIp}:{peerPort}",
            ChunkSize = _chunkSize,
            Status = TransferStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _tasks[task.Id] = task;
        RaiseUpdated(task);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sendCts[task.Id] = linkedCts;

        _logger.Info($"入队发送：{info.Name}（{info.Length:N0} 字节）→ {peerIp}:{peerPort}，任务 {task.Id}。");

        // 队列限流下的实际发送在后台进行，进度经事件上报
        _ = SendFileCoreAsync(task, peerIp, peerPort, linkedCts.Token, pairCode);
        return await Task.FromResult(task).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task CancelAsync(string taskId)
    {
        if (_sendCts.TryRemove(taskId, out CancellationTokenSource? cts))
        {
            try
            { cts.Cancel(); }
            catch { }
            cts.Dispose();
        }
        else if (_tasks.TryGetValue(taskId, out TransferTask? task))
        {
            // 接收侧任务：标记取消 + 关闭 stream + 清理上下文 + 通知对端停止发送
            task.Status = TransferStatus.Cancelled;
            task.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(task);
            RaiseCompleted(task);
            _tasks.TryRemove(taskId, out _);
            if (_pendingConfirms.TryRemove(taskId, out PendingConfirm? pending))
                pending.Cancel(); // 确认等待方立即退出

            // 按 taskId 定位接收上下文（ctx 以连接 Guid 索引，需遍历匹配）
            KeyValuePair<Guid, ReceiveContext> matchingKv = _receiveContexts.FirstOrDefault(kv => kv.Value.Task?.Id == taskId);
            if (matchingKv.Value is { } ctx)
            {
                ctx.CloseStream();
                ctx.Hash?.Dispose();
                ctx.Hash = null;
                _receiveContexts.TryRemove(matchingKv.Key, out _);
                _ = SendControlAsync(matchingKv.Key, new TransferMessage
                {
                    Type = TransferMessageType.Cancel,
                    TaskId = taskId,
                });
            }
            _logger.Info($"接收任务已取消：{task.FileName}（任务 {taskId}），已通知对端并关闭接收流。");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void SetReceiveDirectory(string directory)
    {
        _receiveDirectory = directory ?? string.Empty;
        if (!string.IsNullOrEmpty(_receiveDirectory))
            Directory.CreateDirectory(_receiveDirectory);
    }

    /// <inheritdoc/>
    public void SetRequireKnownPeer(bool require)
    {
        _requireKnownPeer = require;
        _logger.Info($"来源白名单已{(require ? "开启" : "关闭")}（仅接受已发现设备 = {require}）。");
    }

    /// <inheritdoc/>
    public Task RespondTransferAsync(string taskId, bool accept)
    {
        if (_pendingConfirms.TryRemove(taskId, out PendingConfirm? pc))
        {
            pc.Gate.TrySetResult(accept);
            _logger.Info($"接收确认已回复：任务 {taskId} → {(accept ? "接受" : "拒绝")}。");
        }
        else
        {
            _logger.Warn($"接收确认回复被忽略：任务 {taskId} 不在待确认列表（已超时/已处理/已断开）。");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 同步释放（宿主 <c>ServiceProvider.Dispose()</c> 走这条路径）。
    /// <para>
    /// 🔴 必须同时实现 <see cref="IDisposable"/>：只实现 <see cref="IAsyncDisposable"/> 的服务，
    /// 会让同步 <c>Dispose()</c> 抛 <c>InvalidOperationException: type only implements IAsyncDisposable</c>，
    /// 在退出路径上表现为「关闭程序即崩溃」（2026-09-06 实测，同款问题见 FileWebServer）。
    /// </para>
    /// <para>退出时当前线程可能是 UI 线程，故 Task.Run + 2s 有界等待，超时放弃——进程即将结束，由 OS 回收。</para>
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(2)))
            {
                _logger.Warn("[FileTransferService] 同步停止超时（2s），进程退出时由 OS 回收资源。");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[FileTransferService] 同步停止失败（进程即将退出，忽略）：{ex.Message}");
        }
    }

    // ===================== 发送侧（作为 WatsonTcp 客户端） =====================

    /// <summary>
    /// 发送文件核心：限流排队 → 连接对端 → 握手（携 mtime 与可选配对码）→ 边发边算 SHA-256 → Complete（携哈希）→ 等待校验确认。
    /// </summary>
    private async Task SendFileCoreAsync(TransferTask task, string peerIp, int peerPort, CancellationToken ct, string? pairCode)
    {
        WatsonTcpClient? client = null;
        bool gateAcquired = false;
        try
        {
            await _sendGate!.WaitAsync(ct).ConfigureAwait(false);
            gateAcquired = true;

            task.Status = TransferStatus.Negotiating;
            RaiseUpdated(task);

            client = new WatsonTcpClient(peerIp, peerPort);
            var handshakeTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completeTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.Events.MessageReceived += (_, e) =>
            {
                TransferMessage? tm = ParseMessage(e.Metadata);
                if (tm is null)
                    return;
                switch (tm.Type)
                {
                    case TransferMessageType.HandshakeAck:
                        handshakeTcs.TrySetResult(tm);
                        break;
                    case TransferMessageType.CompleteAck:
                        completeTcs.TrySetResult(tm);
                        break;
                    case TransferMessageType.Cancel:
                        task.ErrorMessage = "对端取消了传输。";
                        handshakeTcs.TrySetCanceled();
                        completeTcs.TrySetCanceled();
                        break;
                    case TransferMessageType.Error:
                        var ex = new InvalidOperationException(tm.Error ?? "对端报告错误。");
                        handshakeTcs.TrySetException(ex);
                        completeTcs.TrySetException(ex);
                        break;
                }
            };

            client.Connect();

            int totalChunks = (int)Math.Max(1, (task.FileSize + task.ChunkSize - 1) / task.ChunkSize);
            var handshake = new TransferMessage
            {
                Type = TransferMessageType.Handshake,
                TaskId = task.Id,
                FileName = task.FileName,
                FileSize = task.FileSize,
                ChunkSize = task.ChunkSize,
                TotalChunks = totalChunks,
                // 时间属性跨设备保留（2026-09-06 协议扩展）：手机照片等原始修改时间
                FileModifiedAt = new DateTimeOffset(new FileInfo(task.FilePath).LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                PairCode = pairCode,
            };
            await client.SendAsync(string.Empty, BuildMetadata(handshake), ct).ConfigureAwait(false);

            // 等待握手确认，获取断点位置（超时含接收端确认门的人工等待，见 HandshakeTimeout）
            TransferMessage ack = await AwaitWithTimeout(handshakeTcs.Task, HandshakeTimeout, ct).ConfigureAwait(false);
            long offset = ack.ResumeFrom;
            if (offset < 0 || offset > task.FileSize)
                offset = 0;

            task.Status = TransferStatus.Transferring;
            task.TransferredBytes = offset;
            RaiseUpdated(task);

            // 单遍 IO：发送分片的同时增量计算全文件 SHA-256（从文件头算起，与接收方含断点前缀的哈希一致）
            using var fileStream = new FileStream(task.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: task.ChunkSize, useAsync: true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // 断点续传：跳过的前缀也要喂进哈希，保证与接收方计算口径一致
            if (offset > 0)
            {
                fileStream.Seek(0, SeekOrigin.Begin);
                byte[] prefix = new byte[81920];
                long remain = offset;
                while (remain > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = await fileStream.ReadAsync(prefix.AsMemory(0, (int)Math.Min(prefix.Length, remain)), ct)
                        .ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    hasher.AppendData(prefix, 0, read);
                    remain -= read;
                }
                fileStream.Seek(offset, SeekOrigin.Begin);
            }

            byte[] buffer = new byte[task.ChunkSize];
            long totalSent = offset;
            long speedBase = totalSent;
            var sw = Stopwatch.StartNew();
            // 进度 UI 节流（性能审查 P0-1）：千兆内网分片间隔可低至毫秒级，逐片 RaiseUpdated
            // 会把 INPC 刷新打到每秒数千次。首片强制上报（取消/断点语义依赖首条进度事件），完成片强制上报。
            long lastUiReportMs = -1;
            int chunkIndex = 0;

            while (totalSent < task.FileSize)
            {
                ct.ThrowIfCancellationRequested();
                int read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                // WatsonTcp 按数组从头发送，最后一片需截成实际读取长度
                byte[] payload = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
                var chunkMsg = new TransferMessage
                {
                    Type = TransferMessageType.Chunk,
                    TaskId = task.Id,
                    Offset = totalSent,
                    ChunkIndex = chunkIndex,
                };
                await client.SendAsync(payload, BuildMetadata(chunkMsg), 0, ct).ConfigureAwait(false);

                hasher.AppendData(payload, 0, read);
                totalSent += read;
                chunkIndex++;
                if (lastUiReportMs < 0 || sw.ElapsedMilliseconds - lastUiReportMs >= 100 || totalSent == task.FileSize)
                {
                    task.TransferredBytes = totalSent;
                    UpdateSpeed(task, ref speedBase, sw);
                    RaiseUpdated(task);
                    lastUiReportMs = sw.ElapsedMilliseconds;
                }
            }

            ct.ThrowIfCancellationRequested();

            // 完成消息携带全文件哈希，接收方据此校验
            string hashHex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            task.FileHash = hashHex;
            var complete = new TransferMessage
            {
                Type = TransferMessageType.Complete,
                TaskId = task.Id,
                FileHash = hashHex,
            };
            await client.SendAsync(string.Empty, BuildMetadata(complete), ct).ConfigureAwait(false);

            // 等待对端 SHA-256 校验完成
            await AwaitWithTimeout(completeTcs.Task, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            task.Status = TransferStatus.Completed;
            task.TransferredBytes = task.FileSize;
            task.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(task);
            RaiseCompleted(task);
            _logger.Info($"发送完成：{task.FileName}（{task.FileSize:N0} 字节，{(double)task.FileSize / 1048576 / sw.Elapsed.TotalSeconds:F1} MB/s）→ {peerIp}。");
        }
        catch (OperationCanceledException)
        {
            // 主动取消：尝试通知对端
            await TrySendControlToPeerAsync(client, new TransferMessage { Type = TransferMessageType.Cancel, TaskId = task.Id })
                .ConfigureAwait(false);
            FinalizeTerminal(task, TransferStatus.Cancelled, null);
            _logger.Info($"发送已取消：{task.FileName}（任务 {task.Id}）。");
        }
        catch (Exception ex)
        {
            // 失败：尝试通知对端出错
            await TrySendControlToPeerAsync(client, new TransferMessage { Type = TransferMessageType.Error, TaskId = task.Id, Error = ex.Message })
                .ConfigureAwait(false);
            FinalizeTerminal(task, TransferStatus.Failed, ex.Message);
            _logger.Error($"发送失败：{task.FileName}（任务 {task.Id}）→ {peerIp}:{peerPort}。", ex);
        }
        finally
        {
            // 每步独立 try/catch：单项异常不阻止其余清理（StopAsync 竞态 / 网络异常等）
            try
            { if (gateAcquired) _sendGate?.Release(); }
            catch (ObjectDisposedException) { /* StopAsync 竞态 */ }
            try
            { if (_sendCts.TryRemove(task.Id, out CancellationTokenSource? cts)) cts.Dispose(); }
            catch { }
            try
            { client?.Dispose(); }
            catch { /* 忽略释放异常 */ }
            try
            {
                if (task.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
                    _tasks.TryRemove(task.Id, out _);
            }
            catch { }
        }
    }

    private static async Task TrySendControlToPeerAsync(WatsonTcpClient? client, TransferMessage tm)
    {
        if (client is null)
            return;
        try
        { await client.SendAsync(string.Empty, BuildMetadata(tm), CancellationToken.None).ConfigureAwait(false); }
        catch { /* 控制消息发送失败忽略，对端会因超时/断开而失败 */ }
    }

    // ===================== 接收侧（作为 WatsonTcp 服务端） =====================

    /// <summary>
    /// 服务端消息到达：同步处理以保证单连接分片按序写入（WatsonTcp 串行投递单连接消息，
    /// 同步处理天然形成 TCP 反压，无需额外锁）。
    /// </summary>
    private void OnServerMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        TransferMessage? tm = ParseMessage(e.Metadata);
        if (tm is null)
            return;

        Guid guid = e.Client.Guid;
        ReceiveContext ctx = _receiveContexts.GetOrAdd(guid, _ => new ReceiveContext());
        try
        {
            switch (tm.Type)
            {
                case TransferMessageType.Handshake:
                    HandleHandshake(guid, e.Client.IpPort, tm, ctx);
                    break;
                case TransferMessageType.Chunk:
                    HandleChunk(tm, e.Data ?? Array.Empty<byte>(), ctx);
                    break;
                case TransferMessageType.Complete:
                    HandleComplete(guid, tm, ctx);
                    break;
                case TransferMessageType.Cancel:
                    HandleReceiveCancel(ctx);
                    break;
                case TransferMessageType.Error:
                    HandleReceiveError(ctx, tm.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"接收处理异常（{e.Client.IpPort}，消息 {tm.Type}）。", ex);
            FailReceiveContext(guid, ctx, ex.Message);
        }
    }

    private void OnClientDisconnected(object? sender, DisconnectionEventArgs e)
    {
        Guid guid = e.Client.Guid;
        if (!_receiveContexts.TryRemove(guid, out ReceiveContext? ctx))
            return;
        ctx.CloseStream();
        ctx.Dispose();
        if (ctx.Task is not null && _pendingConfirms.TryRemove(ctx.Task.Id, out PendingConfirm? pending))
            pending.Cancel(); // 确认等待方立即退出（任务由下方断开路径终态化）

        TransferTask? task = ctx.Task;
        if (task is not null
            && task.Status is TransferStatus.Negotiating or TransferStatus.Transferring)
        {
            task.Status = TransferStatus.Failed;
            task.ErrorMessage = "对端断开连接。";
            task.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(task);
            RaiseCompleted(task);
            _tasks.TryRemove(task.Id, out _);
            _logger.Warn($"接收中断（对端断开）：{task.FileName}（任务 {task.Id}），断点已保留在 .part 文件。");
        }
    }

    /// <summary>
    /// 握手处理：来源白名单 → 并发上限 → 分片大小校验 → 建临时 .part 文件（断点续传）→ 回确认。
    /// </summary>
    private void HandleHandshake(Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx)
    {
        // 安全边界一：只接受设备发现在线的对端（防止任意主机投递文件）
        if (_requireKnownPeer && !IsKnownPeer(ipPort))
        {
            _logger.Warn($"拒绝来自未知设备的传输握手：{ipPort}（{tm.FileName}，任务 {tm.TaskId}）——不在已发现在线列表。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "来源设备未在设备发现列表中，拒绝接收。",
            });
            return;
        }

        // 安全边界一·b：配对码校验（2026-09-06 批次二）——首个携带有效码的发送方 IP
        // 记入已配对列表（服务运行期内免码，覆盖多文件批次）；一次性消费防重放
        if (_requirePairing && !IsPairedIp(ipPort))
        {
            if (_pairing is null || string.IsNullOrEmpty(tm.PairCode) || !_pairing.TryConsume(tm.PairCode))
            {
                _logger.Warn($"拒绝传输握手：配对码无效或缺失（{ipPort}，{tm.FileName}，任务 {tm.TaskId}）。");
                _ = SendControlAsync(guid, new TransferMessage
                {
                    Type = TransferMessageType.Error,
                    TaskId = tm.TaskId,
                    Error = "配对码无效或已过期，请从接收端获取最新配对码。",
                });
                return;
            }

            lock (_pairGate)
            {
                _pairedIps.Add(ipPort[..ipPort.LastIndexOf(':')]);
            }
            _logger.Info($"配对成功：{ipPort} 已加入本运行期已配对列表（后续传输免码）。");
        }

        // 关旧上下文资源 + 终态化旧任务（同连接二次握手产生僵尸任务，填占 activeReceives 配额 / 哈希泄漏）
        if (ctx.Task is not null && ctx.Task.Status is TransferStatus.Negotiating or TransferStatus.Transferring)
        {
            TransferTask oldTask = ctx.Task;
            oldTask.Status = TransferStatus.Cancelled;
            oldTask.ErrorMessage = "对端发起新握手，旧任务已被替换。";
            oldTask.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(oldTask);
            RaiseCompleted(oldTask);
            _tasks.TryRemove(oldTask.Id, out _);
            if (_pendingConfirms.TryRemove(oldTask.Id, out PendingConfirm? stale))
                stale.Cancel(); // 确认等待方立即退出，避免僵尸确认
        }
        ctx.Hash?.Dispose();
        ctx.Hash = null;
        ctx.CloseStream();

        // 安全边界二：并发接收上限（防止反复握手填充磁盘）
        int activeReceives = _receiveContexts.Values.Count(c =>
            c.Task is not null && c.Task.Status is TransferStatus.Negotiating or TransferStatus.Transferring);
        if (activeReceives >= _maxConcurrentReceives)
        {
            _logger.Warn($"拒绝传输握手：并发接收已达上限 {_maxConcurrentReceives}（来源 {ipPort}）。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "接收端并发传输已达上限，请稍后重试。",
            });
            return;
        }

        // 安全边界四：分片大小须与接收端配置一致——恶意握手声明超大 ChunkSize 会让接收端按其分配缓冲
        if (tm.ChunkSize <= 0 || tm.ChunkSize > _chunkSize)
        {
            _logger.Warn($"拒绝传输握手：分片大小不一致（对端 {tm.ChunkSize}，本端 {_chunkSize}，来源 {ipPort}）。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = $"分片大小与接收端配置不一致（对端 {tm.ChunkSize}，本端 {_chunkSize}）。",
            });
            return;
        }

        // 安全边界五：文件大小不得为负——恶意握手声明负/超大 FileSize 可绕过偏移越界校验（见 HandleChunk 溢出修复）
        if (tm.FileSize < 0)
        {
            _logger.Warn($"拒绝传输握手：文件大小非法（{tm.FileSize}，来源 {ipPort}）。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "文件大小非法。",
            });
            return;
        }

        // 任务对象统一在确认门前创建——待确认任务以 Negotiating 态出现在任务列表，
        // 并计入并发接收上限（防确认风暴占满配额）
        var task = new TransferTask
        {
            Id = tm.TaskId, // 与发送方任务 ID 关联
            FileName = tm.FileName,
            FileSize = tm.FileSize,
            Direction = TransferDirection.Receive,
            PeerEndpoint = ipPort,
            ChunkSize = tm.ChunkSize,
            Status = TransferStatus.Negotiating,
            StartedAt = DateTimeOffset.UtcNow,
        };

        if (_requireReceiveConfirmation)
        {
            // 2026-09-06 用户裁定（批次一安全模型 = 白名单 + 接收确认）：挂起等待本机用户确认，
            // 拒绝/超时即回错误；确认通过后才创建 .part 等落盘资源
            ctx.Task = task;
            _tasks[task.Id] = task;
            RaiseUpdated(task);

            var pending = new PendingConfirm();
            _pendingConfirms[task.Id] = pending;
            pending.TimeoutCts = new CancellationTokenSource(_receiveConfirmTimeout);
            pending.TimeoutCts.Token.Register(() =>
            {
                // 仅当仍在待确认列表时生效（用户已回复则 TryRemove 失败，无副作用）
                if (_pendingConfirms.TryRemove(task.Id, out PendingConfirm? timedOut))
                {
                    timedOut.TimedOut = true;
                    timedOut.Gate.TrySetResult(false);
                }
            });

            _logger.Info($"等待接收确认：{tm.FileName}（{tm.FileSize:N0} 字节）← {ipPort}，任务 {task.Id}。");
            TransferRequested?.Invoke(this, new TransferRequestEventArgs(task.Id, tm.FileName, tm.FileSize, ipPort));

            _ = Task.Run(() => AwaitConfirmationAndCompleteAsync(guid, ipPort, tm, ctx, task, pending));
            return;
        }

        CompleteHandshake(guid, ipPort, tm, ctx, task);
    }

    /// <summary>确认门等待方：接受 → 落盘资源与握手确认；拒绝/超时 → 失败并回错误；被替换/断开/停止 → 静默退出。</summary>
    private async Task AwaitConfirmationAndCompleteAsync(
        Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx, TransferTask task, PendingConfirm pending)
    {
        bool accepted;
        try
        {
            accepted = await pending.Gate.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // 连接断开 / 对端取消 / 新握手替换 / 服务停止——任务已由相应路径终态化
        }
        finally
        {
            pending.TimeoutCts?.Dispose();
            pending.TimeoutCts = null;
        }

        if (!accepted)
        {
            string reason = pending.TimedOut ? "接收端未响应确认，已自动拒绝。" : "接收端拒绝接收。";
            task.Status = TransferStatus.Failed;
            task.ErrorMessage = reason;
            task.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(task);
            RaiseCompleted(task);
            _tasks.TryRemove(task.Id, out _);
            _logger.Info($"接收请求已拒绝：{tm.FileName}（任务 {task.Id}）——{reason}");
            await SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = reason,
            }).ConfigureAwait(false);
            return;
        }

        if (ctx.Task != task)
            return; // 等待期间被同一连接的新握手替换（旧任务已终态化）

        try
        {
            CompleteHandshake(guid, ipPort, tm, ctx, task);
        }
        catch (Exception ex)
        {
            _logger.Error("接收握手处理异常（确认通过后）。", ex);
            FailReceiveContext(guid, ctx, ex.Message);
        }
    }

    /// <summary>
    /// 握手续处理（协议校验与确认门全部通过）：建临时 .part（断点续传）→ 补算前缀哈希 → 回确认。
    /// </summary>
    private void CompleteHandshake(Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx, TransferTask task)
    {
        ctx.FileModifiedAt = tm.FileModifiedAt;
        string dir = string.IsNullOrEmpty(_receiveDirectory)
            ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), "Received")
            : _receiveDirectory;
        Directory.CreateDirectory(dir);

        string safeName = SanitizeFileName(tm.FileName);

        // 断点续传基于临时 .part 文件（文件名含对端 IP），与「同名的其它文件」彻底隔离：
        // 命名对同一对端确定，取消/断开后的重试能续上上次的进度；不同对端互不干扰，
        // 不同内容的同名旧文件也不会被误当断点（内容不符会被 SHA-256 拦下并删除 .part）。
        int colon = ipPort.LastIndexOf(':');
        string peerIpToken = SanitizeFileName(colon > 0 ? ipPort[..colon] : ipPort);
        string partPath = Path.Combine(dir, $"{safeName}.{peerIpToken}.part");

        // 清理同名其它 .part（其它对端遗留的孤儿），活跃接收上下文正在使用的除外——避免磁盘缓慢泄漏
        foreach (string orphan in Directory.EnumerateFiles(dir, $"{safeName}.*.part"))
        {
            if (string.Equals(Path.GetFileName(orphan), Path.GetFileName(partPath), StringComparison.Ordinal))
                continue;
            if (IsTargetOfActiveReceive(Path.GetFullPath(orphan)))
                continue;
            try
            { File.Delete(orphan); }
            catch { /* 被占用等忽略 */ }
        }

        long resumeFrom = 0;
        if (File.Exists(partPath))
        {
            long existing = new FileInfo(partPath).Length;
            if (existing > 0 && existing < tm.FileSize)
                resumeFrom = existing;
            else
                File.Delete(partPath);
        }

        ctx.TargetPath = partPath;
        ctx.Stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize: Math.Max(4096, tm.ChunkSize), useAsync: false);
        if (resumeFrom > 0)
            ctx.Stream.Seek(resumeFrom, SeekOrigin.Begin);

        // 增量哈希从文件头算起；续传时先补读已落盘前缀，与发送方全文件哈希口径一致
        ctx.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (resumeFrom > 0)
        {
            // FileShare.ReadWrite：写句柄（上方 ctx.Stream，FileAccess.Write）已在本文件上打开，
            // File.OpenRead 的 FileShare.Read 不允许共存 Write 访问，会抛共享冲突 IOException
            using var prefixStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] prefixBuf = new byte[81920];
            long remain = resumeFrom;
            while (remain > 0)
            {
                int read = prefixStream.Read(prefixBuf, 0, (int)Math.Min(prefixBuf.Length, remain));
                if (read <= 0)
                    break;
                ctx.Hash.AppendData(prefixBuf, 0, read);
                remain -= read;
            }
        }

        ctx.Task = task;
        task.FilePath = partPath;
        task.TransferredBytes = resumeFrom;
        _tasks[task.Id] = task;
        RaiseUpdated(task);

        _logger.Info($"接收握手：{tm.FileName}（{tm.FileSize:N0} 字节，{(resumeFrom > 0 ? $"续传自 {resumeFrom:N0}" : "全新")}）← {ipPort}。");

        // 回送握手确认，告知断点位置
        _ = SendControlAsync(guid, new TransferMessage
        {
            Type = TransferMessageType.HandshakeAck,
            TaskId = tm.TaskId,
            ResumeFrom = resumeFrom,
        });

        task.Status = TransferStatus.Transferring;
        RaiseUpdated(task);
    }

    private void HandleChunk(TransferMessage tm, byte[] data, ReceiveContext ctx)
    {
        TransferTask? task = ctx.Task;
        FileStream? stream = ctx.Stream;
        if (task is null || stream is null)
            return;

        // 安全边界三：分片范围校验——Offset/长度完全信任发送方会带来稀疏大文件占盘、进度越界
        if (tm.Offset < 0
            || tm.Offset > task.FileSize
            || data.Length > task.ChunkSize
            || data.Length > task.FileSize - tm.Offset)
        {
            throw new InvalidOperationException(
                $"分片越界：Offset={tm.Offset}，长度={data.Length}，ChunkSize={task.ChunkSize}，FileSize={task.FileSize}。");
        }

        stream.Seek(tm.Offset, SeekOrigin.Begin);
        stream.Write(data, 0, data.Length);
        ctx.Hash?.AppendData(data, 0, data.Length);
        task.TransferredBytes = tm.Offset + data.Length;
        // 接收侧同口径节流（性能审查 P0-1）；完成片强制上报
        long nowTick = Environment.TickCount64;
        if (ctx.LastReportTick < 0 || nowTick - ctx.LastReportTick >= 100 || task.TransferredBytes >= task.FileSize)
        {
            ctx.LastReportTick = nowTick;
            RaiseUpdated(task);
        }
    }

    private void HandleComplete(Guid guid, TransferMessage tm, ReceiveContext ctx)
    {
        TransferTask? task = ctx.Task;
        if (task is null)
            return;
        // 已被取消/失败的任务不再处理 Complete，回 Error 通知对端立即终止（否则发送方等 10 分钟超时）
        if (task.Status is TransferStatus.Cancelled or TransferStatus.Failed)
        {
            ctx.CloseStream();
            ctx.Hash?.Dispose();
            ctx.Hash = null;
            _receiveContexts.TryRemove(guid, out _);
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = $"接收任务已终止（{task.Status}），拒绝 Complete。",
            });
            return;
        }
        ctx.Stream?.Flush();
        ctx.CloseStream();

        bool hashOk;
        string? expectedHash = tm.FileHash;
        if (ctx.Hash is null || string.IsNullOrEmpty(expectedHash) || ctx.TargetPath is null)
        {
            hashOk = false;
        }
        else
        {
            string actualHex = Convert.ToHexString(ctx.Hash.GetHashAndReset()).ToLowerInvariant();
            hashOk = string.Equals(actualHex, expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        if (hashOk)
        {
            // 校验通过后原子落定：.part → 最终文件名；目标名已存在则自动加序号，绝不覆盖已有文件
            // （hashOk 为真蕴含 TargetPath 非空；Complete 消息不带 FileName，须取接收任务里的文件名）
            string targetPath = ctx.TargetPath!;
            string finalPath = GetUniqueDestination(
                Path.GetDirectoryName(targetPath)!, SanitizeFileName(task.FileName));
            File.Move(targetPath, finalPath);
            if (ctx.FileModifiedAt > 0)
            {
                // 时间属性还原（2026-09-06 协议扩展）：失败不影响交付（文件本身已校验通过）
                try
                {
                    DateTime modified = DateTimeOffset.FromUnixTimeMilliseconds(ctx.FileModifiedAt).UtcDateTime;
                    DateTime minMTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    DateTime maxMTime = DateTime.UtcNow.AddDays(1); // 与 Web 路径同窗口：拒绝远端伪造的 9999 年时间戳
                    if (modified < minMTime) modified = minMTime;
                    if (modified > maxMTime) modified = maxMTime;
                    File.SetLastWriteTimeUtc(finalPath, modified);
                }
                catch { }
            }

            task.Status = TransferStatus.Completed;
            task.TransferredBytes = task.FileSize;
            task.FileHash = expectedHash;
            task.FilePath = finalPath;
            task.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(task);
            RaiseCompleted(task);
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.CompleteAck,
                TaskId = tm.TaskId,
            });
            _logger.Info($"接收完成：{task.FileName} → {finalPath}（SHA-256 校验通过）。");
        }
        else
        {
            task.Status = TransferStatus.Failed;
            task.ErrorMessage = "文件 SHA-256 校验失败。";
            task.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(task);
            RaiseCompleted(task);
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "SHA-256 校验失败。",
            });
            // 校验失败说明内容对不上（续传了不同内容的同名文件 / 数据损坏）：
            // 删除损坏的 .part，避免下次重试又从坏断点续传形成永久失败循环
            if (ctx.TargetPath is not null)
            {
                try
                { File.Delete(ctx.TargetPath); }
                catch { /* 被占用等忽略，孤儿清理会兜底 */ }
            }
            _logger.Error($"接收校验失败：{task.FileName}（任务 {task.Id}），已删除损坏的 .part 文件。");
        }

        ctx.Hash?.Dispose();
        ctx.Hash = null;
        _tasks.TryRemove(task.Id, out _);
        if (_receiveContexts.TryRemove(guid, out ReceiveContext? c))
            c.Dispose();
    }

    private void HandleReceiveCancel(ReceiveContext ctx)
    {
        TransferTask? task = ctx.Task;
        ctx.CloseStream();
        if (task is null)
            return;
        task.Status = TransferStatus.Cancelled;
        task.FinishedAt = DateTimeOffset.UtcNow;
        RaiseUpdated(task);
        RaiseCompleted(task);
        _tasks.TryRemove(task.Id, out _);
        if (_pendingConfirms.TryRemove(task.Id, out PendingConfirm? pending))
            pending.Cancel(); // 确认等待方立即退出
        _logger.Info($"对端取消传输：{task.FileName}（任务 {task.Id}），断点保留在 .part 文件。");
    }

    private void HandleReceiveError(ReceiveContext ctx, string? error)
    {
        TransferTask? task = ctx.Task;
        ctx.CloseStream();
        if (task is null)
            return;
        task.Status = TransferStatus.Failed;
        task.ErrorMessage = error ?? "对端报告错误。";
        task.FinishedAt = DateTimeOffset.UtcNow;
        RaiseUpdated(task);
        RaiseCompleted(task);
        _tasks.TryRemove(task.Id, out _);
        if (_pendingConfirms.TryRemove(task.Id, out PendingConfirm? pending))
            pending.Cancel(); // 确认等待方立即退出
        _logger.Warn($"对端报告接收错误：{task.FileName}（任务 {task.Id}）：{error}。");
    }

    private void FailReceiveContext(Guid guid, ReceiveContext ctx, string error)
    {
        ctx.CloseStream();
        TransferTask? task = ctx.Task;
        if (task is not null)
        {
            if (task.Status is not (TransferStatus.Completed or TransferStatus.Cancelled))
            {
                task.Status = TransferStatus.Failed;
                task.ErrorMessage = error;
                task.FinishedAt = DateTimeOffset.UtcNow;
                RaiseUpdated(task);
                RaiseCompleted(task);
                _logger.Warn($"接收失败：{task.FileName}（任务 {task.Id}）：{error}。");
            }
            _tasks.TryRemove(task.Id, out _);
        }
        ctx.Hash?.Dispose();
        ctx.Hash = null;
        if (_receiveContexts.TryRemove(guid, out ReceiveContext? c))
            c.Dispose();
        _ = SendControlAsync(guid, new TransferMessage { Type = TransferMessageType.Error, Error = error });
    }

    /// <summary>目标路径是否正被某个活跃接收上下文使用（孤儿清理时跳过，防误删并发传输的断点）。</summary>
    private bool IsTargetOfActiveReceive(string fullPath)
        => _receiveContexts.Values.Any(c =>
            c.Task is not null
            && c.Task.Status is TransferStatus.Negotiating or TransferStatus.Transferring
            && c.TargetPath is not null
            && string.Equals(Path.GetFullPath(c.TargetPath), fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>发送方 IP 是否已在本运行期配对（多文件批次免码）。</summary>
    private bool IsPairedIp(string ipPort)
    {
        int colon = ipPort.LastIndexOf(':');
        lock (_pairGate)
        {
            return _pairedIps.Contains(colon > 0 ? ipPort[..colon] : ipPort);
        }
    }

    /// <summary>对端 IP 是否在设备发现在线列表中。</summary>
    private bool IsKnownPeer(string ipPort)
    {
        if (_discovery is null)
            return true; // 未接入发现服务时退化为不校验（测试场景）
        int colon = ipPort.LastIndexOf(':');
        string ipText = colon > 0 ? ipPort[..colon] : ipPort;
        if (!IPAddress.TryParse(ipText, out IPAddress? peerIp))
            return false;
        return _discovery.Devices.Any(d => d.IsOnline && d.IPAddress.Equals(peerIp));
    }

    /// <summary>目标目录内取不冲突的落定路径：同名时追加 " (n)" 序号，绝不覆盖已有文件。</summary>
    private static string GetUniqueDestination(string dir, string fileName)
    {
        string candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate))
            return candidate;

        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 1; ; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// 向指定连接回送控制消息（metadata 通道，无文件数据）。
    /// </summary>
    private async Task SendControlAsync(Guid guid, TransferMessage tm)
    {
        WatsonTcpServer? server = _server;
        if (server is null)
            return;
        try
        {
            await server.SendAsync(guid, string.Empty, BuildMetadata(tm), 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* 控制消息发送失败忽略 */ }
    }

    // ===================== 工具方法 =====================

    private void RaiseUpdated(TransferTask task) => TaskUpdated?.Invoke(this, task);

    private void RaiseCompleted(TransferTask task) => TaskCompleted?.Invoke(this, task);

    private void FinalizeTerminal(TransferTask task, TransferStatus status, string? message)
    {
        task.Status = status;
        if (!string.IsNullOrEmpty(message))
            task.ErrorMessage = message;
        task.FinishedAt = DateTimeOffset.UtcNow;
        RaiseUpdated(task);
        RaiseCompleted(task);
    }

    private static void UpdateSpeed(TransferTask task, ref long speedBase, Stopwatch sw)
    {
        if (sw.Elapsed.TotalSeconds >= 0.5)
        {
            task.SpeedBytesPerSec = (long)((task.TransferredBytes - speedBase) / sw.Elapsed.TotalSeconds);
            speedBase = task.TransferredBytes;
            sw.Restart();
        }
    }

    private static async Task<T> AwaitWithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken ct)
    {
        var delay = Task.Delay(timeout, ct);
        Task done = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (done == task)
            return await task.ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        throw new TimeoutException("等待对端响应超时。");
    }

    private static Dictionary<string, object> BuildMetadata(TransferMessage tm)
        => new() { [MetaKey] = JsonSerializer.Serialize(tm, JsonOpts) };

    private static TransferMessage? ParseMessage(Dictionary<string, object>? meta)
    {
        if (meta is null)
            return null;
        if (!meta.TryGetValue(MetaKey, out object? raw))
            return null;
        string? json = raw switch
        {
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
            _ => raw.ToString(),
        };
        if (string.IsNullOrEmpty(json))
            return null;
        try
        { return JsonSerializer.Deserialize<TransferMessage>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "unnamed";
        string sanitized = string.Concat(fileName.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c));
        // Windows 保留设备名（CON/NUL/COM1…）即使带扩展名也是设备节点，落到设备而非文件——前缀 _ 规避
        return IsWindowsReservedDeviceName(sanitized) ? "_" + sanitized : sanitized;
    }

    private static readonly string[] WindowsReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>判定文件名是否为 Windows 保留设备名（CON/NUL/COM1…，含带扩展名形式，如 CON.txt）。</summary>
    private static bool IsWindowsReservedDeviceName(string fileName)
    {
        int dot = fileName.IndexOf('.');
        string baseName = (dot >= 0 ? fileName[..dot] : fileName).TrimEnd(' ', '.');
        foreach (string name in WindowsReservedDeviceNames)
        {
            if (string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }


    /// <summary>
    /// 接收侧单连接上下文：持有目标文件流、增量哈希与任务对象，按连接 Guid 索引。
    /// </summary>
    private sealed class ReceiveContext : IDisposable
    {
        public TransferTask? Task;
        public FileStream? Stream;
        public IncrementalHash? Hash;
        public string? TargetPath;

        /// <summary>发送方携带的文件修改时间（Unix 毫秒；0 = 未知，落定时还原）。</summary>
        public long FileModifiedAt;

        /// <summary>上次进度上报的 Tick（-1 = 未上报过；进度节流用，性能审查 P0-1）。</summary>
        public long LastReportTick = -1;

        public void CloseStream()
        {
            Stream?.Dispose();
            Stream = null;
        }

        public void Dispose()
        {
            CloseStream();
            Hash?.Dispose();
            Hash = null;
        }
    }

    /// <summary>接收确认门的待确认条目：Gate 完成（true=接受 / false=拒绝或超时）或取消（被替换/断开/停止）。</summary>
    private sealed class PendingConfirm
    {
        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource? TimeoutCts { get; set; }

        public bool TimedOut { get; set; }

        public void Cancel() => Gate.TrySetCanceled();
    }
}
