using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services.Protocol;
using SystemToolkit.Core.Logging;
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
    // 审查 F-01：接收并发槽计数（Interlocked 原子抢占，取代"先 Count 再判断"的 TOCTOU 模式）
    private int _activeReceives;
    private readonly HashSet<string> _releasedReceiveSlots = new();
    private readonly object _receiveSlotLock = new();
    // volatile：SetRequireKnownPeer 可在 UI 线程运行时修改，握手校验在 TCP 回调线程读取
    private volatile bool _requireKnownPeer = true;
    // volatile：SetConflictPolicy 在 UI 线程改，握手期的同名解析在 TCP 回调线程读（2026-09-13 批次 P1）
    private volatile TransferConflictPolicy _conflictPolicy = TransferConflictPolicy.Rename;
    private bool _requireReceiveConfirmation;
    private bool _requirePairing;
    private TimeSpan _receiveConfirmTimeout = TimeSpan.FromSeconds(30);
    private string _receiveDirectory = string.Empty;
    private readonly ConcurrentDictionary<string, PendingConfirm> _pendingConfirms = new(StringComparer.Ordinal);

    /// <summary>
    /// 每个活跃任务的暂停闸（协议 §4.2 双向 PAUSE/RESUME，2026-09-13 批次 P1）。
    /// 键 = 任务 ID；终态任务留下的条目由 <see cref="GetOrCreatePauseGate"/> 惰性回收。
    /// </summary>
    private readonly ConcurrentDictionary<string, PauseGate> _pauseGates = new(StringComparer.Ordinal);

    /// <summary>本机暂停的自动取消计时器（键 = 任务 ID）；恢复/终态即取消。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pauseTimers = new(StringComparer.Ordinal);

    /// <summary>
    /// 活跃发送任务的客户端连接（键 = 任务 ID）。
    /// <para>
    /// 为什么要登记：暂停/恢复**从 UI 线程发起**，而连接对象原本只活在发送方法栈里，
    /// 外部拿不到 → 无法把「我停了」告知对端（对端继续等，两边界面不一致）。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, WatsonTcpClient> _sendClients = new(StringComparer.Ordinal);

    /// <summary>本机暂停超过该时长自动取消（<see cref="Timeout.InfiniteTimeSpan"/> = 不限）。</summary>
    private TimeSpan _pauseTimeout = TimeSpan.FromMinutes(30);

    /// <summary>发送方等待握手确认的超时（含接收端确认门等待 + 人工点击延迟，2026-09-06 由 20s 放宽）。</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 报文头（metadata）字节预算，**收发两侧都必须显式设置**。
    /// <para>
    /// 🔴 为什么必须改：文本走 metadata 通道，而 WatsonTcp 的 <c>MaxHeaderSize</c> 默认是
    /// <b>262144 字节（256 KB）</b> —— 与 <see cref="TransferText.MaxBytes"/> **同值**，
    /// 等于"正好写满就发不出去"。这不是推断：B8a-2 的边界用例实测在 15 s 内收不到任何回执，
    /// 逐层排查后从 WatsonTcp 的 API 文档坐实默认值（"Default is 262144 (256KB)"）。
    /// </para>
    /// <para>
    /// 取 1 MB 的依据：文本序列化进 JSON 时，非 ASCII 会被转义（CJK 1 码元 → 6 字节，
    /// emoji 2 码元 → 12 字节），最坏膨胀约 3 倍 —— 262144 字节上限的 emoji 文本
    /// 序列化后约 768 KB，1 MB 留有余量。
    /// </para>
    /// <para>
    /// 代价是这条 DoS 防护（"防止畸形/恶意头耗尽内存"）被放宽了 4 倍：
    /// 单连接最坏 1 MB × 最大连接数。仅发送文本的连接需要它，**文件路径的报文头始终只有几百字节**，
    /// 所以实际暴露面没有变化。
    /// </para>
    /// </summary>
    internal const int MaxHeaderBytes = 1024 * 1024;

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
        Interlocked.Exchange(ref _activeReceives, 0); // 重启即重置并发槽（审查 F-01）
        lock (_receiveSlotLock)
        {
            _releasedReceiveSlots.Clear();
        }
        _requireKnownPeer = settings.RequireKnownPeer;
        // 🔴 策略必须在这里读进来：漏了这一行，设置里选了「跳过/覆盖」也一律按默认改名走，
        // 而用户以为自己选的动作生效了——这是 2026-09-13 实测踩到的（用例先红抓住）。
        _conflictPolicy = settings.ConflictPolicy;
        _requireReceiveConfirmation = settings.RequireReceiveConfirmation;
        _requirePairing = settings.RequirePairing;
        _receiveConfirmTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.ReceiveConfirmTimeoutSeconds));
        // 0 或负数 = 不限时长（用户显式选择）；否则按分钟换算
        _pauseTimeout = settings.PauseTimeoutMinutes > 0
            ? TimeSpan.FromMinutes(settings.PauseTimeoutMinutes)
            : Timeout.InfiniteTimeSpan;
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
        // 报文头预算：默认 256 KB 装不下"写满上限"的文本（见 MaxHeaderBytes 的说明与实测）
        _server.Settings.MaxHeaderSize = MaxHeaderBytes;
        _server.Events.MessageReceived += OnServerMessageReceived;
        _server.Events.ClientDisconnected += OnClientDisconnected;
        _server.Start();

        _logger.Info($"文件传输服务已启动（TCP {settings.TransferPort}，分片 {_chunkSize / 1024}KB，" +
                     $"来源白名单={(_requireKnownPeer ? "开" : "关")}，接收确认门={(_requireReceiveConfirmation ? "开" : "关")}，" +
                     $"配对码={(_requirePairing ? "开" : "关")}，同名策略={TransferConflictResolver.Describe(_conflictPolicy)}，" +
                     $"并发接收上限 {_maxConcurrentReceives}）。");
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

        // 暂停闸全部强制放行（2026-09-13 批次 P1）：停服务时若还有任务卡在暂停等待里，
        // 取消信号不会被看见，StopAsync 会一直等到那 50ms 之后才发现任务还没收尾。
        foreach (PauseGate gate in _pauseGates.Values)
            gate.ForceRelease();

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
        Interlocked.Exchange(ref _activeReceives, 0); // 停止即清空接收槽（迟到的释放只是减到负数，无副作用）
        lock (_receiveSlotLock)
        {
            _releasedReceiveSlots.Clear();
        }
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
    public async Task<TransferTask> SendTextAsync(
        string text, string peerIp, int peerPort, string? pairCode = null, CancellationToken ct = default)
    {
        if (_sendGate is null)
            throw new InvalidOperationException("传输服务未启动，请先调用 StartAsync。");

        // 🔴 本地判据先行，且**不碰网络**：不合规的文本不是一次传输尝试，
        // 抛出去让调用方立刻看到（而不是造一个"传过但失败"的假任务污染任务列表与历史）。
        TextValidation validation = TransferText.Validate(text);
        if (!validation.IsValid)
        {
            _logger.Warn($"拒绝发送文本：{validation.ErrorText}（UTF-8 {validation.ByteCount} 字节）。");
            throw new ArgumentException(validation.ErrorText, nameof(text));
        }

        var task = new TransferTask
        {
            Kind = TransferKind.Text,
            // 文本没有文件名，用预览充当任务/历史的显示名（与历史条目同一口径）
            FileName = TransferText.Preview(text),
            FileSize = validation.ByteCount,
            Direction = TransferDirection.Send,
            PeerEndpoint = $"{peerIp}:{peerPort}",
            Status = TransferStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _tasks[task.Id] = task;
        RaiseUpdated(task);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sendCts[task.Id] = linkedCts;

        _logger.Info(
            $"入队发送文本：{validation.CharCount} 字（UTF-8 {validation.ByteCount} 字节）"
            + $"→ {peerIp}:{peerPort}，任务 {task.Id}。");

        _ = SendTextCoreAsync(task, text, peerIp, peerPort, linkedCts.Token, pairCode);
        return await Task.FromResult(task).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task CancelAsync(string taskId)
    {
        // 原因码：只在尚未打标时记「用户取消」——暂停超时自动取消已写好 PAUSE_TIMEOUT，
        // 用 ??= 保证不把它覆盖成「用户取消」（否则用户看到的原因会指向错误的方向）
        if (_tasks.TryGetValue(taskId, out TransferTask? known))
        {
            known.ReasonCode ??= TransferReasonCodes.UserCancel;
        }

        // 暂停中的任务被取消：先强制放行闸门。否则发送循环还卡在暂停等待里，
        // 取消信号要等下一次分片才被看见——大分片下就是用户感知的"点了取消没反应"。
        if (_pauseGates.TryGetValue(taskId, out PauseGate? pauseGate))
        {
            pauseGate.ForceRelease();
        }
        CancelPauseTimeout(taskId);

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
            ReleaseReceiveSlot(task); // 审查 F-01：归还接收并发槽
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
        => RespondTransferAsync(taskId, accept ? TransferDecision.AcceptWith(TransferConflictPolicy.Rename) : TransferDecision.Reject);

    /// <inheritdoc/>
    public Task RespondTransferAsync(string taskId, TransferDecision decision)
    {
        if (_pendingConfirms.TryRemove(taskId, out PendingConfirm? pc))
        {
            pc.Gate.TrySetResult(decision);
            _logger.Info(
                $"接收确认已回复：任务 {taskId} → {(decision.Accept ? "接受" : "拒绝")}"
                + (decision.Accept ? $"（同名处理：{TransferConflictResolver.Describe(decision.Conflict)}）" : string.Empty));
        }
        else
        {
            _logger.Warn($"接收确认回复被忽略：任务 {taskId} 不在待确认列表（已超时/已处理/已断开）。");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void SetConflictPolicy(TransferConflictPolicy policy)
    {
        _conflictPolicy = policy;
        _logger.Info($"同名冲突策略已切换为「{TransferConflictResolver.Describe(policy)}」（仅影响之后到达的传输）。");
    }

    /// <summary>
    /// 测试专用：直接设定暂停超时时长（生产路径由 <see cref="TransferSettings.PauseTimeoutMinutes"/> 决定）。
    /// <para>
    /// 为什么需要这个缝：配置单位是**分钟**（用户可理解的时间尺度），
    /// 而验证「暂停超时自动取消」不能真等满一分钟——测试被拖慢到不可接受并不换来任何覆盖。
    /// </para>
    /// </summary>
    internal void ConfigurePauseTimeoutForTest(TimeSpan timeout) => _pauseTimeout = timeout;

    /// <inheritdoc/>
    public async Task<bool> PauseTaskAsync(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out TransferTask? task))
        {
            _logger.Warn($"暂停被忽略：任务 {taskId} 不存在或已终态。");
            return false;
        }

        if (task.Status is not (TransferStatus.Pending or TransferStatus.Transferring or TransferStatus.Negotiating))
        {
            _logger.Warn($"暂停被忽略：任务 {taskId} 当前状态为 {task.Status}，只有排队中/协商中/传输中的任务可暂停。");
            return false;
        }

        PauseGate gate = GetOrCreatePauseGate(taskId);
        gate.SetPaused(local: true, paused: true);
        task.PausedAt = DateTimeOffset.UtcNow;
        task.PausedByPeer = false;
        task.Status = TransferStatus.Paused;
        RaiseUpdated(task);

        await NotifyPauseStateAsync(task, paused: true).ConfigureAwait(false);
        StartPauseTimeout(task);
        _logger.Info($"任务已暂停：{task.FileName}（{(task.Direction == TransferDirection.Send ? "发送" : "接收")}，任务 {taskId}）。");
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> ResumeTaskAsync(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out TransferTask? task))
        {
            _logger.Warn($"恢复被忽略：任务 {taskId} 不存在或已终态。");
            return false;
        }

        if (task.Status != TransferStatus.Paused)
        {
            _logger.Warn($"恢复被忽略：任务 {taskId} 当前状态为 {task.Status}，并未暂停。");
            return false;
        }

        PauseGate gate = GetOrCreatePauseGate(taskId);
        gate.SetPaused(local: true, paused: false);
        CancelPauseTimeout(taskId);
        task.PausedAt = null;

        // 双方都恢复才回到「传输中」：对端仍暂停时如实留在「已暂停」，不谎报开始
        if (gate.IsPaused)
        {
            task.PausedByPeer = true;
        }
        else
        {
            task.PausedByPeer = false;
            task.Status = TransferStatus.Transferring;
        }
        RaiseUpdated(task);

        await NotifyPauseStateAsync(task, paused: false).ConfigureAwait(false);
        _logger.Info($"任务已恢复：{task.FileName}（任务 {taskId}）。");
        return true;
    }

    /// <summary>把本机的暂停/恢复意图告知对端（发送任务走客户端连接，接收任务走该任务所属的服务端连接）。</summary>
    private async Task NotifyPauseStateAsync(TransferTask task, bool paused)
    {
        TransferMessageType type = paused ? TransferMessageType.Pause : TransferMessageType.Resume;
        var message = new TransferMessage { Type = type, TaskId = task.Id };

        if (task.Direction == TransferDirection.Send)
        {
            if (_sendClients.TryGetValue(task.Id, out WatsonTcpClient? client))
            {
                await TrySendControlToPeerAsync(client, message).ConfigureAwait(false);
            }
            return;
        }

        KeyValuePair<Guid, ReceiveContext> kv = _receiveContexts.FirstOrDefault(k => k.Value.Task?.Id == task.Id);
        if (kv.Value is not null)
        {
            await SendControlAsync(kv.Key, message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 本机暂停的自动取消计时：到点仍停着就取消任务。
    /// <para>
    /// 为什么必须有：暂停是单方面意图，对端无法区分「对面在暂停」与「对面挂了」。
    /// 没有上限，一次忘掉的暂停会让对端永久挂着，且本机接收并发槽也一直占着。
    /// </para>
    /// </summary>
    private void StartPauseTimeout(TransferTask task)
    {
        CancelPauseTimeout(task.Id);
        if (_pauseTimeout == Timeout.InfiniteTimeSpan)
        {
            return; // 用户显式选择「不限时长」
        }

        var cts = new CancellationTokenSource();
        _pauseTimers[task.Id] = cts;
        TimeSpan limit = _pauseTimeout;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(limit, cts.Token).ConfigureAwait(false);
                if (_pauseGates.TryGetValue(task.Id, out PauseGate? gate)
                    && gate.IsPaused && gate.Local
                    && task.Status == TransferStatus.Paused)
                {
                    task.ReasonCode = TransferReasonCodes.PauseTimeout;
                    task.ErrorMessage = TransferReasonCodes.Describe(TransferReasonCodes.PauseTimeout);
                    _logger.Warn($"暂停超时自动取消：{task.FileName}（任务 {task.Id}，上限 {limit.TotalMinutes:0} 分钟）。");
                    await CancelAsync(task.Id).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 已恢复或已终态——预期路径
            }
        });
    }

    private void CancelPauseTimeout(string taskId)
    {
        if (_pauseTimers.TryRemove(taskId, out CancellationTokenSource? cts))
        {
            try
            { cts.Cancel(); }
            catch { }
            cts.Dispose();
        }
    }

    /// <summary>
    /// 取（必要时建）任务的暂停闸，并顺手回收**已终态任务**遗留的闸与计时器。
    /// <para>
    /// 惰性清理的理由：终态路径有六七处，逐处插一行清理必然漏一处 = 长期缓慢泄漏；
    /// 集中在这里回收则只需保证「下一次暂停/恢复时清一次」，增长天然有界。
    /// </para>
    /// </summary>
    private PauseGate GetOrCreatePauseGate(string taskId)
    {
        // 🟡 审查 v8-🟡-4：**先取闸、再清理**（原先反过来，两步之间没有任何互斥）。
        // 原实现是「先按 `!_tasks.ContainsKey(id)` 摘除 → 再 GetOrAdd」，两步非原子：
        // 并发 PauseAsync（另一任务触发的清理）可能把本线程刚拿到的闸摘掉，
        // 于是 PauseAsync 作用在闸 A 上、传输循环按 id 去查却拿到闸 B（或查不到）——
        // 界面显示「已暂停」而数据照流。改为先 GetOrAdd（本身原子）再清理**其它**键。
        PauseGate gate = _pauseGates.GetOrAdd(taskId, _ => new PauseGate());

        foreach (string stale in _pauseGates.Keys
            .Where(id => !string.Equals(id, taskId, StringComparison.Ordinal)
                && !_tasks.ContainsKey(id)
                // 已暂停的闸不回收：回收等于把"暂停"这个状态静默丢掉
                && !(_pauseGates.TryGetValue(id, out PauseGate? g) && g.IsPaused))
            .ToArray())
        {
            _pauseGates.TryRemove(stale, out _);
            CancelPauseTimeout(stale);
        }

        return gate;
    }

    /// <summary>
    /// 分片循环里的暂停等待：返回是否真的等过（true 时调用方需重置速度基线，
    /// 否则暂停时长会把速度摊薄成假低值）。等待期间以固定粒度轮询，
    /// 保证「取消 / 服务停止」在暂停状态下依然立即生效。
    /// </summary>
    private async Task<bool> WaitWhilePausedAsync(string taskId, CancellationToken ct)
    {
        if (!_pauseGates.TryGetValue(taskId, out PauseGate? gate))
        {
            return false;
        }

        bool waited = false;
        while (gate.IsPaused)
        {
            waited = true;
            ct.ThrowIfCancellationRequested();
            Task signal = gate.WaitAsync();
            if (signal.IsCompleted)
            {
                break;
            }
            // ct 传入 Delay：取消/停服务时立即唤醒，不必等满一个轮询周期
            await Task.WhenAny(signal, Task.Delay(PausePollIntervalMs, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        return waited;
    }

    /// <summary>暂停等待的轮询粒度：取 1s —— 远小于人的操作尺度，又不会空转烧 CPU。</summary>
    private const int PausePollIntervalMs = 1000;

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
        // 审查 F-03：捕获到局部——Stop→Start 会替换 _sendGate 字段，旧任务 finally 若按字段
        // 释放会把许可还给"新"信号量（Wait(A)/Release(B)），新服务的并发上限因此失真
        SemaphoreSlim gate = _sendGate
            ?? throw new InvalidOperationException("传输服务未启动，无法发送。");
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            gateAcquired = true;

            client = new WatsonTcpClient(peerIp, peerPort);
            var handshakeTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completeTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 暂停闸先建好再订阅消息（2026-09-13 批次 P1）：对端随时可能发 Pause，
            // 处理器必须已持有同一个 gate 实例——后建会让先到的 Pause 落空，
            // 表现为"对面点了暂停，这边还在灌数据"。
            // ⚠️ 变量名不能叫 gate：本方法里 gate 已被并发信号量占用（见上方 gateAcquired）。
            PauseGate pauseGate = GetOrCreatePauseGate(task.Id);
            // 排队中被暂停的任务：直接以「已暂停」入场，别先亮一下「协商中」再变回去（视觉闪烁 = 状态不实）
            task.Status = pauseGate.IsPaused ? TransferStatus.Paused : TransferStatus.Negotiating;
            RaiseUpdated(task);

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
                    case TransferMessageType.Pause:
                        // 对端要求暂停：挂起本地闸门（发送循环会在下一次分片前看到）
                        pauseGate.SetPaused(local: false, paused: true);
                        task.PausedByPeer = true;
                        task.Status = TransferStatus.Paused;
                        RaiseUpdated(task);
                        break;
                    case TransferMessageType.Resume:
                        pauseGate.SetPaused(local: false, paused: false);
                        task.PausedByPeer = false;
                        if (!pauseGate.IsPaused)
                        {
                            task.Status = TransferStatus.Transferring;
                        }
                        RaiseUpdated(task);
                        break;
                    case TransferMessageType.Cancel:
                        task.ErrorMessage = "对端取消了传输。";
                        task.ReasonCode = TransferReasonCodes.UserCancel;
                        handshakeTcs.TrySetCanceled();
                        completeTcs.TrySetCanceled();
                        break;
                    case TransferMessageType.Error:
                        // 对端给的原因码是权威来源（例如「跳过」「磁盘不足」），本机不臆造
                        task.ReasonCode = tm.ReasonCode;
                        var ex = new InvalidOperationException(tm.Error ?? "对端报告错误。");
                        handshakeTcs.TrySetException(ex);
                        completeTcs.TrySetException(ex);
                        break;
                }
            };

            client.Connect();
            _sendClients[task.Id] = client; // 暂停/恢复需要从 UI 线程拿到这条连接（见字段注释）

            // 对端在暂停期间掉线：放行闸门，让循环走到下一次 SendAsync 并由它抛出真实错误，
            // 走既有失败路径收场。没有这一步，「暂停 + 对面消失」会一直挂到暂停超时（默认 30 分钟）。
            client.Events.ServerDisconnected += (_, _) => pauseGate.ForceRelease();

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

            task.Status = pauseGate.IsPaused ? TransferStatus.Paused : TransferStatus.Transferring;
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

                // 暂停闸（2026-09-13 批次 P1）：在**分片边界**挂起——已发出的分片不撤回，
                // 对端按偏移继续落盘，恢复后从断点续上，无需重传任何已确认数据。
                if (await WaitWhilePausedAsync(task.Id, ct).ConfigureAwait(false))
                {
                    // 暂停时长不计入速度/耗时口径：重置统计基线，否则速度被"暂停的几十分钟"摊薄成假低值
                    sw.Restart();
                    speedBase = totalSent;
                    task.SpeedBytesPerSec = 0;
                    RaiseUpdated(task);
                }

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
            { if (gateAcquired) gate.Release(); } // 释放当初 Wait 的那一个（审查 F-03）
            catch (ObjectDisposedException) { /* StopAsync 竞态 */ }
            try
            { if (_sendCts.TryRemove(task.Id, out CancellationTokenSource? cts)) cts.Dispose(); }
            catch { }
            try
            { _sendClients.TryRemove(task.Id, out _); } // 连接已不再需要（暂停/恢复入口随之失效）
            catch { }
            try
            { CancelPauseTimeout(task.Id); }
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

    /// <summary>
    /// 文本发送核心：建一条短连接 → 发 <c>Text</c> → 等 <c>TextAck</c> → 终态。
    /// <para>
    /// 与 <see cref="SendFileCoreAsync"/> 的差别：**没有分片循环、没有哈希、没有暂停语义**。
    /// 文本只占一条 metadata 消息，谈不上"暂停到一半"——所以这里对 <c>Pause</c>/<c>Resume</c>
    /// **不做任何状态表演**（假装把一条已发完的消息"暂停"就是状态欺骗）。
    /// </para>
    /// </summary>
    private async Task SendTextCoreAsync(
        TransferTask task, string text, string peerIp, int peerPort, CancellationToken ct, string? pairCode)
    {
        WatsonTcpClient? client = null;
        bool gateAcquired = false;
        SemaphoreSlim gate = _sendGate
            ?? throw new InvalidOperationException("传输服务未启动，无法发送。");
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            gateAcquired = true;

            client = new WatsonTcpClient(peerIp, peerPort);
            // 文本要装进报文头 → 与接收端对称地抬高这一侧的预算（文件路径无需，其头只有几百字节）
            client.Settings.MaxHeaderSize = MaxHeaderBytes;
            var ackTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.Events.MessageReceived += (_, e) =>
            {
                TransferMessage? tm = ParseMessage(e.Metadata);
                if (tm is null)
                {
                    return;
                }

                switch (tm.Type)
                {
                    case TransferMessageType.TextAck:
                        ackTcs.TrySetResult(tm);
                        break;
                    case TransferMessageType.Cancel:
                        task.ErrorMessage = "对端取消了传输。";
                        task.ReasonCode = TransferReasonCodes.UserCancel;
                        ackTcs.TrySetCanceled();
                        break;
                    case TransferMessageType.Error:
                        // 对端给的原因码是权威来源（例如「剪贴板写入失败」「被拒绝」），本机不臆造
                        task.ReasonCode = tm.ReasonCode;
                        ackTcs.TrySetException(new InvalidOperationException(tm.Error ?? "对端报告错误。"));
                        break;
                }
            };

            client.Connect();

            task.Status = TransferStatus.Transferring;
            RaiseUpdated(task);

            var message = new TransferMessage
            {
                Type = TransferMessageType.Text,
                TaskId = task.Id,
                Text = text,
                PairCode = pairCode,
            };
            await client.SendAsync(string.Empty, BuildMetadata(message), ct).ConfigureAwait(false);

            // 超时含接收端确认门的人工等待（与文件握手同一上限，口径一致）
            await AwaitWithTimeout(ackTcs.Task, HandshakeTimeout, ct).ConfigureAwait(false);

            task.Status = TransferStatus.Completed;
            task.TransferredBytes = task.FileSize;
            task.FinishedAt = DateTimeOffset.UtcNow;
            RaiseUpdated(task);
            RaiseCompleted(task);
            _logger.Info($"文本已送达：{task.FileName}（{task.FileSize} 字节 → {peerIp}，任务 {task.Id}）。");
        }
        catch (OperationCanceledException)
        {
            await TrySendControlToPeerAsync(client, new TransferMessage { Type = TransferMessageType.Cancel, TaskId = task.Id })
                .ConfigureAwait(false);
            FinalizeTerminal(task, TransferStatus.Cancelled, null);
            _logger.Info($"文本发送已取消：任务 {task.Id}。");
        }
        catch (Exception ex)
        {
            await TrySendControlToPeerAsync(client, new TransferMessage { Type = TransferMessageType.Error, TaskId = task.Id, Error = ex.Message })
                .ConfigureAwait(false);
            FinalizeTerminal(task, TransferStatus.Failed, ex.Message);
            // 对端若已给出原因码（拒绝 / 剪贴板失败），FinalizeTerminal 不会覆盖它
            _logger.Warn($"文本发送失败：任务 {task.Id} → {peerIp}:{peerPort}：{ex.Message}（原因码 {task.ReasonCode ?? "无"}）。");
        }
        finally
        {
            try
            { if (gateAcquired) gate.Release(); }
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
        {
            // 反序列化失败（含"对端版本更新、消息类型本端不认识"）——**不得静默丢弃**：
            // 能认出类型名时明确回一个 Error，让对端立刻知道原因，而不是傻等到超时。
            HandleUnparsableMessage(e.Client.Guid, e.Client.IpPort, e.Metadata);
            return;
        }

        Guid guid = e.Client.Guid;
        ReceiveContext ctx = _receiveContexts.GetOrAdd(guid, _ => new ReceiveContext());
        try
        {
            switch (tm.Type)
            {
                case TransferMessageType.Handshake:
                    HandleHandshake(guid, e.Client.IpPort, tm, ctx);
                    break;
                case TransferMessageType.Text:
                    HandleText(guid, e.Client.IpPort, tm, ctx);
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
                case TransferMessageType.Pause:
                    HandleReceivePauseState(ctx, paused: true);
                    break;
                case TransferMessageType.Resume:
                    HandleReceivePauseState(ctx, paused: false);
                    break;
                case TransferMessageType.Error:
                    HandleReceiveError(ctx, tm.Error, tm.ReasonCode);
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
        // 🔴 审查 2026-09-10（🔴-1）：此处**不得**先摘除 ctx —— ReleaseReceiveSlot 的守卫
        // 要求任务仍登记在 _receiveContexts 中（见 :1166），先摘除会让归还被短路
        // （Interlocked.Decrement 永不执行）→ 每次对端断开泄漏一个接收并发槽，
        // MaxConcurrentReceives（默认 8）次后接收端彻底拒绝新握手，直到重启进程。
        // 摘除统一挪到本方法末尾（归还之后）。
        if (!_receiveContexts.TryGetValue(guid, out ReceiveContext? ctx))
            return;
        ctx.CloseStream();
        ctx.Dispose();
        if (ctx.Task is not null && _pendingConfirms.TryRemove(ctx.Task.Id, out PendingConfirm? pending))
            pending.Cancel(); // 确认等待方立即退出（任务由下方断开路径终态化）

        TransferTask? task = ctx.Task;
        if (task is not null && HoldsReceiveResources(task.Status))
        {
            task.Status = TransferStatus.Failed;
            task.ErrorMessage = "对端断开连接。";
            task.ReasonCode = TransferReasonCodes.PeerDisconnected;
            task.FinishedAt = DateTimeOffset.UtcNow;
            CancelPauseTimeout(task.Id); // 暂停计时器已无意义，顺手回收
            ReleaseReceiveSlot(task); // 审查 F-01：归还接收并发槽（ctx 仍在册，守卫放行）
            RaiseUpdated(task);
            RaiseCompleted(task);
            _tasks.TryRemove(task.Id, out _);
            _logger.Warn($"接收中断（对端断开）：{task.FileName}（任务 {task.Id}），断点已保留在 .part 文件。");
        }

        _receiveContexts.TryRemove(guid, out _); // 归还槽之后再摘除上下文
    }

    /// <summary>接收目录（未配置时为「桌面\Received」）。单一来源——预检与落盘必须指向同一处。</summary>
    private string ResolveReceiveDirectory()
        => string.IsNullOrEmpty(_receiveDirectory)
            ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), "Received")
            : _receiveDirectory;

    /// <summary>
    /// 握手处理：来源白名单 → 配对码 → 并发上限 → 分片大小 / 文件大小校验 → **磁盘空间预检**
    /// → 建临时 .part 文件（断点续传）→ 回确认。
    /// </summary>
    /// <summary>
    /// 接收准入的两道**共同**安全边界（协议 §2）：来源白名单 + 配对码。文件与文本共用同一份判据。
    /// <para>
    /// 🔴 为什么提成一个方法：文本能直接进本机剪贴板，敏感度**不低于**文件（可能是一条密码、
    /// 验证码或带 token 的链接）。若只给文件路径设白名单而让文本自己再写一遍，两处判据迟早漂移，
    /// 而漂移的方向总是"某一类悄悄放宽"——本仓已经吃过"同一判据两处各写"的亏。
    /// </para>
    /// </summary>
    /// <param name="guid">对端连接标识（回错误用）。</param>
    /// <param name="ipPort">对端 IP:Port。</param>
    /// <param name="tm">收到的消息（取配对码与任务 ID）。</param>
    /// <param name="subject">日志里"这是什么"的描述（文件「名，任务号」或文本「字数，任务号」）。</param>
    /// <returns>true = 放行；false = 已回带原因码的 Error，调用方直接返回。</returns>
    private bool TryPassAdmissionGates(Guid guid, string ipPort, TransferMessage tm, string subject)
    {
        // 安全边界一：只接受设备发现在线的对端（防止任意主机投递）
        if (_requireKnownPeer && !IsKnownPeer(ipPort))
        {
            _logger.Warn($"拒绝来自未知设备的传输请求：{ipPort}（{subject}）——不在已发现在线列表。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "来源设备未在设备发现列表中，拒绝接收。",
                ReasonCode = TransferReasonCodes.PeerNotDiscovered,
            });
            return false;
        }

        // 安全边界一·b：配对码校验（2026-09-06 批次二）——首个携带有效码的发送方 IP
        // 记入已配对列表（服务运行期内免码，覆盖多文件/多文本批次）；一次性消费防重放
        if (_requirePairing && !IsPairedIp(ipPort))
        {
            if (_pairing is null || string.IsNullOrEmpty(tm.PairCode) || !_pairing.TryConsume(tm.PairCode))
            {
                _logger.Warn($"拒绝传输请求：配对码无效或缺失（{ipPort}，{subject}）。");
                _ = SendControlAsync(guid, new TransferMessage
                {
                    Type = TransferMessageType.Error,
                    TaskId = tm.TaskId,
                    Error = "配对码无效或已过期，请从接收端获取最新配对码。",
                    ReasonCode = TransferReasonCodes.PairingInvalid,
                });
                return false;
            }

            // 🟠 审查 2026-09-10（🟠-5）：补冒号守卫（与 IsPairedIp/IsKnownPeer 同款）——
            // 无冒号时 LastIndexOf 返回 -1，ipPort[..-1] 抛 ArgumentOutOfRangeException，
            // 被外层 catch 记成"接收处理异常"，把本可正常完成的传输记成故障。
            int colon = ipPort.LastIndexOf(':');
            if (colon > 0)
            {
                lock (_pairGate)
                {
                    _pairedIps.Add(ipPort[..colon]);
                }
            }
            _logger.Info($"配对成功：{ipPort} 已加入本运行期已配对列表（后续传输免码）。");
        }

        return true;
    }

    private void HandleHandshake(Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx)
    {
        if (!TryPassAdmissionGates(guid, ipPort, tm, $"{tm.FileName}，任务 {tm.TaskId}"))
        {
            return;
        }

        // 关旧上下文资源 + 终态化旧任务（同连接二次握手产生僵尸任务，填占 activeReceives 配额 / 哈希泄漏）
        if (ctx.Task is not null
            && ctx.Task.Status is TransferStatus.Negotiating or TransferStatus.Transferring or TransferStatus.Paused)
        {
            TransferTask oldTask = ctx.Task;
            oldTask.Status = TransferStatus.Cancelled;
            ReleaseReceiveSlot(oldTask); // 审查 F-01：被替换的旧任务归还接收并发槽
            oldTask.ErrorMessage = "对端发起新握手，旧任务已被替换。";
            oldTask.ReasonCode ??= TransferReasonCodes.UserCancel;
            oldTask.FinishedAt = DateTimeOffset.UtcNow;
            CancelPauseTimeout(oldTask.Id);
            if (_pauseGates.TryGetValue(oldTask.Id, out PauseGate? staleGate))
            {
                staleGate.ForceRelease(); // 任务已终结，别让任何等待方还挂在暂停上
            }
            RaiseUpdated(oldTask);
            RaiseCompleted(oldTask);
            _tasks.TryRemove(oldTask.Id, out _);
            if (_pendingConfirms.TryRemove(oldTask.Id, out PendingConfirm? stale))
                stale.Cancel(); // 确认等待方立即退出，避免僵尸确认
        }
        ctx.Hash?.Dispose();
        ctx.Hash = null;
        ctx.CloseStream();

        // 安全边界二：并发接收上限（审查 F-01：Interlocked 原子抢占——
        // 原"先 Count 再 if"的观察→决策两步可被并发握手同时通过而突破上限）
        int nowActive = Interlocked.Increment(ref _activeReceives);
        if (nowActive > _maxConcurrentReceives)
        {
            Interlocked.Decrement(ref _activeReceives); // 抢占失败即归还
            _logger.Warn($"拒绝传输握手：并发接收已达上限 {_maxConcurrentReceives}（来源 {ipPort}）。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "接收端并发传输已达上限，请稍后重试。",
                ReasonCode = TransferReasonCodes.ConcurrencyLimit,
            });
            return;
        }

        // 安全边界四：分片大小须与接收端配置一致——恶意握手声明超大 ChunkSize 会让接收端按其分配缓冲
        if (tm.ChunkSize <= 0 || tm.ChunkSize > _chunkSize)
        {
            Interlocked.Decrement(ref _activeReceives); // 审查 R2（2026-09-10）：拒绝分支必须归还已抢的槽
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
            Interlocked.Decrement(ref _activeReceives); // 审查 R2（2026-09-10）：拒绝分支必须归还已抢的槽
            _logger.Warn($"拒绝传输握手：文件大小非法（{tm.FileSize}，来源 {ipPort}）。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "文件大小非法。",
                ReasonCode = TransferReasonCodes.InvalidFileSize,
            });
            return;
        }

        // 安全边界六（2026-09-13 P0）：**接收前磁盘空间预检**。
        // 设计依据：协议 §7「磁盘空间不足 → TRANSFER_REQUEST 时接收端预检总量，不足即
        // TRANSFER_REJECT（原因码 INSUFFICIENT_DISK）」。
        // 🔴 此前只有手机 Web 通道做了预检（FileWebServer 复用同一个 DiskSpaceUtil），
        // 电脑↔电脑通道**没做** → 大文件会"传到一半才发现盘满"，此刻已写坏 .part，
        // 比"提前拒绝"差得多（用户白等、磁盘多一份垃圾）。
        // 复用手机端同款工具（含 128MB 安全余量），保证两条通道口径一致。
        // 已知取舍（有意，非疏漏）：这里按 **FileSize 总量**判定，不减去断点已收字节——
        // 断点偏移要到 CompleteHandshake 才知道，而那里的拒绝路径无法安全归还接收槽
        // （ReleaseReceiveSlot 要求 ctx.Task 已挂上且任务已终态）。宁可对"盘将满时的续传"
        // 偏保守（提示用户清理后再传），也不让"盘真的不够"漏过去。
        // Unknown（UNC / 未就绪卷 / 无权限）**不阻断**：接收失败最坏只是白传一次，
        // 不像备份那样会毁数据；但必须留痕，不允许静默通过。
        DiskSpaceCheck space = DiskSpaceUtil.Check(ResolveReceiveDirectory(), tm.FileSize);
        if (space == DiskSpaceCheck.Insufficient)
        {
            Interlocked.Decrement(ref _activeReceives); // 与上面各拒绝分支同款：归还已抢的接收槽
            _logger.Warn(
                $"拒绝传输握手：接收目录所在磁盘空间不足（需 {tm.FileSize:N0} 字节 + 安全余量，"
                + $"目录 {ResolveReceiveDirectory()}），来源 {ipPort}，任务 {tm.TaskId}，原因码 INSUFFICIENT_DISK。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "接收端磁盘空间不足，请清理空间后重试。",
                ReasonCode = TransferReasonCodes.InsufficientDisk,
            });
            return;
        }
        if (space == DiskSpaceCheck.Unknown)
        {
            _logger.Warn(
                $"无法判定接收目录剩余空间（目录 {ResolveReceiveDirectory()}，任务 {tm.TaskId}）"
                + "——按放行处理；若传输中途失败请优先检查目标盘空间。");
        }

        // 安全边界七（2026-09-13 批次 P1 ⑥）：**同名冲突策略在握手期解析**，不在落定时才反悔。
        // 为什么必须提前：等到收完才发现"该跳过" = 白传一趟（大文件是分钟级浪费）；
        // 而"覆盖"若到落定时才揭晓，用户在按「接收」时根本不知道自己会丢文件——那就是欺骗。
        string receiveDir = ResolveReceiveDirectory();
        TransferConflictPolicy effectivePolicy = _conflictPolicy;
        bool askPending = false;
        if (effectivePolicy == TransferConflictPolicy.Ask)
        {
            if (_requireReceiveConfirmation)
            {
                // 由确认门弹窗逐次询问；用户的选择（改名/覆盖/跳过）**只对本次生效**，
                // 不改动全局设置——把"本次选择"写成"永久设置"是越权。
                askPending = true;
                effectivePolicy = TransferConflictPolicy.Rename; // 询问结果落地前的保守占位
            }
            else
            {
                // 用户裁定（2026-09-13）：无从询问时降级为「自动改名」——不弹窗、不阻断、不覆盖。
                effectivePolicy = TransferConflictPolicy.Rename;
                _logger.Warn(
                    $"同名策略为「询问」但接收确认门未开启，本份降级为「自动改名」"
                    + $"（原因码 {TransferReasonCodes.ConflictAskUnavailable}，任务 {tm.TaskId}）。");
            }
        }

        string safeTargetName = SanitizeFileName(tm.FileName);
        ConflictPlan conflict = TransferConflictResolver.Resolve(receiveDir, safeTargetName, effectivePolicy);
        if (conflict.Kind == ConflictResolution.Skip)
        {
            Interlocked.Decrement(ref _activeReceives); // 与其它拒绝分支同款：归还已抢的接收槽
            _logger.Info(
                $"按同名策略「跳过」未接收：{safeTargetName}（目标已存在 {conflict.TargetPath}），"
                + $"来源 {ipPort}，任务 {tm.TaskId}，原因码 {TransferReasonCodes.ConflictSkip}。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "接收端已存在同名文件，按「跳过」策略未接收。",
                ReasonCode = TransferReasonCodes.ConflictSkip,
            });
            return;
        }

        ctx.ConflictPolicy = effectivePolicy;
        ctx.Conflict = conflict;
        ctx.AskPending = askPending;

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
                    timedOut.Gate.TrySetResult(TransferDecision.Reject);
                }
            });

            _logger.Info(
                $"等待接收确认：{tm.FileName}（{tm.FileSize:N0} 字节）← {ipPort}，任务 {task.Id}"
                + $"（同名：{TransferConflictResolver.Describe(conflict.Kind)}）。");
            // 弹窗要能回答用户"接不接"的全部疑问（2026-09-13 批次 P1 ⑦）：
            // 存到哪、盘还剩多少、是否已有同名、按当前策略会发生什么、对面是谁。
            TransferRequested?.Invoke(this, new TransferRequestEventArgs(task.Id, tm.FileName, tm.FileSize, ipPort)
            {
                ReceiveDirectory = receiveDir,
                AvailableFreeBytes = DiskSpaceUtil.TryGetAvailableFreeBytes(receiveDir),
                DiskSpace = space,
                TargetExists = conflict.Kind != ConflictResolution.Fresh,
                ConflictAction = conflict.Kind,
                ConflictPolicy = _conflictPolicy,
                PeerDeviceName = ResolvePeerDeviceName(ipPort),
            });

            _ = Task.Run(() => AwaitConfirmationAndCompleteAsync(guid, ipPort, tm, ctx, task, pending));
            return;
        }

        CompleteHandshake(guid, ipPort, tm, ctx, task);
    }

    /// <summary>确认门等待方：接受 → 落盘资源与握手确认；拒绝/超时 → 失败并回错误；被替换/断开/停止 → 静默退出。</summary>
    private async Task AwaitConfirmationAndCompleteAsync(
        Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx, TransferTask task, PendingConfirm pending)
    {
        TransferDecision decision;
        try
        {
            decision = await pending.Gate.Task.ConfigureAwait(false);
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

        if (!decision.Accept)
        {
            string reason = pending.TimedOut ? "接收端未响应确认，已自动拒绝。" : "接收端拒绝接收。";
            task.Status = TransferStatus.Failed;
            task.ErrorMessage = reason;
            task.ReasonCode = pending.TimedOut ? TransferReasonCodes.ConfirmTimeout : TransferReasonCodes.UserReject;
            task.FinishedAt = DateTimeOffset.UtcNow;
            ReleaseReceiveSlot(task); // 审查 F-01：归还接收并发槽
            RaiseUpdated(task);
            RaiseCompleted(task);
            _tasks.TryRemove(task.Id, out _);
            _logger.Info($"接收请求已拒绝：{tm.FileName}（任务 {task.Id}）——{reason}（原因码 {task.ReasonCode}）。");
            // 审查 v5（🟡-3）：fire-and-forget Task 内的发送失败会静默逃逸——兜底记日志，避免未观察异常
            try
            {
                await SendControlAsync(guid, new TransferMessage
                {
                    Type = TransferMessageType.Error,
                    TaskId = tm.TaskId,
                    Error = reason,
                    ReasonCode = task.ReasonCode,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"拒绝回执发送失败（任务 {task.Id}）：{ex.Message}");
            }
            return;
        }

        // 策略=「询问」：用户的本次选择只对本份生效（不改全局设置），据此重算落定计划
        if (ctx.AskPending)
        {
            ctx.AskPending = false;
            ctx.ConflictPolicy = decision.Conflict;
            ctx.Conflict = TransferConflictResolver.Resolve(
                ResolveReceiveDirectory(), SanitizeFileName(tm.FileName), decision.Conflict);
            _logger.Info(
                $"用户在确认门选择了同名处理方式「{TransferConflictResolver.Describe(decision.Conflict)}」"
                + $"（任务 {task.Id} → {TransferConflictResolver.Describe(ctx.Conflict.Kind)}）。");

            if (ctx.Conflict.Kind == ConflictResolution.Skip)
            {
                // 用户在弹窗里选了「跳过」：如实按跳过收场，**不落盘、不改名、不覆盖**
                task.Status = TransferStatus.Skipped;
                task.ReasonCode = TransferReasonCodes.ConflictSkip;
                task.ErrorMessage = TransferReasonCodes.Describe(TransferReasonCodes.ConflictSkip);
                task.FinishedAt = DateTimeOffset.UtcNow;
                ReleaseReceiveSlot(task);
                RaiseUpdated(task);
                RaiseCompleted(task);
                _tasks.TryRemove(task.Id, out _);
                try
                {
                    await SendControlAsync(guid, new TransferMessage
                    {
                        Type = TransferMessageType.Error,
                        TaskId = tm.TaskId,
                        Error = "接收方选择「跳过」（已存在同名文件），未接收。",
                        ReasonCode = TransferReasonCodes.ConflictSkip,
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"跳过回执发送失败（任务 {task.Id}）：{ex.Message}");
                }
                return;
            }
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
    /// 文本接收（FT-3，B8a）。与文件路径的差别：**不落盘、不分片、无同名冲突、无磁盘预检**。
    /// <para>
    /// 🔴 **文本恒走确认门**，与 <see cref="TransferSettings.RequireReceiveConfirmation"/> 无关。
    /// 理由有二：①「接收一条文本」这个动作**就是**「写进本机剪贴板」，而写剪贴板必须由 UI 完成
    /// （Core 不依赖 WPF）——没有 UI 就没有交付，此时若照旧静默回 <c>TextAck</c>，
    /// 等于告诉发送方"已送达"而实际什么都没发生（状态欺骗）；② 剪贴板是**全局单例**，
    /// 不打招呼就覆盖用户正在用的内容，本身就是一种打扰（剪贴板劫持）。
    /// </para>
    /// <para>
    /// 确认通过后由 <see cref="AwaitTextConfirmationAsync"/> 回 <c>TextAck</c>；
    /// 拒绝/超时回 <c>Error</c> + 原因码（**用户拒绝**与**剪贴板写失败**是两个不同的码）。
    /// </para>
    /// </summary>
    private void HandleText(Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx)
    {
        TextValidation validation = TransferText.Validate(tm.Text);
        if (!TryPassAdmissionGates(guid, ipPort, tm, $"{validation.CharCount} 字文本，任务 {tm.TaskId}"))
        {
            return;
        }

        // 同一连接上已有活跃接收（例如正在进行文件传输）：拒绝，**绝不以"新请求替换旧任务"处理**。
        // 文件握手那样做是合理的（同一份文件重发起），但让一条文本去取消别人正在传的文件是破坏性的。
        if (ctx.Task is not null && HoldsReceiveResources(ctx.Task.Status))
        {
            _logger.Warn(
                $"拒绝接收文本：连接 {ipPort} 上已有进行中的接收任务（{ctx.Task.FileName}，任务 {ctx.Task.Id}）"
                + "——不得用一条文本替换进行中的文件传输。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "接收端该连接上已有进行中的传输，请稍后重试。",
                ReasonCode = TransferReasonCodes.ConcurrencyLimit,
            });
            return;
        }

        // 并发接收上限：与文件同一条闸（文本虽小，但"文本可以无限灌"同样是攻击面）
        int nowActive = Interlocked.Increment(ref _activeReceives);
        if (nowActive > _maxConcurrentReceives)
        {
            Interlocked.Decrement(ref _activeReceives);
            _logger.Warn($"拒绝接收文本：并发接收已达上限 {_maxConcurrentReceives}（来源 {ipPort}）。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = "接收端并发传输已达上限，请稍后重试。",
                ReasonCode = TransferReasonCodes.ConcurrencyLimit,
            });
            return;
        }

        // 长度校验：对端未校验时（旧版本 / 非本工具实现）本端必须拦下，**绝不截断后当成功**
        if (!validation.IsValid)
        {
            Interlocked.Decrement(ref _activeReceives);
            bool tooLong = validation.Kind == TextValidationKind.TooLong;
            string reason = tooLong ? "文本超出单条上限，已拒绝接收。" : "收到空文本，已拒绝接收。";
            _logger.Warn($"拒绝接收文本：{reason}（来源 {ipPort}，任务 {tm.TaskId}，UTF-8 {validation.ByteCount} 字节）。");
            _ = SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.Error,
                TaskId = tm.TaskId,
                Error = reason,
                // 空文本是本工具自己就不允许发的情况，没有独立成因码；超限有专码
                ReasonCode = tooLong ? TransferReasonCodes.TextTooLong : null,
            });
            return;
        }

        var task = new TransferTask
        {
            Id = tm.TaskId, // 与发送方任务 ID 关联
            Kind = TransferKind.Text,
            FileName = TransferText.Preview(tm.Text),
            FileSize = validation.ByteCount,
            Direction = TransferDirection.Receive,
            PeerEndpoint = ipPort,
            Status = TransferStatus.Negotiating,
            StartedAt = DateTimeOffset.UtcNow,
        };
        ctx.Task = task; // 必须挂上：ReleaseReceiveSlot 以「任务仍登记在 ctx」为归还并发槽的前提
        _tasks[task.Id] = task;
        RaiseUpdated(task);

        var pending = new PendingConfirm();
        _pendingConfirms[task.Id] = pending;
        pending.TimeoutCts = new CancellationTokenSource(_receiveConfirmTimeout);
        pending.TimeoutCts.Token.Register(() =>
        {
            if (_pendingConfirms.TryRemove(task.Id, out PendingConfirm? timedOut))
            {
                timedOut.TimedOut = true;
                timedOut.Gate.TrySetResult(TransferDecision.Reject);
            }
        });

        _logger.Info($"等待接收确认（文本）：{validation.CharCount} 字 ← {ipPort}，任务 {task.Id}。");
        TransferRequested?.Invoke(this, new TransferRequestEventArgs(task.Id, task.FileName, task.FileSize, ipPort)
        {
            Kind = TransferKind.Text,
            // 🔴 全文，不做 120 字预览截断：用户要判断"接不接"必须看到全貌
            Text = tm.Text,
            TextLength = validation.CharCount,
            PeerDeviceName = ResolvePeerDeviceName(ipPort),
        });

        _ = Task.Run(() => AwaitTextConfirmationAsync(guid, ipPort, tm, ctx, task, pending));
    }

    /// <summary>
    /// 文本确认门等待方：接受 → 回 <c>TextAck</c>；拒绝/超时 → 失败并回带原因码的错误；被替换/断开/停止 → 静默退出。
    /// <para>
    /// 🔴 刻意**不在这里写剪贴板**：交付由 UI 在回复确认门**之前**完成（写成功才回 Accept）。
    /// 这样"回执"与"实际交付"是同一个事实，不存在"先说成功、失败再改口"的窗口。
    /// </para>
    /// </summary>
    private async Task AwaitTextConfirmationAsync(
        Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx, TransferTask task, PendingConfirm pending)
    {
        TransferDecision decision;
        try
        {
            decision = await pending.Gate.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // 连接断开 / 对端取消 / 服务停止——任务已由相应路径终态化
        }
        finally
        {
            pending.TimeoutCts?.Dispose();
            pending.TimeoutCts = null;
        }

        if (!decision.Accept)
        {
            // 原因码优先级：超时 → CONFIRM_TIMEOUT；UI 给了具体码（如剪贴板写失败）→ 用它；否则 → USER_REJECT
            string reasonCode = pending.TimedOut
                ? TransferReasonCodes.ConfirmTimeout
                : decision.ReasonCode ?? TransferReasonCodes.UserReject;
            string reason = pending.TimedOut
                ? "接收端未响应确认，已自动拒绝。"
                : TransferReasonCodes.Describe(reasonCode);
            task.Status = TransferStatus.Failed;
            task.ErrorMessage = reason;
            task.ReasonCode = reasonCode;
            task.FinishedAt = DateTimeOffset.UtcNow;
            ReleaseReceiveSlot(task);
            RaiseUpdated(task);
            RaiseCompleted(task);
            _tasks.TryRemove(task.Id, out _);
            _logger.Info($"文本接收被拒绝：任务 {task.Id}——{reason}（原因码 {reasonCode}）。");
            try
            {
                await SendControlAsync(guid, new TransferMessage
                {
                    Type = TransferMessageType.Error,
                    TaskId = tm.TaskId,
                    Error = reason,
                    ReasonCode = reasonCode,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"文本拒绝回执发送失败（任务 {task.Id}）：{ex.Message}");
            }
            return;
        }

        // 接受：交付（写剪贴板）已由 UI 完成，这里只负责如实回执
        task.Status = TransferStatus.Completed;
        task.TransferredBytes = task.FileSize;
        task.FinishedAt = DateTimeOffset.UtcNow;
        ReleaseReceiveSlot(task);
        RaiseUpdated(task);
        RaiseCompleted(task);
        _tasks.TryRemove(task.Id, out _);
        _logger.Info($"文本已接收并交付：{task.FileName}（{task.FileSize} 字节 ← {ipPort}，任务 {task.Id}）。");
        try
        {
            await SendControlAsync(guid, new TransferMessage
            {
                Type = TransferMessageType.TextAck,
                TaskId = tm.TaskId,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"文本回执发送失败（任务 {task.Id}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 握手续处理（协议校验与确认门全部通过）：建临时 .part（断点续传）→ 补算前缀哈希 → 回确认。
    /// </summary>
    private void CompleteHandshake(Guid guid, string ipPort, TransferMessage tm, ReceiveContext ctx, TransferTask task)
    {
        ctx.FileModifiedAt = tm.FileModifiedAt;
        string dir = ResolveReceiveDirectory();
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
        // 已终态的任务不再处理 Complete，回 Error 通知对端立即终止（否则发送方等 10 分钟超时）
        // （含 Skipped：跳过也是定论，不能因为迟到的一条 Complete 就把结论翻过来）
        if (IsTerminal(task.Status))
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
            // 校验通过后原子落定：.part → 最终文件名。
            // 动作由**握手期算好的冲突计划**决定（2026-09-13 批次 P1 ⑥）；
            // 但「全新/改名」在落定前**重新哨探一次唯一名**——接收期间目标目录可能又冒出同名文件，
            // 若沿用握手时的判断，就会静默覆盖别人刚放进来的文件（那不是用户选过的"覆盖"）。
            // （hashOk 为真蕴含 TargetPath 非空；Complete 消息不带 FileName，须取接收任务里的文件名）
            string targetPath = ctx.TargetPath!;
            string finalDir = Path.GetDirectoryName(targetPath)!;
            string safeName = SanitizeFileName(task.FileName);
            ConflictPlan plan = ctx.Conflict.Kind switch
            {
                ConflictResolution.Overwrite => ctx.Conflict,
                ConflictResolution.Skip => ctx.Conflict,
                _ => new ConflictPlan(
                    ConflictResolution.Fresh,
                    TransferConflictResolver.GetUniqueDestination(finalDir, safeName)),
            };

            if (plan.Kind == ConflictResolution.Skip)
            {
                // 竞态路径：握手时目标不存在、收完才出现同名 → 按策略跳过（保留对方的文件，丢弃本次 .part）
                task.Status = TransferStatus.Skipped;
                task.ReasonCode = TransferReasonCodes.ConflictSkip;
                task.ErrorMessage = TransferReasonCodes.Describe(TransferReasonCodes.ConflictSkip);
                task.FinishedAt = DateTimeOffset.UtcNow;
                ReleaseReceiveSlot(task);
                RaiseUpdated(task);
                RaiseCompleted(task);
                _tasks.TryRemove(task.Id, out _);
                try
                { File.Delete(targetPath); }
                catch { /* 被占用等忽略，孤儿清理兜底 */ }
                ctx.Hash?.Dispose();
                ctx.Hash = null;
                if (_receiveContexts.TryRemove(guid, out ReceiveContext? skippedCtx))
                    skippedCtx.Dispose();
                _ = SendControlAsync(guid, new TransferMessage
                {
                    Type = TransferMessageType.Error,
                    TaskId = tm.TaskId,
                    Error = "接收端在接收期间出现同名文件，按「跳过」策略未写入。",
                    ReasonCode = TransferReasonCodes.ConflictSkip,
                });
                _logger.Warn(
                    $"接收完成但按「跳过」策略未写入：{task.FileName}（目标 {plan.TargetPath} 已存在，"
                    + $"任务 {task.Id}，原因码 {TransferReasonCodes.ConflictSkip}）。");
                return;
            }

            string finalPath = plan.TargetPath;
            File.Move(targetPath, finalPath, overwrite: plan.Kind == ConflictResolution.Overwrite);
            if (plan.Kind == ConflictResolution.Overwrite)
            {
                _logger.Warn($"按「覆盖」策略替换了同名文件：{finalPath}（任务 {task.Id}）——原文件已不可恢复。");
            }
            if (ctx.FileModifiedAt > 0)
            {
                // 时间属性还原（2026-09-06 协议扩展）：失败不影响交付（文件本身已校验通过）
                try
                {
                    DateTime modified = DateTimeOffset.FromUnixTimeMilliseconds(ctx.FileModifiedAt).UtcDateTime;
                    var minMTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    DateTime maxMTime = DateTime.UtcNow.AddDays(1); // 与 Web 路径同窗口：拒绝远端伪造的 9999 年时间戳
                    if (modified < minMTime)
                    {
                        modified = minMTime;
                    }
                    if (modified > maxMTime)
                    {
                        modified = maxMTime;
                    }
                    File.SetLastWriteTimeUtc(finalPath, modified);
                }
                catch { }
            }

            task.Status = TransferStatus.Completed;
            task.TransferredBytes = task.FileSize;
            task.FileHash = expectedHash;
            task.FilePath = finalPath;
            ReleaseReceiveSlot(task); // 审查 F-01：归还接收并发槽
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
            task.ReasonCode = TransferReasonCodes.HashMismatch;
            ReleaseReceiveSlot(task); // 审查 F-01：归还接收并发槽
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
        if (IsTerminal(task.Status))
        {
            _logger.Info($"忽略迟到的取消消息：{task.FileName}（任务 {task.Id}）已终态 {task.Status}，不覆盖结论。");
            return;
        }
        task.Status = TransferStatus.Cancelled;
        task.ReasonCode ??= TransferReasonCodes.UserCancel; // 对端取消（本机此前若已打标则保留）
        task.FinishedAt = DateTimeOffset.UtcNow;
        CancelPauseTimeout(task.Id);
        if (_pauseGates.TryGetValue(task.Id, out PauseGate? gate))
        {
            gate.ForceRelease(); // 已终止，别让发送循环还挂着等恢复
        }
        ReleaseReceiveSlot(task); // 审查 F-01：归还接收并发槽
        RaiseUpdated(task);
        RaiseCompleted(task);
        _tasks.TryRemove(task.Id, out _);
        if (_pendingConfirms.TryRemove(task.Id, out PendingConfirm? pending))
            pending.Cancel(); // 确认等待方立即退出
        _logger.Info($"对端取消传输：{task.FileName}（任务 {task.Id}），断点保留在 .part 文件。");
    }

    /// <summary>
    /// 对端暂停/恢复：本端**只如实跟随状态**——不释放流、不丢哈希、不动断点。
    /// 恢复所需的全部上下文都还在 <see cref="ReceiveContext"/> 里，所以"恢复"就是改一个状态。
    /// </summary>
    private void HandleReceivePauseState(ReceiveContext ctx, bool paused)
    {
        TransferTask? task = ctx.Task;
        if (task is null || task.Status is not (TransferStatus.Transferring or TransferStatus.Paused))
        {
            return; // 未开始/已终态的任务不理会暂停请求
        }

        PauseGate gate = GetOrCreatePauseGate(task.Id);
        gate.SetPaused(local: false, paused: paused);
        task.PausedByPeer = paused;
        task.Status = paused ? TransferStatus.Paused : TransferStatus.Transferring;
        RaiseUpdated(task);
        _logger.Info($"对端{(paused ? "暂停" : "恢复")}传输：{task.FileName}（任务 {task.Id}）。");
    }

    private void HandleReceiveError(ReceiveContext ctx, string? error, string? reasonCode = null)
    {
        TransferTask? task = ctx.Task;
        ctx.CloseStream();
        if (task is null)
            return;
        if (IsTerminal(task.Status))
        {
            // 终态冻结：一条迟到的 Error 不许改写已有结论（否则会把「确认超时」改写成"对端报错"，
            // 或把已成功的传输改成失败）。日志留痕，便于排查"为什么原因码不是我预期那个"。
            // 反向验证：把本守卫收窄为仅 Completed → ReasonCode_ConfirmTimeout 立即变红。
            _logger.Info(
                $"忽略迟到的错误消息：{task.FileName}（任务 {task.Id}）已终态 {task.Status}"
                + $"（对端原因码 {reasonCode ?? "无"}），不覆盖结论。");
            return;
        }
        task.Status = TransferStatus.Failed;
        task.ErrorMessage = error ?? "对端报告错误。";
        task.ReasonCode = reasonCode; // 对端给的原因码优先（它是权威来源），本机不臆造
        task.FinishedAt = DateTimeOffset.UtcNow;
        CancelPauseTimeout(task.Id);
        if (_pauseGates.TryGetValue(task.Id, out PauseGate? gate))
        {
            gate.ForceRelease();
        }
        ReleaseReceiveSlot(task); // 审查 F-01：归还接收并发槽
        RaiseUpdated(task);
        RaiseCompleted(task);
        _tasks.TryRemove(task.Id, out _);
        if (_pendingConfirms.TryRemove(task.Id, out PendingConfirm? pending))
            pending.Cancel(); // 确认等待方立即退出
        _logger.Warn($"对端报告接收错误：{task.FileName}（任务 {task.Id}）：{error}（原因码 {reasonCode}）。");
    }

    private void FailReceiveContext(Guid guid, ReceiveContext ctx, string error)
    {
        ctx.CloseStream();
        TransferTask? task = ctx.Task;
        if (task is not null)
        {
            if (!IsTerminal(task.Status))
            {
                task.Status = TransferStatus.Failed;
                task.ErrorMessage = error;
                task.FinishedAt = DateTimeOffset.UtcNow;
                RaiseUpdated(task);
                RaiseCompleted(task);
                _logger.Warn($"接收失败：{task.FileName}（任务 {task.Id}）：{error}。");
            }
            else
            {
                _logger.Info($"接收上下文清理（任务已终态 {task.Status}）：{task.FileName}（任务 {task.Id}）。");
            }
            _tasks.TryRemove(task.Id, out _);
            ReleaseReceiveSlot(task); // 审查 R2（2026-09-10）：终态后归还并发槽（幂等，防槽泄漏锁死接收端）
        }
        ctx.Hash?.Dispose();
        ctx.Hash = null;
        if (_receiveContexts.TryRemove(guid, out ReceiveContext? c))
            c.Dispose();
        _ = SendControlAsync(guid, new TransferMessage { Type = TransferMessageType.Error, Error = error });
    }

    /// <summary>
    /// 该状态是否**仍持有接收资源**（断点 .part 文件 + 接收并发槽）——单一来源。
    /// <para>
    /// 🔴 为什么提成一个方法：这条判据此前在两处**各写一遍**（孤儿断点清理的"活跃"判据、
    /// 对端断开时的终态化判据），2026-09-13 加入 Paused 时两处都漏了，意味着
    /// 「暂停中的断点被当孤儿删掉」与「暂停中掉线留僵尸任务 + 泄漏并发槽」同时发生。
    /// 判据只留一份，就不会再有一处改了一处没改。
    /// </para>
    /// </summary>
    internal static bool HoldsReceiveResources(TransferStatus status)
        => status is TransferStatus.Negotiating or TransferStatus.Transferring or TransferStatus.Paused;

    /// <summary>
    /// 是否已终态——**终态即冻结**：任何后续消息都不得再改动这个任务。
    /// <para>
    /// 🔴 为什么需要：任务对象是引用，终态事件（<c>TaskCompleted</c>）把它交给 UI 之后，
    /// 一条迟到的对端消息还能改写它的 Status/ReasonCode。2026-09-13 实测踩中：
    /// 接收端因确认超时判 <c>CONFIRM_TIMEOUT</c>，随后发送方失败又回了一条**不带原因码**的 Error，
    /// 接收端照单全收把自己的原因码覆盖成 null——用户看到的原因指向了错误的真相。
    /// 「首个终态即定论」是不变量，不是优化。
    /// </para>
    /// </summary>
    internal static bool IsTerminal(TransferStatus status)
        => status is TransferStatus.Completed or TransferStatus.Skipped
            or TransferStatus.Failed or TransferStatus.Cancelled;

    /// <summary>目标路径是否正被某个活跃接收上下文使用（孤儿清理时跳过，防误删并发传输的断点）。</summary>
    private bool IsTargetOfActiveReceive(string fullPath)
        => _receiveContexts.Values.Any(c =>
            c.Task is not null
            && HoldsReceiveResources(c.Task.Status)
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

    /// <summary>
    /// 取对端设备名（确认门弹窗要显示"是谁在发"，只给 IP 用户判断不了）。
    /// 查不到（对端不在发现列表 / 未接入发现服务）返回空串——界面据此隐藏该行，不编造。
    /// </summary>
    private string ResolvePeerDeviceName(string ipPort)
    {
        if (_discovery is null)
        {
            return string.Empty;
        }

        int colon = ipPort.LastIndexOf(':');
        string ipText = colon > 0 ? ipPort[..colon] : ipPort;
        if (!IPAddress.TryParse(ipText, out IPAddress? peerIp))
        {
            return string.Empty;
        }

        return _discovery.Devices.FirstOrDefault(d => d.IPAddress.Equals(peerIp))?.Name ?? string.Empty;
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

    // 落定命名（"同名追加 (n) 序号、绝不覆盖"）已于 2026-09-13 收敛到 TransferConflictResolver：
    // 本包与手机通道 FileWebServer 曾是两份独立实现，冲突策略只改一处就会让两台设备结果不同。

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

    /// <summary>
    /// 接收任务到达终态后归还并发槽（审查 F-01 配套）。
    /// 幂等：以 <see cref="_releasedReceiveSlots"/> 记账，重复调用只减一次；
    /// 只认"已置终态且仍登记在 _receiveContexts 的任务"——发送任务/未登记任务不占槽。
    /// 漏调的后果是槽位偏保守（拒绝新握手），不会超限——宁保守勿失守。
    /// </summary>
    private void ReleaseReceiveSlot(TransferTask task)
    {
        if (task.Status is TransferStatus.Negotiating or TransferStatus.Transferring or TransferStatus.Paused)
        {
            // 防御：调用点必须已置终态。Paused 也**不是**终态——断点还在、槽还得占着，
            // 误在暂停中归还槽会让上限计数比真实活跃数少（并发闸形同虚设）。
            return;
        }

        if (!_receiveContexts.Values.Any(c => c.Task == task))
        {
            return; // 非接收任务（发送任务另有 _sendGate 管控）
        }

        bool firstRelease;
        lock (_receiveSlotLock)
        {
            firstRelease = _releasedReceiveSlots.Add(task.Id);
        }

        if (firstRelease)
        {
            Interlocked.Decrement(ref _activeReceives);
        }
    }

    private void RaiseUpdated(TransferTask task) => TaskUpdated?.Invoke(this, task);

    private void RaiseCompleted(TransferTask task)
    {
        // LOG-3：所有终态（完成/失败/取消/替换）都经过这里——任务级三字段单点落齐，
        // Duration 取 StartedAt→FinishedAt 真实传输耗时（而非 SendFileAsync 的移交耗时）
        LogTransferOutcome(task);
        TaskCompleted?.Invoke(this, task);
    }

    /// <summary>传输任务终态结构化留痕（Action/Result/Duration）；NullLogger 注入时静默。</summary>
    private void LogTransferOutcome(TransferTask task)
    {
        if (_logger is NullLogger)
        {
            return;
        }

        LogResult outcome = task.Status switch
        {
            TransferStatus.Completed => LogResult.Success,
            TransferStatus.Cancelled => LogResult.Cancelled,
            _ => LogResult.Failed,
        };
        LogLevel level = outcome switch
        {
            LogResult.Success => LogLevel.Info,
            LogResult.Cancelled => LogLevel.Info,
            _ => LogLevel.Warn,
        };
        long? durationMs = task.FinishedAt is { } finished && task.StartedAt != default
            ? (long)(finished - task.StartedAt).TotalMilliseconds
            : null;
        bool isText = task.Kind == TransferKind.Text;
        string dir = task.Direction == TransferDirection.Send ? "发送" : "接收";
        AppLog.Write(LogEntry.Create(
            level, _logger.Source,
            $"{(isText ? "文本" : "传输")}结束：{dir}「{task.FileName}」→ {task.Status}"
                + (string.IsNullOrEmpty(task.ErrorMessage) ? string.Empty : $"（{task.ErrorMessage}）")
                + (string.IsNullOrEmpty(task.ReasonCode) ? string.Empty : $" [原因码 {task.ReasonCode}]"),
            // 动作名按方向 × 种类四分（文本若记成 SendFile，日志检索会把两种完全不同的行为混在一起）
            action: task.Direction == TransferDirection.Send
                ? (isText ? "SendText" : "SendFile")
                : (isText ? "ReceiveText" : "ReceiveFile"),
            outcome: outcome,
            durationMs: durationMs));
    }

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
        string? json = ExtractRawJson(meta);
        if (string.IsNullOrEmpty(json))
            return null;
        try
        { return JsonSerializer.Deserialize<TransferMessage>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }

    /// <summary>从 WatsonTcp 的 metadata 里取出那段 JSON（键固定为 <see cref="MetaKey"/>）。</summary>
    private static string? ExtractRawJson(Dictionary<string, object>? meta)
    {
        if (meta is null)
            return null;
        if (!meta.TryGetValue(MetaKey, out object? raw))
            return null;
        return raw switch
        {
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
            _ => raw.ToString(),
        };
    }

    /// <summary>
    /// 反序列化失败时的兜底：**不再静默丢弃**（方案 §3.2）。
    /// <para>
    /// 典型场景：对端版本更新，发来本端 <see cref="TransferMessageType"/> 里还没有的类型。
    /// 旧行为是 <see cref="ParseMessage"/> 把 <c>JsonException</c> 咽掉 → 本端毫无反应、
    /// 对端傻等到超时，两端都拿不到原因。现在只要能认出类型名就明确回一个 <c>Error</c>。
    /// </para>
    /// <para>
    /// 认不出类型名（metadata 缺失 / 根本不是本协议的报文）时**只记日志、不回错误**：
    /// 那未必是协议对端，回一个"我们不认识你"的消息只会制造噪音。
    /// </para>
    /// </summary>
    private void HandleUnparsableMessage(Guid guid, string ipPort, Dictionary<string, object>? meta)
    {
        (string? typeName, string? taskId) = TryPeekEnvelope(meta);
        if (string.IsNullOrEmpty(typeName))
        {
            _logger.Warn($"收到无法解析的消息（{ipPort}）——metadata 里没有可识别的协议报文，既不处理也不回错误。");
            return;
        }

        _logger.Warn(
            $"收到本版本不支持的消息类型：{ipPort}（类型 {typeName}，任务 {taskId ?? "未知"}）"
            + "——已回错误，不静默丢弃（两端版本可能不一致）。");
        _ = SendControlAsync(guid, new TransferMessage
        {
            Type = TransferMessageType.Error,
            TaskId = taskId ?? string.Empty,
            // 这类"协议层不兼容"没有成因码（原因码表按约定只收传输语义的码），
            // 文案里带上类型名让对端/用户能直接判断"是不是版本不一致"。
            Error = $"不支持的消息类型：{typeName}。两端版本可能不一致，请更新后重试。",
        });
    }

    /// <summary>
    /// 尽力从未知报文里取出「类型名」与「任务 ID」——**不依赖能否反序列化成
    /// <see cref="TransferMessage"/>**（枚举值未知时它必失败）。
    /// </summary>
    private static (string? TypeName, string? TaskId) TryPeekEnvelope(Dictionary<string, object>? meta)
    {
        string? json = ExtractRawJson(meta);
        if (string.IsNullOrEmpty(json))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            string? type = null;
            string? taskId = null;
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                // ⚠️ 先判 ValueKind：JsonElement.GetString 对非字符串会抛异常（本仓既有实证）
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;
                if (type is null && string.Equals(property.Name, "Type", StringComparison.OrdinalIgnoreCase))
                    type = property.Value.GetString();
                else if (taskId is null && string.Equals(property.Name, "TaskId", StringComparison.OrdinalIgnoreCase))
                    taskId = property.Value.GetString();
            }

            return (type, taskId);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    // internal + InternalsVisibleTo：消毒口径有直测锁定（v5 🟡-4，反向验证见 FileTransferServiceTests）
    internal static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "unnamed";
        // 审查 v5（🟡-4）：剥离零宽不可见字符（U+200B/200C/200D/2060/FEFF）——
        // Trim/InvalidFileNameChars 均不匹配，会造成落盘名与感知名不一致（伪装面）
        fileName = TextSanitizer.StripInvisible(fileName) ?? string.Empty;
        string sanitized = string.Concat(fileName.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c));
        // Windows 保留设备名（CON/NUL/COM1…）即使带扩展名也是设备节点，落到设备而非文件——前缀 _ 规避
        sanitized = IsWindowsReservedDeviceName(sanitized) ? "_" + sanitized : sanitized;
        // 审查 v5（🟡-4）：Windows 落盘时静默剥离尾随点/空格——请求名与实际文件名错位
        // 会破坏 upload-status 指纹与孤儿 .part 清理的 Ordinal 比较，先归一化
        return sanitized.TrimEnd('.', ' ');
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

        /// <summary>本次传输生效的同名冲突策略（已在握手期把「询问」解析掉，2026-09-13 批次 P1）。</summary>
        public TransferConflictPolicy ConflictPolicy = TransferConflictPolicy.Rename;

        /// <summary>握手期算好的落定计划（落定时可能因竞态重算，见 HandleComplete）。</summary>
        public ConflictPlan Conflict;

        /// <summary>策略为「询问」且确认门开启 → 等用户在弹窗里选本次处理方式。</summary>
        public bool AskPending;

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

    /// <summary>
    /// 可重臂的异步暂停闸（协议 §4.2 PAUSE/RESUME 的本地实现）。
    /// <para>
    /// 语义：<see cref="Local"/> = 本机用户暂停；<see cref="Remote"/> = 对端要求暂停。
    /// 任一为真即「已暂停」——**两个标志分开记**是因为只有本机暂停才该跑自动取消计时
    /// （对端何时恢复由对端决定，本机无权超时取消别人的传输）。
    /// </para>
    /// <para>
    /// 实现要点：用一个「已完成」的 <see cref="TaskCompletionSource"/> 表示"通行"；
    /// 一旦暂停就换成新的未完成 TCS（重臂），恢复时完成它唤醒等待方。
    /// 判断与替换全程持同一把锁，避免"检查完 IsPaused 就被恢复、于是永远等一个已完成信号"的竞态。
    /// </para>
    /// </summary>
    private sealed class PauseGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource _signal = CreateCompletedSignal();

        public bool Local { get; private set; }

        public bool Remote { get; private set; }

        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    return Local || Remote;
                }
            }
        }

        /// <summary>设置暂停/恢复；两标志都为假时唤醒等待方。</summary>
        public void SetPaused(bool local, bool paused)
        {
            lock (_lock)
            {
                if (local)
                {
                    Local = paused;
                }
                else
                {
                    Remote = paused;
                }

                if (Local || Remote)
                {
                    if (_signal.Task.IsCompleted)
                    {
                        // 🔴 必须换成**未完成**的信号：暂停期间任何"已完成"的信号都会让等待方
                        // 立刻放行——那样暂停只改了状态、数据照流（2026-09-13 探针实测踩中：
                        // 界面显示「已暂停」，而 8MB 照样一路传完）。
                        // 反向验证：改回 CreateCompletedSignal() → Pause_* 三条用例立即变红。
                        _signal = CreatePendingSignal();
                    }
                }
                else
                {
                    _signal.TrySetResult();
                }
            }
        }

        /// <summary>取「恢复后完成」的信号（可能已完成 = 当前未暂停）。</summary>
        public Task WaitAsync()
        {
            lock (_lock)
            {
                return _signal.Task;
            }
        }

        /// <summary>
        /// 强制放行（对端断开时用）：清两个标志并唤醒等待方，让发送循环继续走到下一次
        /// <c>SendAsync</c>——由它抛出真实错误，走既有失败路径收场；
        /// 否则「暂停中 + 对端消失」会一直挂到暂停超时才有结果。
        /// </summary>
        public void ForceRelease()
        {
            lock (_lock)
            {
                Local = false;
                Remote = false;
                _signal.TrySetResult();
            }
        }

        /// <summary>未完成的信号 = 「暂停中，等恢复」。</summary>
        private static TaskCompletionSource CreatePendingSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>已完成的信号 = 「未暂停，可通行」（初始态）。</summary>
        private static TaskCompletionSource CreateCompletedSignal()
        {
            TaskCompletionSource tcs = CreatePendingSignal();
            tcs.SetResult();
            return tcs;
        }
    }

    /// <summary>接收确认门的待确认条目：Gate 完成（携带用户决定）或取消（被替换/断开/停止）。</summary>
    private sealed class PendingConfirm
    {
        public TaskCompletionSource<TransferDecision> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource? TimeoutCts { get; set; }

        public bool TimedOut { get; set; }

        public void Cancel() => Gate.TrySetCanceled();
    }
}
