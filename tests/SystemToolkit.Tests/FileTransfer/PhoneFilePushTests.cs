using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Infrastructure.FileTransfer;
using SystemToolkit.Modules.FileTransfer;

namespace SystemToolkit.Tests;

/// <summary>
/// W3c：桌面端「发文件到手机」。
/// <para>
/// 要害是主人 2026-09-14 定的那条取舍：**用户选的文件不在共享目录时自动复制进去**
/// （贴合"发文件给手机"的直觉，代价是电脑上多一份副本）。
/// 所以用例围绕"复制的三条路径"展开：已在目录内不复制、不在则复制、同名按策略处置。
/// </para>
/// </summary>
public class PhoneFilePushTests
{
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string NewTempDir(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "stkft-push-" + tag, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static async Task<System.Net.WebSockets.ClientWebSocket> ConnectWsAsync(int port, string token)
    {
        var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?t={token}"), CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            await ReceiveTextAsync(ws);
        }

        return ws;
    }

    private static async Task<string> ReceiveTextAsync(System.Net.WebSockets.WebSocket ws)
    {
        byte[] buffer = new byte[16 * 1024];
        System.Net.WebSockets.WebSocketReceiveResult result =
            await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    /// <summary>
    /// 造一个已注入 Web 通道与"文件选择器"的桌面 VM（选择器返回给定路径）。
    /// <para>
    /// 🔴 审查 v8-🟠-1：<paramref name="receiveDirectory"/> 是**故意**独立于共享目录的形参。
    /// 原先把它与 <c>web.StartAsync(..., shareDir)</c> 设成同值，正好把
    /// 「共享目录取错配置源」这个缺陷**结构性掩盖**了（用例永远绿）。现在各用例显式传
    /// <see cref="string.Empty"/> 或一个**不同的**目录，钉住"推送只认服务端 SharedRoot"。
    /// </para>
    /// </summary>
    private static (FileTransferDesktopViewModel Vm, ConcurrentQueue<string> Logs, string HistDir)
        NewVm(FileWebServer web, string receiveDirectory, params string[] pickedFiles)
    {
        string hist = NewTempDir("hist");
        var logs = new ConcurrentQueue<string>();
        var vm = new FileTransferDesktopViewModel(
            new DeviceDiscoveryService(),
            new FileTransferService(),
            new TransferHistoryService(hist),
            logs.Enqueue,
            NullLogger.Instance,
            dispatcher: null,
            web: web)
        {
            ReceiveDirectory = receiveDirectory,
            PickFiles = () => pickedFiles,
        };
        return (vm, logs, hist);
    }

    /// <summary>
    /// 文件**不在**共享目录 → 复制进去再推送；手机端收到的 path 是共享目录内的相对路径。
    /// <para>反向验证：去掉 <c>EnsureInSharedDirectoryAsync</c> 里的复制分支 → 本用例变红
    /// （推送会因"共享目录里找不到该文件"直接抛）。</para>
    /// </summary>
    [Fact]
    public async Task FileOutsideShareDir_IsCopiedIn_ThenOffered()
    {
        string shareDir = NewTempDir("share");
        string otherDir = NewTempDir("other");
        int port = FreeTcpPort();
        try
        {
            string source = Path.Combine(otherDir, "报表.txt");
            await File.WriteAllTextAsync(source, "内容-A");

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, source);
            try
            {
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                // ① 文件确实被复制进了共享目录
                string copied = Path.Combine(shareDir, "报表.txt");
                Assert.True(File.Exists(copied), "不在共享目录的文件应被自动复制进去");
                Assert.Equal("内容-A", await File.ReadAllTextAsync(copied));

                // ② 推送的 path 是**相对路径**（手机端要拿它去下载）
                string frame = await ReceiveTextAsync(ws);
                Assert.Contains("\"type\":\"fileOffered\"", frame);
                using (var doc = JsonDocument.Parse(frame))
                {
                    JsonElement payload = doc.RootElement.GetProperty("payload");
                    Assert.Equal("报表.txt", payload.GetProperty("path").GetString());
                    Assert.Equal("报表.txt", payload.GetProperty("name").GetString());
                    Assert.Equal(Encoding.UTF8.GetByteCount("内容-A"), payload.GetProperty("size").GetInt64());
                }

                Assert.Contains(logs, m => m.Contains("正在复制到共享目录"));
                Assert.Contains(logs, m => m.Contains("已向 1 个手机浏览器推送 1 个文件"));
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
            DeleteTempDir(otherDir);
        }
    }

    /// <summary>
    /// 文件**已在**共享目录 → 不复制（不留副本、不改名），直接推送。
    /// <para>反向验证：把 <c>IsInsideDirectory</c> 改成恒 false → 本用例变红（会多出一份 "a (2).txt"）。</para>
    /// </summary>
    [Fact]
    public async Task FileAlreadyInShareDir_IsNotCopied()
    {
        string shareDir = NewTempDir("share");
        int port = FreeTcpPort();
        try
        {
            string existing = Path.Combine(shareDir, "a.txt");
            await File.WriteAllTextAsync(existing, "already-here");

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, existing);
            try
            {
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);
                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                string[] inShare = Directory.GetFiles(shareDir);
                Assert.Single(inShare);
                Assert.Equal(existing, inShare[0]);
                Assert.DoesNotContain(logs, m => m.Contains("正在复制到共享目录"));

                string frame = await ReceiveTextAsync(ws);
                Assert.Contains("\"type\":\"fileOffered\"", frame);
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
        }
    }

    /// <summary>共享目录内**子目录**里的文件 → 推送相对路径保留子目录层级。</summary>
    [Fact]
    public async Task FileInSubDirectory_KeepsRelativePath()
    {
        string shareDir = NewTempDir("share");
        int port = FreeTcpPort();
        try
        {
            string sub = Path.Combine(shareDir, "docs");
            Directory.CreateDirectory(sub);
            string existing = Path.Combine(sub, "c.txt");
            await File.WriteAllTextAsync(existing, "sub-file");

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, existing);
            try
            {
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);
                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                // GetFiles 不递归：根目录本来就该是空的 —— 它在子目录里，不该被搬到根目录
                Assert.Empty(Directory.GetFiles(shareDir));
                Assert.Single(Directory.GetFiles(sub));

                string frame = await ReceiveTextAsync(ws);
                using var doc = JsonDocument.Parse(frame);
                Assert.Equal(
                    Path.Combine("docs", "c.txt"),
                    doc.RootElement.GetProperty("payload").GetProperty("path").GetString());
                _ = logs;
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
        }
    }

    /// <summary>同名 + 「自动改名」策略 → 复制成 <c>a (2).txt</c>，且两个文件都在。</summary>
    [Fact]
    public async Task SameName_RenamePolicy_MakesUniqueCopy()
    {
        string shareDir = NewTempDir("share");
        string otherDir = NewTempDir("other");
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(shareDir, "a.txt"), "old");
            string source = Path.Combine(otherDir, "a.txt");
            await File.WriteAllTextAsync(source, "new");

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, source);
            try
            {
                vm.ConflictPolicy = TransferConflictPolicy.Rename;
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(shareDir, "a.txt")));
                string renamed = Path.Combine(shareDir, "a (2).txt");
                Assert.True(File.Exists(renamed), "同名时应自动改名");
                Assert.Equal("new", await File.ReadAllTextAsync(renamed));

                string frame = await ReceiveTextAsync(ws);
                using var doc = JsonDocument.Parse(frame);
                Assert.Equal("a (2).txt", doc.RootElement.GetProperty("payload").GetProperty("path").GetString());
                Assert.Contains(logs, m => m.Contains("自动改名为"));
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
            DeleteTempDir(otherDir);
        }
    }

    /// <summary>
    /// 同名 + 「跳过不接收」策略 → **不复制、不推送**，日志如实说明。
    /// 这条钉的是"策略必须真的被遵守"：把策略当摆设，用户会以为选了跳过却还是被塞了副本。
    /// </summary>
    [Fact]
    public async Task SameName_SkipPolicy_DoesNotCopyNorPush()
    {
        string shareDir = NewTempDir("share");
        string otherDir = NewTempDir("other");
        int port = FreeTcpPort();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(shareDir, "a.txt"), "old");
            string source = Path.Combine(otherDir, "a.txt");
            await File.WriteAllTextAsync(source, "new");

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, source);
            try
            {
                vm.ConflictPolicy = TransferConflictPolicy.Skip;
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);

                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                Assert.Single(Directory.GetFiles(shareDir)); // 没有新增副本
                Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(shareDir, "a.txt")));
                Assert.Contains(logs, m => m.Contains("跳过"));

                // 不该有任何 fileOffered 帧：紧跟一次 ping，下一帧应是 pong（次序断言，不靠等待超时）
                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                Assert.Contains("\"type\":\"pong\"", await ReceiveTextAsync(ws));
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
            DeleteTempDir(otherDir);
        }
    }

    /// <summary>Web 服务未启动 → 明确指出"去哪开"，且不去复制文件（不该有副作用）。</summary>
    [Fact]
    public async Task WebNotRunning_LogsGuidance_AndDoesNotCopy()
    {
        string shareDir = NewTempDir("share");
        string otherDir = NewTempDir("other");
        try
        {
            string source = Path.Combine(otherDir, "b.txt");
            await File.WriteAllTextAsync(source, "x");

            using var server = new FileWebServer(); // 只构造不启动
            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, source);
            try
            {
                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                Assert.Contains(logs, m => m.Contains("Web 服务未启动") && m.Contains("手机访问"));
                Assert.Empty(Directory.GetFiles(shareDir)); // 没推送就不该留下副本
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
            DeleteTempDir(otherDir);
        }
    }

    /// <summary>用户取消选择（选择器返回 null / 空）→ 什么都不做，也不留副本。</summary>
    [Fact]
    public async Task NoFilePicked_DoesNothing()
    {
        string shareDir = NewTempDir("share");
        int port = FreeTcpPort();
        try
        {
            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty); // 不传任何文件
            try
            {
                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                Assert.Empty(Directory.GetFiles(shareDir));
                Assert.DoesNotContain(logs, m => m.Contains("已向"));
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
        }
    }

    /// <summary>
    /// 🟠 审查 v8-🟠-1（失效形态 ①）：**只配了共享目录**（启动 Web 服务的必要条件）、
    /// 「接收目录」为空时，「发文件到手机」必须照常工作。
    /// <para>
    /// 旧实现取 <c>ReceiveDirectory</c> 当共享目录，此处会直接报"接收目录未设置"而功能失效。
    /// </para>
    /// <para>反向验证：把 <c>SendFileToPhoneCoreAsync</c> 里的 <c>_web.SharedRoot</c> 换回
    /// <c>ReceiveDirectory</c> → 本用例变红（文件不会被复制，日志出现"共享目录不可用"）。</para>
    /// </summary>
    [Fact]
    public async Task ReceiveDirectoryEmpty_ButShareRootSet_StillPushes()
    {
        string shareDir = NewTempDir("share");
        string otherDir = NewTempDir("other");
        int port = FreeTcpPort();
        try
        {
            string source = Path.Combine(otherDir, "only-share.txt");
            await File.WriteAllTextAsync(source, "payload");

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, source);
            try
            {
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);
                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                Assert.True(
                    File.Exists(Path.Combine(shareDir, "only-share.txt")),
                    "共享目录已配置时，接收目录为空也必须能推送");
                string frame = await ReceiveTextAsync(ws);
                Assert.Contains("\"type\":\"fileOffered\"", frame);
                Assert.Contains(logs, m => m.Contains("已向 1 个手机浏览器推送 1 个文件"));
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
            DeleteTempDir(otherDir);
        }
    }

    /// <summary>
    /// 🟠 审查 v8-🟠-1（失效形态 ②）：「接收目录」与共享目录**指向不同目录**时，
    /// 文件必须复制进**共享目录**（手机唯一可见的目录），而不是接收目录。
    /// <para>
    /// 旧实现按接收目录复制 → 随后 <c>PublishFileOfferAsync</c> 去共享目录里找
    /// → 抛"共享目录里找不到该文件" → **推送全败**（且副本还留在用户没预期的地方）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReceiveDirectoryDiffers_FromShareRoot_CopiesIntoShareRoot()
    {
        string shareDir = NewTempDir("share");
        string receiveDir = NewTempDir("receive");
        string otherDir = NewTempDir("other");
        int port = FreeTcpPort();
        try
        {
            string source = Path.Combine(otherDir, "mixed.txt");
            await File.WriteAllTextAsync(source, "payload");

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDir, source);
            try
            {
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);
                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                // 复制目标是**共享目录**（手机看得到的那个），不是接收目录
                Assert.True(File.Exists(Path.Combine(shareDir, "mixed.txt")));
                Assert.False(
                    File.Exists(Path.Combine(receiveDir, "mixed.txt")),
                    "文件不该落到手机看不到的「接收目录」里");

                string frame = await ReceiveTextAsync(ws);
                Assert.Contains("\"type\":\"fileOffered\"", frame);
                // 不一致时给出可操作提示（说清"手机只能看到共享目录里的文件"）
                Assert.Contains(logs, m => m.Contains("与「接收目录」") && m.Contains("共享目录"));
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
            DeleteTempDir(receiveDir);
            DeleteTempDir(otherDir);
        }
    }

    /// <summary>
    /// 🟠 审查 v8-🟠-6：复制到共享目录必须**原子**——失败时不能在共享目录留下半截文件、
    /// 也不能留下 <c>.part</c> 残留（否则手机 <c>/api/files</c> 能浏览到半成品，用户还会再推一次）。
    /// <para>
    /// <b>怎么造出"复制之后、落定之前"的失败</b>：在共享目录里预先建一个**同名目录**
    /// （<c>shareDir/boom.txt/</c>）。此时 <c>File.Exists(target)</c> 为假（它是目录），
    /// 复制照常写进 <c>boom.txt.part</c>，但收尾的 <c>File.Move(tmp, target, overwrite)</c> 必失败
    /// ——正是新代码里那个 <c>catch</c> 分支。
    /// </para>
    /// <para>反向验证：删掉 <c>catch</c> 里的 <c>File.Delete(tmpTarget)</c> → 本用例变红
    /// （共享目录里会留下 <c>boom.txt.part</c>）。</para>
    /// </summary>
    [Fact]
    public async Task CopyFailure_LeavesNoHalfFileNorPartResidue()
    {
        string shareDir = NewTempDir("share");
        string otherDir = NewTempDir("other");
        int port = FreeTcpPort();
        try
        {
            string source = Path.Combine(otherDir, "boom.txt");
            await File.WriteAllTextAsync(source, "payload");
            Directory.CreateDirectory(Path.Combine(shareDir, "boom.txt")); // 占住最终路径

            await using var server = new FileWebServer();
            await server.StartAsync(new TransferSettings { WebPort = port }, shareDir);

            (FileTransferDesktopViewModel vm, ConcurrentQueue<string> logs, string hist) =
                NewVm(server, receiveDirectory: string.Empty, source);
            try
            {
                using System.Net.WebSockets.ClientWebSocket ws = await ConnectWsAsync(port, server.Token);
                await vm.SendFileToPhoneCommand.ExecuteAsync(null);

                Assert.Contains(logs, m => m.Contains("复制到共享目录失败"));
                // 不留半截：既没有 .part 残留，也没有被误当成成品的文件
                Assert.False(
                    File.Exists(Path.Combine(shareDir, "boom.txt.part")),
                    "失败后必须清掉 .part 临时文件");
                Assert.Empty(Directory.GetFiles(shareDir));

                // 失败就不该推送：紧跟一次 ping，下一帧应是 pong（次序断言，不靠等待超时）
                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                Assert.Contains("\"type\":\"pong\"", await ReceiveTextAsync(ws));
            }
            finally
            {
                DeleteTempDir(hist);
            }
        }
        finally
        {
            DeleteTempDir(shareDir);
            DeleteTempDir(otherDir);
        }
    }
}
