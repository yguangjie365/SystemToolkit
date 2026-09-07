using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.FileTransfer.Services.Protocol;
using SystemToolkit.Core.Utilities;
using WatsonTcp;

namespace SystemToolkit.Tests;

/// <summary>
/// 文件传输服务单测：双实例真实 TCP 回环验证端到端传输 / 断点续传 / 取消 / 失败路径。
/// 接收方与发送方各起一个服务实例（不同端口），源文件为临时目录随机字节，
/// 分片设小（64KB）以在毫秒级用例内跑出多分片路径。自旧工程移植，L15 英文命名；
/// 新增 mtime 保留与接收确认门（接受/拒绝/超时）契约用例。
/// </summary>
public class FileTransferServiceTests
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>取一个空闲 TCP 端口。</summary>
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
        // REVIEW-3 G-7 后 RequirePairing 默认 true；本文件多数用例专测白名单/确认门等
        // 其它安全门，显式关闭配对门保持用例意图（配对门另有专测）
        RequirePairing = false,
    };

    /// <summary>生成含随机内容的临时文件（中文+空格文件名顺带覆盖 SanitizeFileName 路径）。</summary>
    private static string CreateSourceFile(string dir, string fileName, int sizeBytes)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        byte[] data = RandomNumberGenerator.GetBytes(sizeBytes);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stkft-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        { Directory.Delete(dir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    /// <summary>等待发送/接收双方任务终态（V0.5 新特性用例共用）。</summary>
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

    [Fact]
    public async Task StartWithoutReceiveDirectory_DefaultsToDownloadsReceived()
    {
        await using var svc = new FileTransferService();
        // 故意不设 ReceiveDirectory：验证默认值不再是桌面
        var settings = new TransferSettings { TransferPort = FreeTcpPort(), ChunkSize = ChunkSize, RequirePairing = false };

        await svc.StartAsync(settings);
        try
        {
            string expected = Path.Combine(UserFolders.GetDownloadsFolder(), "Received");
            Assert.True(Directory.Exists(expected), $"默认接收目录应已创建：{expected}");

            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            Assert.NotEqual(Path.Combine(desktop, "Received"), expected);
        }
        finally
        {
            await svc.StopAsync();
        }
    }

    [Fact]
    public async Task SendBeforeStart_ThrowsInvalidOperation()
    {
        await using var svc = new FileTransferService();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendFileAsync("anything.bin", "127.0.0.1", 1));
    }

    [Fact]
    public async Task SendMissingFile_ThrowsFileNotFound()
    {
        int port = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var svc = new FileTransferService();
            await svc.StartAsync(MakeSettings(port, Path.Combine(dir, "recv")));
            string missing = Path.Combine(dir, "不存在.bin");
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => svc.SendFileAsync(missing, "127.0.0.1", port));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task StartAsync_Twice_ThrowsInvalidOperation()
    {
        int port = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var svc = new FileTransferService();
            await svc.StartAsync(MakeSettings(port, Path.Combine(dir, "recv")));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.StartAsync(MakeSettings(port, Path.Combine(dir, "recv"))));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Start_CreatesReceiveDirectory()
    {
        int port = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string recv = Path.Combine(dir, "recv-created");
            await using var svc = new FileTransferService();
            await svc.StartAsync(MakeSettings(port, recv));
            Assert.True(Directory.Exists(recv));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task CancelUnknownTask_NoThrow()
    {
        await using var svc = new FileTransferService();
        await svc.CancelAsync("nonexistent-task-id");
    }

    [Fact]
    public async Task EndToEnd_MultiChunk_ByteIdentical_BothSidesComplete()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "测试 文件.bin", 300 * 1024);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            TransferTask task = await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));

            // 双方任务均完成
            Assert.Equal(TransferStatus.Completed, sendFinal.Status);
            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            Assert.Equal(task.Id, recvFinal.Id);
            Assert.Equal(sourceBytes.LongLength, recvFinal.TransferredBytes);
            Assert.Equal(TransferDirection.Receive, recvFinal.Direction);

            // 接收文件内容与源文件逐字节一致
            string recvPath = Path.Combine(Path.Combine(dir, "recv"), "测试 文件.bin");
            Assert.True(File.Exists(recvPath), $"接收文件应存在：{recvPath}");
            byte[] recvBytes = await File.ReadAllBytesAsync(recvPath);
            Assert.Equal(sourceBytes.Length, recvBytes.Length);
            Assert.True(sourceBytes.AsSpan().SequenceEqual(recvBytes), "接收内容应与源文件逐字节一致");

            // 终态任务已从活跃列表移除
            Assert.DoesNotContain(sender.ActiveTasks, t => t.Id == task.Id);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Resume_AfterInterrupt_ContinuesFromPart_ByteIdentical()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            const int totalSize = 1024 * 1024; // 16 个分片：取消几乎必然落在中段
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "resume.bin", totalSize);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            string recvDir = Path.Combine(dir, "recv");
            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, recvDir));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            // 阶段一：传输中断（首个分片进度事件时取消），接收端应保留 .part 断点
            var phase1 = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancelRequested = false;
            sender.TaskUpdated += (_, t) =>
            {
                if (!cancelRequested && t.TransferredBytes > 0 && t.Status == TransferStatus.Transferring)
                {
                    cancelRequested = true;
                    _ = sender.CancelAsync(t.Id);
                }
            };
            receiver.TaskCompleted += (_, t) => phase1.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask interrupted = await phase1.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // 取消控制消息与连接关闭谁先到都有可能：Cancelled（Cancel 消息）/ Failed（断开）均为终态且保留断点
            Assert.True(interrupted.Status is TransferStatus.Cancelled or TransferStatus.Failed,
                $"实际状态：{interrupted.Status}");

            string[] parts = Directory.GetFiles(recvDir, "*.part");
            Assert.True(parts.Length == 1, $"应恰好保留 1 个 .part 断点，实际：{string.Join(", ", parts)}");
            long partLength = new FileInfo(parts[0]).Length;
            Assert.True(partLength > 0 && partLength < totalSize, $"断点应非空且未完成：{partLength}");

            // 阶段二：重发同一文件，应从断点继续（首条接收更新 = 断点大小）
            var phase2 = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            long firstResumeFrom = -1;
            receiver.TaskUpdated += (_, t) =>
            {
                if (firstResumeFrom < 0 && t.Direction == TransferDirection.Receive)
                {
                    firstResumeFrom = t.TransferredBytes;
                }
            };
            receiver.TaskCompleted += (_, t) => phase2.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask final = await phase2.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(TransferStatus.Completed, final.Status);
            Assert.Equal(partLength, firstResumeFrom);

            // 最终文件以原名落定（.part 已消费），内容与源逐字节一致，且不留 .part
            string finalPath = Path.Combine(recvDir, "resume.bin");
            byte[] recvBytes = await File.ReadAllBytesAsync(finalPath);
            Assert.True(sourceBytes.AsSpan().SequenceEqual(recvBytes), "续传后内容应与源文件逐字节一致");
            Assert.Empty(Directory.GetFiles(recvDir, "*.part"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task CancelDuringTransfer_TaskCancelled()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            // 1MB / 64KB = 16 个分片：在首个分片事件里取消，发送循环在下一轮 ct 检查处退出
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "cancel.bin", 1024 * 1024);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            var sendDone = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancelRequested = false;
            sender.TaskUpdated += (_, t) =>
            {
                if (!cancelRequested && t.TransferredBytes > 0 && t.Status == TransferStatus.Transferring)
                {
                    cancelRequested = true;
                    _ = sender.CancelAsync(t.Id);
                }
            };
            sender.TaskCompleted += (_, t) => sendDone.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            TransferTask final = await sendDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(cancelRequested, "取消应发生在传输进度事件中");
            Assert.Equal(TransferStatus.Cancelled, final.Status);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task DeadPort_TaskFailed_CompletedEventRaised()
    {
        int sendPort = FreeTcpPort();
        int deadPort = FreeTcpPort(); // 已释放的端口，无监听
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "fail.bin", 16 * 1024);

            await using var sender = new FileTransferService();
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            var failed = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => failed.TrySetResult(t);

            TransferTask task = await sender.SendFileAsync(sourcePath, "127.0.0.1", deadPort);

            TransferTask final = await failed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(task.Id, final.Id);
            Assert.Equal(TransferStatus.Failed, final.Status);
            Assert.False(string.IsNullOrEmpty(final.ErrorMessage));
            Assert.DoesNotContain(sender.ActiveTasks, t => t.Id == task.Id);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task KnownPeerWhitelist_UnknownRejected_KnownAccepted()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "auth.bin", 64 * 1024);

            // 设备发现列表为空 → 127.0.0.1 是「未知设备」，RequireKnownPeer 默认开启应拒绝握手
            var discovery = new FakeDiscovery();
            await using var receiver = new FileTransferService(discovery);
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            var failed = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => failed.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask rejected = await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, rejected.Status);
            Assert.Contains("来源设备", rejected.ErrorMessage);
            // 被拒绝的传输不应在接收目录留下任何文件
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "recv")));

            // 把对端加入在线列表后，同一来源可正常完成传输
            discovery.Devices = new[]
            {
                new DiscoveredDevice
                {
                    DeviceId = "peer-1",
                    Name = "对端",
                    IPAddress = IPAddress.Parse("127.0.0.1"),
                    TransferPort = recvPort,
                    WebPort = 1,
                    LastSeen = DateTimeOffset.UtcNow, // LastSeen 在阈值内即 IsOnline=true
                },
            };
            var done = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => done.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask completed = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Completed, completed.Status);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task MaliciousOversizedChunk_RejectedWithError()
    {
        int recvPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));

            var recvFailed = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TaskCompleted += (_, t) => recvFailed.TrySetResult(t);

            // 模拟恶意客户端：合法握手后发送越界分片（Offset 超过 FileSize）
            using var client = new WatsonTcpClient("127.0.0.1", recvPort);
            var ackTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Events.MessageReceived += (_, e) =>
            {
                TransferMessage? tm = ParseTestMessage(e.Metadata);
                if (tm is null)
                {
                    return;
                }

                switch (tm.Type)
                {
                    case TransferMessageType.HandshakeAck:
                        ackTcs.TrySetResult(tm);
                        break;
                    case TransferMessageType.Error:
                        errTcs.TrySetResult(tm.Error);
                        break;
                }
            };
            client.Connect();

            await client.SendAsync(string.Empty, BuildTestMetadata(new TransferMessage
            {
                Type = TransferMessageType.Handshake,
                TaskId = "evil-task",
                FileName = "evil.bin",
                FileSize = 1000,
                ChunkSize = ChunkSize,
                TotalChunks = 1,
            }), CancellationToken.None);

            await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await client.SendAsync(new byte[16], BuildTestMetadata(new TransferMessage
            {
                Type = TransferMessageType.Chunk,
                TaskId = "evil-task",
                Offset = 2000, // > FileSize(1000)，越界
                ChunkIndex = 0,
            }), 0, CancellationToken.None);

            string? error = await errTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("分片越界", error);

            // 接收任务以失败终态收场
            TransferTask failed = await recvFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TransferStatus.Failed, failed.Status);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ===================== V0.5 新特性（2026-09-06 用户裁定） =====================

    [Fact]
    public async Task ModifiedTime_PreservedOnReceivedFile()
    {
        // 2026-09-06 协议扩展：TCP 桌面通道还原文件修改时间（手机照片原始时间跨设备保留）
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "photo.bin", 128 * 1024);
            var expectedUtc = new DateTime(2025, 1, 15, 8, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(sourcePath, expectedUtc);

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            await recvDone.WaitAsync(TimeSpan.FromSeconds(30));

            string recvPath = Path.Combine(Path.Combine(dir, "recv"), "photo.bin");
            Assert.True(File.Exists(recvPath));
            DateTime actualUtc = File.GetLastWriteTimeUtc(recvPath);
            Assert.True((actualUtc - expectedUtc).Duration() <= TimeSpan.FromSeconds(1),
                $"修改时间应被还原为 {expectedUtc:O}，实际 {actualUtc:O}");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ReceiveConfirm_Accepted_TransferCompletes()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "confirm.bin", 128 * 1024);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 15;

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            var requested = new TaskCompletionSource<TransferRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TransferRequested += (_, e) =>
            {
                requested.TrySetResult(e);
                _ = receiver.RespondTransferAsync(e.TaskId, accept: true);
            };

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            TransferRequestEventArgs request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("confirm.bin", request.FileName);
            Assert.Equal(sourceBytes.LongLength, request.FileSize);

            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Completed, sendFinal.Status);
            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            Assert.True(File.Exists(Path.Combine(dir, "recv", "confirm.bin")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ReceiveConfirm_Rejected_SenderFails_NoFilesLeft()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "rejected.bin", 128 * 1024);

            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 15;

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            var requested = new TaskCompletionSource<TransferRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TransferRequested += (_, e) =>
            {
                requested.TrySetResult(e);
                _ = receiver.RespondTransferAsync(e.TaskId, accept: false);
            };

            var failed = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => failed.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            await requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            TransferTask sendFinal = await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, sendFinal.Status);
            Assert.Contains("拒绝", sendFinal.ErrorMessage);

            // 确认门阶段被拒：不应创建任何 .part 或落定文件
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "recv")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ReceiveConfirm_Timeout_AutoRejected_NoFilesLeft()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "timeout.bin", 128 * 1024);

            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 1; // 不响应 → 1 秒后自动拒绝

            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            var requested = new TaskCompletionSource<TransferRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TransferRequested += (_, e) => requested.TrySetResult(e); // 收到请求但不回复

            var failed = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => failed.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);

            await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            TransferTask sendFinal = await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, sendFinal.Status);
            Assert.Contains("未响应确认", sendFinal.ErrorMessage);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "recv")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ===================== V0.5 批次二：TCP 配对码（PairingService 统一模型） =====================

    private static TransferSettings MakePairingSettings(int transferPort, string receiveDir) => new()
    {
        TransferPort = transferPort,
        ChunkSize = ChunkSize,
        ReceiveDirectory = receiveDir,
        RequirePairing = true,
    };

    [Fact]
    public async Task RequirePairing_NoCode_RejectedZeroFiles()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "need-code.bin", 64 * 1024);

            await using var receiver = new FileTransferService(pairing: new PairingService());
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakePairingSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            var failed = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => failed.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            TransferTask final = await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, final.Status);
            Assert.Contains("配对码", final.ErrorMessage);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "recv")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task RequirePairing_WrongCode_RejectedZeroFiles()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string sourcePath = CreateSourceFile(Path.Combine(dir, "src"), "wrong-code.bin", 64 * 1024);
            var receiverPairing = new PairingService();
            _ = receiverPairing.CurrentCode; // 接收端已有在展配对码

            await using var receiver = new FileTransferService(pairing: receiverPairing);
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakePairingSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            var failed = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => failed.TrySetResult(t);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort, pairCode: "AAAAAA");
            TransferTask final = await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, final.Status);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "recv")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task RequirePairing_ValidCode_FirstSucceeds_SubsequentRidePairedIp()
    {
        // 统一配对语义：首个文件消费一次性码并记忆发送方 IP，同批次后续文件免码
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            string first = CreateSourceFile(Path.Combine(dir, "src"), "paired-1.bin", 64 * 1024);
            string second = CreateSourceFile(Path.Combine(dir, "src"), "paired-2.bin", 64 * 1024);
            var receiverPairing = new PairingService();

            await using var receiver = new FileTransferService(pairing: receiverPairing);
            await using var sender = new FileTransferService();
            await receiver.StartAsync(MakePairingSettings(recvPort, Path.Combine(dir, "recv")));
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            // 第一个文件：携带接收端当前配对码
            TransferTask t1 = await sender.SendFileAsync(first, "127.0.0.1", recvPort, receiverPairing.CurrentCode);
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Completed, sendFinal.Status);
            Assert.Equal(t1.Id, (await recvDone.WaitAsync(TimeSpan.FromSeconds(30))).Id);
            Assert.True(File.Exists(Path.Combine(dir, "recv", "paired-1.bin")));

            // 第二个文件：免码（发送方 IP 已配对）
            var done2 = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TaskCompleted += (_, t) => done2.TrySetResult(t);
            await sender.SendFileAsync(second, "127.0.0.1", recvPort);
            TransferTask final2 = await done2.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Completed, final2.Status);
            Assert.True(File.Exists(Path.Combine(dir, "recv", "paired-2.bin")));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>解析 WatsonTcp metadata 中的协议消息（值可能为 string 或 JsonElement）。</summary>
    private static TransferMessage? ParseTestMessage(Dictionary<string, object>? meta)
    {
        if (meta is null || !meta.TryGetValue("m", out object? raw))
        {
            return null;
        }

        string? json = raw switch
        {
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
            _ => raw.ToString(),
        };
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<TransferMessage>(json);
    }

    private static Dictionary<string, object> BuildTestMetadata(TransferMessage tm)
        => new() { ["m"] = JsonSerializer.Serialize(tm) };

    /// <summary>可控设备列表的发现服务桩（用于来源白名单用例）。</summary>
    private sealed class FakeDiscovery : IDeviceDiscoveryService
    {
        public IReadOnlyList<DiscoveredDevice> Devices { get; set; } = Array.Empty<DiscoveredDevice>();

        public string LocalDeviceId => "fake-device";

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(TransferSettings settings, CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
