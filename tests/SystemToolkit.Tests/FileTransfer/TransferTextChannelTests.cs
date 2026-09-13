using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.FileTransfer.Services.Protocol;
using WatsonTcp;

namespace SystemToolkit.Tests;

/// <summary>
/// FT-3 文本通道的**服务层收发**用例（B8a-2）：真实 TCP 回环的双实例端到端。
/// <para>
/// 契约与判据（长度口径、预览截断、序列化兼容）在 <c>TransferTextTests</c>（B8a-1）已钉死；
/// 本类只测"跑起来之后会怎样"：接受 / 拒绝 / 剪贴板写失败 / 超时 / 对端未校验的超限文本 /
/// 不认识的消息类型。
/// </para>
/// </summary>
public class TransferTextChannelTests
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>取一个空闲 TCP 端口。</summary>
    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static TransferSettings MakeSettings(int transferPort, string receiveDir) => new()
    {
        TransferPort = transferPort,
        ChunkSize = ChunkSize,
        ReceiveDirectory = receiveDir,
        MaxConcurrentTransfers = 2,
        // 与既有用例同款：本类专测文本通道，显式关闭配对门保持意图（配对门另有专测）
        RequirePairing = false,
    };

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

    /// <summary>等待双方任务终态。</summary>
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

    // ===================== 本地校验：不发起任何连接，也不留任务 =====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task SendTextAsync_BlankText_ThrowsAndCreatesNoTask(string text)
    {
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var sender = new FileTransferService();
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            await Assert.ThrowsAsync<ArgumentException>(
                () => sender.SendTextAsync(text, "127.0.0.1", FreeTcpPort()));

            // 🔴 关键：不合规的文本不是一次传输尝试 —— 不得留下"传过但失败"的假任务
            Assert.Empty(sender.ActiveTasks);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task SendTextAsync_OverlongText_ThrowsAndCreatesNoTask()
    {
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var sender = new FileTransferService();
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));

            string tooLong = new string('a', TransferText.MaxBytes + 1);

            await Assert.ThrowsAsync<ArgumentException>(
                () => sender.SendTextAsync(tooLong, "127.0.0.1", FreeTcpPort()));

            Assert.Empty(sender.ActiveTasks);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ===================== 端到端：接受 =====================

    [Fact]
    public async Task Text_Accepted_DeliversFullTextAndCompletes()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 20;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            const string payload = "https://example.com/分享?token=abc123 😀 第二行";
            var requested = new TaskCompletionSource<TransferRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.TransferRequested += (_, e) =>
            {
                requested.TrySetResult(e);
                // 模拟 UI：写剪贴板成功 → 才回 Accept（回执与实际交付是同一个事实）
                _ = receiver.RespondTransferAsync(e.TaskId, TransferDecision.AcceptWith(TransferConflictPolicy.Rename));
            };

            await sender.SendTextAsync(payload, "127.0.0.1", recvPort);

            TransferRequestEventArgs args = await requested.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(TransferKind.Text, args.Kind);
            Assert.Equal(payload, args.Text); // 全文，不是 120 字预览
            Assert.Equal(payload.Length, args.TextLength);
            Assert.Equal(TransferText.GetByteCount(payload), args.FileSize);
            // 文本没有这些文件语义 —— 必须是默认值，弹窗据此整块隐藏
            Assert.Equal(string.Empty, args.ReceiveDirectory);
            Assert.False(args.TargetExists);

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            Assert.Equal(TransferKind.Text, recvFinal.Kind);
            Assert.Equal(TransferStatus.Completed, sendFinal.Status);
            Assert.Equal(TransferKind.Text, sendFinal.Kind);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 🔴 本批最重要的**边界实测**：单条文本的**最坏形态**必须真的能到达对端。
    /// 形态取 emoji（4 字节 UTF-8 / 字符，且序列化进 JSON 时每个码元转义成 6 字节 = 12 字节/字符），
    /// 并按 <see cref="TransferText.MaxBytes"/> **正好写满**。
    /// <para>
    /// 背景：文本走 metadata（报文头）通道，而 WatsonTcp 的 <c>MaxHeaderSize</c> 默认 262144 字节
    /// —— 与本上限**同值**，等于"正好写满就发不出去"（本用例在修复前 15 s 内收不到任何回执）。
    /// 修复 = <see cref="FileTransferService.MaxHeaderBytes"/> 显式抬高报文头预算且收发两侧都设。
    /// 谁把它改回默认值，本用例立即变红。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Text_AtExactByteLimit_WorstCaseEscaping_StillReachesReceiver()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 30;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            var builder = new StringBuilder();
            for (int i = 0; i < TransferText.MaxBytes / 4; i++)
            {
                builder.Append("\U0001F600");
            }
            string payload = builder.ToString();

            // 自证前提：这确实是"正好写满"的上限文本（而不是我算错了长度）
            Assert.Equal(TransferText.MaxBytes, TransferText.GetByteCount(payload));
            TextValidation validation = TransferText.Validate(payload);
            Assert.True(validation.IsValid, "上限文本必须判为合法，否则测的是别的分支");

            // 报文头预算必须装得下"序列化 + JSON 转义膨胀"后的体积
            string header = JsonSerializer.Serialize(new TransferMessage
            {
                Type = TransferMessageType.Text,
                TaskId = "limit",
                Text = payload,
            });
            Assert.True(
                header.Length <= FileTransferService.MaxHeaderBytes,
                $"报文头 {header.Length} 字节超出预算 {FileTransferService.MaxHeaderBytes}（转义膨胀 {header.Length / (double)TransferText.MaxBytes:F2}×）");

            receiver.TransferRequested += (_, e) =>
                _ = receiver.RespondTransferAsync(e.TaskId, TransferDecision.AcceptWith(TransferConflictPolicy.Rename));

            await sender.SendTextAsync(payload, "127.0.0.1", recvPort);

            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(60));
            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferStatus.Completed, sendFinal.Status);
            Assert.Equal(TransferStatus.Completed, recvFinal.Status);
            Assert.Equal(TransferText.MaxBytes, recvFinal.FileSize); // 对端收到的就是写满的那一份
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ===================== 端到端：拒绝（两种成因必须区分） =====================
    [Fact]
    public async Task Text_ClipboardWriteFailed_CarriesThatReasonCodeToSender()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 20;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            receiver.TransferRequested += (_, e) =>
                _ = receiver.RespondTransferAsync(
                    e.TaskId, TransferDecision.RejectWith(TransferReasonCodes.ClipboardWriteFailed));

            await sender.SendTextAsync("剪贴板被占了", "127.0.0.1", recvPort);

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, recvFinal.Status);
            Assert.Equal(TransferReasonCodes.ClipboardWriteFailed, recvFinal.ReasonCode);

            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, sendFinal.Status);
            // 🔴 发送方拿到的必须是"对端要了但没接住"，不能退化成笼统的"被拒绝"
            Assert.Equal(TransferReasonCodes.ClipboardWriteFailed, sendFinal.ReasonCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Text_UserRejectWithoutCode_FallsBackToUserReject()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 20;
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            receiver.TransferRequested += (_, e) => _ = receiver.RespondTransferAsync(e.TaskId, TransferDecision.Reject);

            await sender.SendTextAsync("用户点拒绝", "127.0.0.1", recvPort);

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, recvFinal.Status);
            Assert.Equal(TransferReasonCodes.UserReject, recvFinal.ReasonCode);

            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferReasonCodes.UserReject, sendFinal.ReasonCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Text_ConfirmTimeout_FailsWithConfirmTimeout()
    {
        int recvPort = FreeTcpPort();
        int sendPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await using var sender = new FileTransferService();
            TransferSettings recvSettings = MakeSettings(recvPort, Path.Combine(dir, "recv"));
            recvSettings.RequireReceiveConfirmation = true;
            recvSettings.ReceiveConfirmTimeoutSeconds = 2; // 缩短等待，用例不该等满 30 秒
            await receiver.StartAsync(recvSettings);
            await sender.StartAsync(MakeSettings(sendPort, Path.Combine(dir, "unused")));
            HookBothDone(receiver, sender, out Task<TransferTask> recvDone, out Task<TransferTask> sendDone);

            // 刻意不订阅 TransferRequested（模拟"没人看这个弹窗"）
            await sender.SendTextAsync("没人应答", "127.0.0.1", recvPort);

            TransferTask recvFinal = await recvDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferStatus.Failed, recvFinal.Status);
            Assert.Equal(TransferReasonCodes.ConfirmTimeout, recvFinal.ReasonCode);

            TransferTask sendFinal = await sendDone.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(TransferReasonCodes.ConfirmTimeout, sendFinal.ReasonCode);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ===================== 接收端自保：对端未校验 / 协议不认识 =====================

    /// <summary>对端（旧版本或非本工具实现）发来超限文本：本端必须拦下并回原因码，**不静默截断**。</summary>
    [Fact]
    public async Task Text_OverlongFromRawPeer_RejectedWithTextTooLong()
    {
        int recvPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));

            using var client = new WatsonTcpClient("127.0.0.1", recvPort);
            var errTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Events.MessageReceived += (_, e) =>
            {
                TransferMessage? tm = ParseTestMessage(e.Metadata);
                if (tm?.Type == TransferMessageType.Error)
                {
                    errTcs.TrySetResult(tm);
                }
            };
            client.Connect();

            await client.SendAsync(string.Empty, BuildTestMetadata(new TransferMessage
            {
                Type = TransferMessageType.Text,
                TaskId = "evil-text",
                Text = new string('a', TransferText.MaxBytes + 1),
            }), CancellationToken.None);

            TransferMessage error = await errTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(TransferReasonCodes.TextTooLong, error.ReasonCode);
            Assert.Equal("evil-text", error.TaskId);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// 本端不认识的消息类型必须收到明确的 Error（方案 §3.2）。
    /// 修复前 <c>ParseMessage</c> 把 <c>JsonException</c> 咽掉、<c>switch</c> 又没有 default
    /// → 对端傻等到超时、两端都拿不到原因（本用例会因等不到 Error 而超时变红）。
    /// </summary>
    [Fact]
    public async Task UnknownMessageType_RepliesErrorInsteadOfSilentDrop()
    {
        int recvPort = FreeTcpPort();
        string dir = NewTempDir();
        try
        {
            await using var receiver = new FileTransferService();
            await receiver.StartAsync(MakeSettings(recvPort, Path.Combine(dir, "recv")));

            using var client = new WatsonTcpClient("127.0.0.1", recvPort);
            var errTcs = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Events.MessageReceived += (_, e) =>
            {
                TransferMessage? tm = ParseTestMessage(e.Metadata);
                if (tm?.Type == TransferMessageType.Error)
                {
                    errTcs.TrySetResult(tm);
                }
            };
            client.Connect();

            // 直接用裸 JSON：模拟"更新版本的对端"发来本端尚未实现的类型
            Dictionary<string, object> meta = new()
            {
                ["m"] = """{"Type":"SomethingFromTheFuture","TaskId":"future-1"}""",
            };
            await client.SendAsync(string.Empty, meta, CancellationToken.None);

            TransferMessage error = await errTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("SomethingFromTheFuture", error.Error);
            Assert.Equal("future-1", error.TaskId);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
