using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// LOG-3 操作边界三字段行为测试（批次 2）：网络变更服务（退出码/校验拒绝映射）、
/// VSS 创建回退（无 UAC 路径：helper 缺失 → Failed）、互传任务终态单咽喉
/// （真实回环：SendFile/ReceiveFile + 真实传输 Duration）。
/// 本文件全部用例前后复位总线（parallelizeTestCollections=false，串行安全）。
/// </summary>
public class LogFieldBoundaryTests : IDisposable
{
    public LogFieldBoundaryTests() => AppLog.Reset();

    public void Dispose() => AppLog.Reset();

    private sealed class FakeRunner : ICommandRunner
    {
        public int ExitCode { get; set; }

        public Task<int> RunAsync(string fileName, string arguments, Action<string> onLine,
            CancellationToken ct = default, TimeSpan? timeout = null)
            => Task.FromResult(ExitCode);
    }

    /* ───────────────────── 网络：退出码 → Result 映射 ───────────────────── */

    [Fact]
    public async Task SetDns_Success_LogsSuccessWithDuration()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);
        var svc = new NetConfigService(new FakeRunner { ExitCode = 0 }, new BusLogger("net"));

        int exit = await svc.SetDnsAsync("以太网", "223.5.5.5", null, _ => { });

        Assert.Equal(0, exit);
        LogEntry entry = Assert.Single(capture.Entries, e => e.Action == "SetDns");
        Assert.Equal(LogResult.Success, entry.Outcome);
        Assert.Equal("net", entry.Source);
        Assert.NotNull(entry.DurationMs);
    }

    [Fact]
    public async Task SetDns_NonZeroExit_LogsFailed()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);
        var svc = new NetConfigService(new FakeRunner { ExitCode = 1 }, new BusLogger("net"));

        int exit = await svc.SetDnsAsync("以太网", null, null, _ => { }); // 恢复自动路径

        Assert.Equal(1, exit);
        LogEntry entry = Assert.Single(capture.Entries, e => e.Action == "SetDns");
        Assert.Equal(LogResult.Failed, entry.Outcome);
        Assert.Equal(LogLevel.Warn, entry.Level);
    }

    [Fact]
    public async Task SetStaticIp_InvalidInput_LogsRejected_AndThrows()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);
        var svc = new NetConfigService(new FakeRunner(), new BusLogger("net"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.SetStaticIpAsync("以太网", "999.1.1.1", "255.255.255.0", null, _ => { }));

        LogEntry entry = Assert.Single(capture.Entries, e => e.Action == "SetStaticIp");
        Assert.Equal(LogResult.Rejected, entry.Outcome);
    }

    [Fact]
    public async Task RepairStep_FlushDnsSuccess_LogsWithStepId()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);
        var svc = new NetRepairService(new FakeRunner { ExitCode = 0 }, null!, new BusLogger("net"));

        int exit = await svc.ExecuteAsync("flushdns", _ => { });

        Assert.Equal(0, exit);
        LogEntry entry = Assert.Single(capture.Entries, e => e.Action == "RepairStep");
        Assert.Equal(LogResult.Success, entry.Outcome);
        Assert.Contains("flushdns", entry.Message);
    }

    [Fact]
    public async Task RepairStep_UnknownId_LogsRejected_AndThrows()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);
        var svc = new NetRepairService(new FakeRunner(), null!, new BusLogger("net"));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.ExecuteAsync("nope", _ => { }));

        LogEntry entry = Assert.Single(capture.Entries, e => e.Action == "RepairStep");
        Assert.Equal(LogResult.Rejected, entry.Outcome);
    }

    [Fact]
    public async Task TcpTuningRestore_NoSnapshot_LogsRejected_AndThrows()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);
        string missing = Path.Combine(Path.GetTempPath(), $"stk_log3_{Guid.NewGuid():N}.json");
        var svc = new TcpTuningService(new FakeRunner(), snapshotPath: missing, logger: new BusLogger("net"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(_ => { }));

        LogEntry entry = Assert.Single(capture.Entries, e => e.Action == "TcpTuningRestore");
        Assert.Equal(LogResult.Rejected, entry.Outcome);
    }

    /* ───────────────────── VSS：helper 缺失 → Failed 回退 ───────────────────── */

    [Fact]
    public async Task VssCreate_HelperMissing_LogsFailed_ReturnsNull()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);
        string temp = Path.Combine(Path.GetTempPath(), "stk_log3_vss_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var client = new ElevatedVssClient(
                helperPath: Path.Combine(temp, "definitely-missing.exe"),
                tempDirectoryProvider: () => temp,
                logger: new BusLogger("filebackup"));

            VssLease? lease = await client.CreateAsync("C:\\");

            Assert.Null(lease);
            LogEntry entry = Assert.Single(capture.Entries, e => e.Action == "VssCreateSnapshot");
            Assert.Equal(LogResult.Failed, entry.Outcome);
            Assert.Contains("回退普通复制", entry.Message);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /* ───────────────────── 互传：真实回环终态单咽喉 ───────────────────── */

    [Fact]
    public async Task Transfer_EndToEnd_BothSides_LogTaskLevelFields()
    {
        BusCapture capture = new();
        AppLog.AddSink(capture);

        var recvListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        recvListener.Start();
        int recvPort = ((System.Net.IPEndPoint)recvListener.LocalEndpoint).Port;
        recvListener.Stop();
        var sendListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        sendListener.Start();
        int sendPort = ((System.Net.IPEndPoint)sendListener.LocalEndpoint).Port;
        sendListener.Stop();

        string dir = Path.Combine(Path.GetTempPath(), "stk_log3_ft", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string sourcePath = Path.Combine(dir, "field.bin");
            await File.WriteAllBytesAsync(sourcePath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(64 * 1024));

            await using var receiver = new FileTransferService(logger: new BusLogger("filetransfer"));
            await using var sender = new FileTransferService(logger: new BusLogger("filetransfer"));
            await receiver.StartAsync(new TransferSettings
            {
                TransferPort = recvPort,
                ChunkSize = 64 * 1024,
                ReceiveDirectory = Path.Combine(dir, "recv"),
                RequirePairing = false,
            });
            await sender.StartAsync(new TransferSettings
            {
                TransferPort = sendPort,
                ChunkSize = 64 * 1024,
                ReceiveDirectory = Path.Combine(dir, "unused"),
                RequirePairing = false,
            });

            var recvDone = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendDone = new TaskCompletionSource<TransferTask>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TaskCompleted += (_, t) => recvDone.TrySetResult(t);
            sender.TaskCompleted += (_, t) => sendDone.TrySetResult(t);
            receiver.TransferRequested += (_, e) => _ = receiver.RespondTransferAsync(e.TaskId, accept: true);

            await sender.SendFileAsync(sourcePath, "127.0.0.1", recvPort);
            Assert.Equal(TransferStatus.Completed, (await sendDone.Task.WaitAsync(TimeSpan.FromSeconds(30))).Status);
            Assert.Equal(TransferStatus.Completed, (await recvDone.Task.WaitAsync(TimeSpan.FromSeconds(30))).Status);

            LogEntry send = Assert.Single(capture.Entries, e => e.Action == "SendFile");
            LogEntry recv = Assert.Single(capture.Entries, e => e.Action == "ReceiveFile");
            Assert.Equal(LogResult.Success, send.Outcome);
            Assert.Equal(LogResult.Success, recv.Outcome);
            // Duration 语义=真实传输耗时（StartedAt→FinishedAt），必须非空
            Assert.NotNull(send.DurationMs);
            Assert.NotNull(recv.DurationMs);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                /* 临时目录尽力清理 */
            }
        }
    }
}
