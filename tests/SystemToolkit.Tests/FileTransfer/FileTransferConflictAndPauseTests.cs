using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.FileTransfer.Services.Protocol;
using WatsonTcp;

namespace SystemToolkit.Tests;

/// <summary>
/// 文件互传批次 P1 契约用例：同名冲突策略（⑥）/ 原因码（⑧）/ 双向暂停恢复（⑨）。
/// <para>
/// 全部走**双实例真实 TCP 回环**（与 <c>FileTransferServiceTests</c> 同款夹具）：
/// 这几条契约的要害恰恰在两侧协同——冲突策略决定"收不收/怎么写"、暂停要两侧状态一致，
/// 只测纯函数会漏掉"一侧改了另一侧没跟上"这类真实缺陷。
/// </para>
/// </summary>
public class FileTransferConflictAndPauseTests
{
    private const int ChunkSize = 64 * 1024;

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static TransferSettings MakeSettings(int transferPort, string receiveDir) => new()
    {
        TransferPort = transferPort,
        ChunkSize = ChunkSize,
        ReceiveDirectory = receiveDir,
        MaxConcurrentTransfers = 2,
        RequirePairing = false, // 本文件专测冲突/暂停，配对门另有专测
    };

    private static string CreateSourceFile(string dir, string fileName, int sizeBytes)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(sizeBytes));
        return path;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stkft-p1-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        { Directory.Delete(dir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private static void HookBothDone(
        FileTransferService receiver, FileTransferService sender,
        out Task<TransferTask> recvDone, out Task<TransferTask> sendDone)
    {
        var recvTcs = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendTcs = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TaskCompleted += (_, t) => recvTcs.TrySetResult(t);
        sender.TaskCompleted += (_, t) => sendTcs.TrySetResult(t);
        recvDone = recvTcs.Task;
        sendDone = sendTcs.Task;
    }

    /// <summary>
    /// 轮询等待条件成立（暂停/恢复是跨进程消息往返，固定 sleep 不是可靠的同步手段：
    /// sleep 太短会假失败，太长会拖慢用例）。
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(5);
        }
        return condition();
    }

    private static TransferMessage? ParseTestMessage(Dictionary<string, object>? meta)
        => meta is not null && meta.TryGetValue("m", out object? raw)
            ? JsonSerializer.Deserialize<TransferMessage>(raw?.ToString() ?? string.Empty)
            : null;

    private static Dictionary<string, object> BuildTestMetadata(TransferMessage tm)
        => new() { ["m"] = JsonSerializer.Serialize(tm) };

    // ── ⑥ 冲突策略（纯函数裁决处） ──

    /// <summary>
    /// 落定裁决处的直测：这是两条接收通道（电脑 ↔ 电脑 / 手机上传）共用的唯一裁决点，
    /// 所以它必须自己就是可测的纯函数，而不是塞在服务内部的两个私有分支里。
    /// </summary>
    [Fact]
    public void Resolve_ByPolicy_ReturnsExpectedAction()
    {
        string dir = NewTempDir();
        try
        {
            string existing = Path.Combine(dir, "a.bin");
            File.WriteAllBytes(existing, new byte[8]);

            // 目标不存在 → 永远是 Fresh（任何策略都不该改动它）
            Assert.Equal(
                ConflictResolution.Fresh,
                TransferConflictResolver.Resolve(dir, "missing.bin", TransferConflictPolicy.Skip).Kind);
            Assert.Equal(
                ConflictResolution.Fresh,
                TransferConflictResolver.Resolve(dir, "missing.bin", TransferConflictPolicy.Overwrite).Kind);

            // 目标存在 → 按策略分流
            ConflictPlan renamed = TransferConflictResolver.Resolve(dir, "a.bin", TransferConflictPolicy.Rename);
            Assert.Equal(ConflictResolution.Renamed, renamed.Kind);
            Assert.Equal("a (1).bin", Path.GetFileName(renamed.TargetPath));

            ConflictPlan skipped = TransferConflictResolver.Resolve(dir, "a.bin", TransferConflictPolicy.Skip);
            Assert.Equal(ConflictResolution.Skip, skipped.Kind);
            Assert.False(skipped.WritesFile);

            ConflictPlan overwritten = TransferConflictResolver.Resolve(dir, "a.bin", TransferConflictPolicy.Overwrite);
            Assert.Equal(ConflictResolution.Overwrite, overwritten.Kind);
            Assert.Equal(existing, overwritten.TargetPath);

            // Ask 不该走到这里（调用方必须先解析）；真漏解析了，也必须按最安全的改名处理，
            // 绝不能因一个未解析的枚举值就替用户覆盖文件。
            ConflictPlan accidental = TransferConflictResolver.Resolve(dir, "a.bin", TransferConflictPolicy.Ask);
            Assert.Equal(ConflictResolution.Renamed, accidental.Kind);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>项目对已有文件的口径是「绝不覆盖」→ 默认策略只能是自动改名。</summary>
    [Fact]
    public void DefaultPolicy_IsRename_NeverOverwrite()
        => Assert.Equal(TransferConflictPolicy.Rename, new TransferSettings().ConflictPolicy);

    // ── ⑥ 冲突策略（端到端） ──

    /// <summary>默认策略：同名的旧文件原样保留，新到的一份改名落定。</summary>
    [Fact]
    public async Task Conflict_DefaultRename_KeepsExistingFile_AndAddsNumberedOne()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string recvDir = Path.Combine(dir, "recv");
            Directory.CreateDirectory(recvDir);
            string oldPath = Path.Combine(recvDir, "same.bin");
            File.WriteAllBytes(oldPath, new byte[] { 1, 2, 3 });

            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "same.bin", 128 * 1024);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, recvDir));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(oldPath)); // 旧文件一字未动
            string numbered = Path.Combine(recvDir, "same (1).bin");
            Assert.True(File.Exists(numbered), "默认策略应把新文件落成 same (1).bin");
            byte[] numberedBytes = await File.ReadAllBytesAsync(numbered);
            Assert.True(sourceBytes.AsSpan().SequenceEqual(numberedBytes));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>策略=跳过：接收端在**握手期**就拒绝，连一个字节都不收（避免白传一趟）。</summary>
    [Fact]
    public async Task Conflict_Skip_RejectsAtHandshake_WithReasonCode()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string recvDir = Path.Combine(dir, "recv");
            Directory.CreateDirectory(recvDir);
            string oldPath = Path.Combine(recvDir, "same.bin");
            File.WriteAllBytes(oldPath, new byte[] { 9, 9, 9 });

            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "same.bin", 256 * 1024);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, recvDir);
            recvSettings.ConflictPolicy = TransferConflictPolicy.Skip;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(TransferStatus.Failed, sendFinal.Status);
            Assert.Equal(TransferReasonCodes.ConflictSkip, sendFinal.ReasonCode); // ⑧ 原因码随错误回传
            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(oldPath));
            Assert.False(File.Exists(Path.Combine(recvDir, "same (1).bin")));
            Assert.Empty(Directory.GetFiles(recvDir, "*.part")); // 握手期就拒绝 → 不留断点
            // 接收端连任务都不该建（没有开始接收这件事，就不该在列表里留下一条"失败"）
            Assert.DoesNotContain(receiver.ActiveTasks, t => t.FileName == "same.bin");
            _ = recvDone; // 接收端无任务终态事件，仅保留引用以示预期
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>策略=覆盖：用户明确选了破坏性动作 → 旧文件被替换，且不留序号副本。</summary>
    [Fact]
    public async Task Conflict_Overwrite_ReplacesExistingFile()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string recvDir = Path.Combine(dir, "recv");
            Directory.CreateDirectory(recvDir);
            string oldPath = Path.Combine(recvDir, "same.bin");
            File.WriteAllBytes(oldPath, new byte[] { 7, 7, 7, 7 });

            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "same.bin", 128 * 1024);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, recvDir);
            recvSettings.ConflictPolicy = TransferConflictPolicy.Overwrite;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            byte[] overwrittenBytes = await File.ReadAllBytesAsync(oldPath);
            Assert.True(sourceBytes.AsSpan().SequenceEqual(overwrittenBytes), "覆盖策略下目标文件应为新内容");
            Assert.False(File.Exists(Path.Combine(recvDir, "same (1).bin")), "覆盖策略不该再产生序号副本");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>策略=询问 + 确认门开启：弹窗拿到同名事实与当前策略，用户选「跳过」按跳过收场（两侧一致）。</summary>
    [Fact]
    public async Task Conflict_Ask_UserPicksSkip_BothSidesReportSkipped()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string recvDir = Path.Combine(dir, "recv");
            Directory.CreateDirectory(recvDir);
            string oldPath = Path.Combine(recvDir, "same.bin");
            File.WriteAllBytes(oldPath, new byte[] { 5, 5 });

            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "same.bin", 128 * 1024);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, recvDir);
            recvSettings.ConflictPolicy = TransferConflictPolicy.Ask;
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 20;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            var requested = new TaskCompletionSource<TransferRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TransferRequested += (_, e) =>
            {
                requested.TrySetResult(e);
                _ = receiver.RespondTransferAsync(e.TaskId, TransferDecision.AcceptWith(TransferConflictPolicy.Skip));
            };

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            // 弹窗信息：同名事实 + 当前策略（这两条正是"接不接"要看的）
            TransferRequestEventArgs args = await requested.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(args.TargetExists);
            Assert.Equal(TransferConflictPolicy.Ask, args.ConflictPolicy);
            Assert.Equal(ConflictResolution.Renamed, args.ConflictAction); // 询问未决前的保守占位
            Assert.Equal(recvDir, args.ReceiveDirectory);
            Assert.NotNull(args.AvailableFreeBytes);

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Skipped, recvFinal.Status); // 不谎报"已完成"
            Assert.Equal(TransferReasonCodes.ConflictSkip, recvFinal.ReasonCode);
            Assert.Equal(TransferStatus.Failed, sendFinal.Status);
            Assert.Equal(TransferReasonCodes.ConflictSkip, sendFinal.ReasonCode);
            Assert.Equal(new byte[] { 5, 5 }, await File.ReadAllBytesAsync(oldPath));
            Assert.False(File.Exists(Path.Combine(recvDir, "same (1).bin")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>策略=询问但确认门关闭：无从询问 → 降级为自动改名（不弹窗、不阻断、更不覆盖）。</summary>
    [Fact]
    public async Task Conflict_Ask_WithoutConfirmGate_FallsBackToRename()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string recvDir = Path.Combine(dir, "recv");
            Directory.CreateDirectory(recvDir);
            File.WriteAllBytes(Path.Combine(recvDir, "same.bin"), new byte[] { 4 });

            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "same.bin", 128 * 1024);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, recvDir);
            recvSettings.ConflictPolicy = TransferConflictPolicy.Ask;
            recvSettings.RequireReceiveConfirmation = false; // 关键：没有弹窗可问
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            Assert.True(File.Exists(Path.Combine(recvDir, "same (1).bin")), "无法询问时应降级为自动改名");
            Assert.Equal(new byte[] { 4 }, await File.ReadAllBytesAsync(Path.Combine(recvDir, "same.bin")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ── ⑧ 原因码 ──

    /// <summary>确认门超时 → 两侧都拿到 CONFIRM_TIMEOUT 这个稳定判据（而不是去匹配中文措辞）。</summary>
    [Fact]
    public async Task ReasonCode_ConfirmTimeout_PropagatedToSender()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "timeout.bin", 64 * 1024);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 1; // 短超时，别让用例等 30 秒
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort); // 故意不响应确认

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferReasonCodes.ConfirmTimeout, recvFinal.ReasonCode);
            Assert.Equal(TransferReasonCodes.ConfirmTimeout, sendFinal.ReasonCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>用户拒绝 → USER_REJECT（与"超时"必须是两个码：一个要追责到人，一个要追责到界面）。</summary>
    [Fact]
    public async Task ReasonCode_UserReject_DistinctFromTimeout()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "reject.bin", 64 * 1024);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 20;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            receiver.TransferRequested += (_, e) => _ = receiver.RespondTransferAsync(e.TaskId, accept: false);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferReasonCodes.UserReject, recvFinal.ReasonCode);
            Assert.Equal(TransferReasonCodes.UserReject, sendFinal.ReasonCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>「仍持有接收资源」的状态集只此一份：加了 Paused 却漏在某处，正是本批次踩到的坑。</summary>
    [Theory]
    [InlineData(TransferStatus.Negotiating, true)]
    [InlineData(TransferStatus.Transferring, true)]
    [InlineData(TransferStatus.Paused, true)]
    [InlineData(TransferStatus.Pending, false)]
    [InlineData(TransferStatus.Completed, false)]
    [InlineData(TransferStatus.Skipped, false)]
    [InlineData(TransferStatus.Failed, false)]
    [InlineData(TransferStatus.Cancelled, false)]
    public void HoldsReceiveResources_IncludesPaused(TransferStatus status, bool expected)
        => Assert.Equal(expected, FileTransferService.HoldsReceiveResources(status));

    // ── ⑨ 暂停 / 恢复 ──

    /// <summary>
    /// 本机暂停 → 数据真的停（进度冻结）→ 恢复后跑完且逐字节一致。
    /// <para>
    /// 判据用「进度冻结」而不是只看状态字段：状态是软件自己写的，进度是数据流的真实结果——
    /// 只断言状态等于自证清白（实现改成"只改状态不真停"也一样会绿）。这是本项目的反向验证纪律。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pause_FreezesProgress_ResumeCompletes_ByteIdentical()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            // 文件足够大以保证暂停发生时传输远未结束（8MB / 64KB = 128 片）
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "pause.bin", 8 * 1024 * 1024);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            string recvDir = Path.Combine(dir, "recv");
            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, recvDir));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            TransferTask task = await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            Assert.True(await sender.PauseTaskAsync(task.Id), "传输中的任务应可暂停");
            Assert.Equal(TransferStatus.Paused, task.Status);

            await Task.Delay(300);
            long frozen = task.TransferredBytes;
            await Task.Delay(400);
            Assert.Equal(frozen, task.TransferredBytes); // 暂停期间进度必须一动不动
            Assert.NotEqual(task.FileSize, frozen); // 且确实还没传完（否则本用例没测到暂停）

            Assert.True(await sender.ResumeTaskAsync(task.Id));
            Assert.Equal(TransferStatus.Transferring, task.Status);

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(60));
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferStatus.Completed, sendFinal.Status);
            Assert.Equal(TransferStatus.Completed, recvFinal.Status);

            byte[] recvBytes = await File.ReadAllBytesAsync(Path.Combine(recvDir, "pause.bin"));
            Assert.True(sourceBytes.AsSpan().SequenceEqual(recvBytes), "暂停恢复后内容必须逐字节一致");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 双向：**接收方**暂停 → 发送方必须真的停下（且状态如实转「已暂停」，不显示"传输中"），
    /// 接收方恢复后跑完。这条走的是协议 PAUSE/RESUME 的对端分支，与上一条（本机发起）互补。
    /// <para>
    /// 用 16MB 文件并在**首片落地后立即**暂停：暂停是"下一次分片边界"生效的，
    /// 文件太小会让传输在暂停指令送达前就跑完——那样用例测到的不是暂停，而是"来不及暂停"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pause_InitiatedByReceiver_SuspendsSender_ThenResumeCompletes()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "peer-pause.bin", 16 * 1024 * 1024);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            TransferTask sendTask = await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            // 等接收端真的开始收（首片落地）再暂停，避免"传完了才暂停"
            Assert.True(
                await WaitUntilAsync(() => receiver.ActiveTasks.Any(t => t.TransferredBytes > 0), TimeSpan.FromSeconds(20)),
                "接收端应开始收数据");

            TransferTask recvTask = receiver.ActiveTasks.First(t => t.TransferredBytes > 0);
            Assert.True(await receiver.PauseTaskAsync(recvTask.Id), "接收中任务应可暂停");

            // 对端暂停：发送方状态必须如实转「已暂停」，且真的不再发数据
            Assert.True(
                await WaitUntilAsync(() => sendTask.Status == TransferStatus.Paused, TimeSpan.FromSeconds(10)),
                $"发送方应收到暂停并转「已暂停」，实际 {sendTask.Status}/{sendTask.TransferredBytes}");
            Assert.True(sendTask.PausedByPeer);

            long frozen = sendTask.TransferredBytes;
            await Task.Delay(400);
            Assert.Equal(frozen, sendTask.TransferredBytes); // 数据流确实停了
            Assert.NotEqual(sendTask.FileSize, frozen); // 且确实还没传完（否则本用例没测到暂停）

            Assert.True(await receiver.ResumeTaskAsync(recvTask.Id));
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(60));
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferStatus.Completed, sendFinal.Status);
            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            byte[] peerPauseBytes = await File.ReadAllBytesAsync(Path.Combine(dir, "recv", "peer-pause.bin"));
            Assert.True(sourceBytes.AsSpan().SequenceEqual(peerPauseBytes));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 暂停超时自动取消：对端无法区分「对面在暂停」与「对面挂了」，所以上限是必需的——
    /// 没有它，一次忘掉的暂停会让对端永久挂着、本机接收槽也一直被占。
    /// </summary>
    [Fact]
    public async Task PauseTimeout_AutoCancels_WithReasonCode()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "pause-timeout.bin", 8 * 1024 * 1024);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            sender.ConfigurePauseTimeoutForTest(TimeSpan.FromMilliseconds(400));

            TransferTask task = await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            Assert.True(await sender.PauseTaskAsync(task.Id));

            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Cancelled, sendFinal.Status);
            Assert.Equal(TransferReasonCodes.PauseTimeout, sendFinal.ReasonCode);

            // 接收端由对端取消路径收场，且**不**把超时误记成用户取消
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Cancelled, recvFinal.Status);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 暂停中对端**硬断开**（不发 Cancel）：接收任务必须被终态化（不许永远停在「已暂停」），
    /// 且接收并发槽被归还。
    /// <para>
    /// 用裸 WatsonTcp 客户端而不是另一个 FileTransferService：后者的 StopAsync 会先发 Cancel，
    /// 那样测到的是"取消路径"而不是"断开路径"——而"暂停中掉线"漏进终态化判据正是本批次修掉的缺陷。
    /// 槽上限压到 1 放大效应：不归还则后续握手必被拒。
    /// </para>
    /// </summary>
    [Fact]
    public async Task DisconnectWhilePaused_FinalizesReceiveTask_AndReleasesSlot()
    {
        int recvPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string recvDir = Path.Combine(dir, "recv");
            await using var receiver = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, recvDir);
            recvSettings.MaxConcurrentReceives = 1;
            await receiver.StartAsync(recvSettings);

            var recvDone = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TaskCompleted += (_, t) => recvDone.TrySetResult(t);

            using var client = new WatsonTcpClient("127.0.0.1", recvPort);
            var ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Events.MessageReceived += (_, e) =>
            {
                if (ParseTestMessage(e.Metadata)?.Type == TransferMessageType.HandshakeAck)
                {
                    ackTcs.TrySetResult(true);
                }
            };
            client.Connect();

            await client.SendAsync(string.Empty, BuildTestMetadata(new TransferMessage
            {
                Type = TransferMessageType.Handshake,
                TaskId = "paused-drop",
                FileName = "drop.bin",
                FileSize = 64L * 1024 * 1024,
                ChunkSize = ChunkSize,
                TotalChunks = 1024,
            }), CancellationToken.None);

            Assert.True(await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(15)), "握手应被接受");

            TransferTask recvTask = receiver.ActiveTasks.Single(t => t.Id == "paused-drop");
            Assert.Equal(TransferStatus.Transferring, recvTask.Status);
            Assert.True(await receiver.PauseTaskAsync(recvTask.Id));
            Assert.Equal(TransferStatus.Paused, recvTask.Status);

            client.Dispose(); // 硬断开

            TransferTask recvFinal = await recvDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(TransferStatus.Failed, recvFinal.Status);
            Assert.Equal(TransferReasonCodes.PeerDisconnected, recvFinal.ReasonCode);

            // 槽确实归还：上限为 1 时再传一次仍能完成
            await using var sender2 = new FileTransferService();
            await sender2.StartAsync(MakeSettings(FreeTcpPort(), Path.Combine(dir, "unused2")));
            HookBothDone(receiver, sender2, out Task<TransferTask> recvDone2, out Task<TransferTask> sendDone2);
            string second = CreateSourceFile(Path.Combine(dir, "src2"), "after-drop.bin", 128 * 1024);
            await sender2.SendFileAsync(second, "127.0.0.1", recvPort);
            TransferTask recvFinal2 = await recvDone2.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Completed, recvFinal2.Status);
            _ = sendDone2;
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>暂停中的任务仍持有断点资源 → 不能被视为"活跃任务已结束"（避免断点被当孤儿清掉）。</summary>
    [Fact]
    public void HoldsReceiveResources_PausedIsNotTerminal()
    {
        Assert.True(FileTransferService.HoldsReceiveResources(TransferStatus.Paused));
        Assert.False(FileTransferService.HoldsReceiveResources(TransferStatus.Skipped));
    }
}
