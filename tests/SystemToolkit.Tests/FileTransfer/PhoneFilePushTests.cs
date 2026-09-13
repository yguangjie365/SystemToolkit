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

    /// <summary>造一个已注入 Web 通道与"文件选择器"的桌面 VM（选择器返回给定路径）。</summary>
    private static (FileTransferDesktopViewModel Vm, ConcurrentQueue<string> Logs, string HistDir)
        NewVm(FileWebServer web, string shareDir, params string[] pickedFiles)
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
            ReceiveDirectory = shareDir,
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
                NewVm(server, shareDir, source);
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
                NewVm(server, shareDir, existing);
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
                NewVm(server, shareDir, existing);
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
                NewVm(server, shareDir, source);
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
                NewVm(server, shareDir, source);
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
                NewVm(server, shareDir, source);
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
                NewVm(server, shareDir); // 不传任何文件
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
}
