using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Infrastructure.FileTransfer;

/// <summary>
/// Web 文件服务（Kestrel 实现）：通过 HTTP 暴露共享目录，供手机浏览器访问。
/// <para>
/// 提供：静态前端资源（嵌入资源 index.html / app.js / style.css）、
/// RESTful API（文件列表 / 下载 / 打包下载 / 分块上传 / 设备列表 / 配对）。
/// </para>
/// <para>安全边界：除配对端点与静态外壳资源外，所有请求必须携带访问令牌（<c>?t=</c>，
/// 每次启动随机生成，随 <see cref="Url"/> 展示给用户）；文件路径一律先做穿越校验，
/// 目标必须落在共享根目录之内；上传写入只取安全相对路径且同名不覆盖（自动追加序号）；
/// 上传走<b>原始 body 流式直写</b>，单文件上限 10GB。</para>
/// <para>配对模型：二维码只携带 6 位短期配对码（<see cref="LanUrl"/> 的 <c>?c=</c>），
/// 手机提交配对码换长期令牌（<see cref="PairingService"/>，一次性消费、10 分钟轮换），
/// 令牌不再出现在 URL 与浏览器历史里。旧工程内联的配对码实现已抽取为
/// <see cref="PairingService"/>（桌面 TCP 通道共用），本类只注入使用，不再自持配对码状态。</para>
/// </summary>
public sealed partial class FileWebServer : IFileWebServer, IDisposable
{
    /// <summary>分块上传的默认块大小：8MB（手机端切片与服务端校验长度用）。</summary>
    private const long ChunkSizeBytes = 8L * 1024 * 1024;

    /// <summary>打包下载的文件数上限：防止一次圈选整个大目录把服务器内存与带宽吃干。</summary>
    private const int MaxZipEntries = 1000;

    /// <summary>
    /// 单个上传文件的大小上限：10GB。
    /// <para>
    /// 之所以敢放开到 10GB：上传是流式直写，全程只有 80KB 缓冲，内存不随文件大小增长；
    /// multipart 表单会把请求缓冲成同体积临时文件（C 盘 temp 先爆），故不提供该形态端点。
    /// </para>
    /// </summary>
    private const long UploadLimitBytes = 10L * 1024 * 1024 * 1024;

    private readonly IDeviceDiscoveryService? _discovery;
    private readonly SystemToolkit.Core.Contracts.ILogger _logger;
    private readonly PairingService _pairing;

    /// <summary>
    /// 分块上传串行化闸门（按 uploadId）。前端本就逐块顺序传，这里防的是失败重试与多端同传同一文件。
    /// <para>定稿后主动移除；意外中断残留的只是一个 SemaphoreSlim 小对象，不构成泄漏。</para>
    /// </summary>
    /// <summary>
    /// 同一 uploadId 的写入串行闸。**带引用计数**（🟠 审查 2026-09-11，🟠-1）。
    /// <para>
    /// 为什么不能"用完直接 TryRemove"：A 持闸执行、B 在旧闸上排队；A 失败时把它从字典摘掉，
    /// B 仍在等**同一个旧对象**；C 随后 GetOrAdd 拿到**新闸**并立即进入 —— C 与 B 并发写同一个
    /// <c>.part</c>，恰好绕过闸要防的写入交错（:409 注释所述）。故摘除必须满足两个条件：
    /// ① 已无人持有/等待（<see cref="RefCount"/> 归零）；② 字典里存的**仍是本对象**。
    /// </para>
    /// </summary>
    private sealed class UploadGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>持有者 + 等待者计数（由 <c>_uploadGateSync</c> 锁维护）。归零才允许摘除。</summary>
        public int RefCount;
    }

    /// <summary>上传闸字典（键 = uploadId）；条目生命周期由 <see cref="UploadGate.RefCount"/> 决定。</summary>
    private readonly ConcurrentDictionary<string, UploadGate> _uploadGates = new();

    /// <summary>
    /// 上传闸的取用/归还互斥（🟠 审查 2026-09-11，F-3）：把「取闸 + 加计数」与「减计数 + 摘除」
    /// 各自括成原子段。
    /// <para>
    /// 原实现是 <c>AddOrUpdate</c> + <c>Interlocked.Increment</c> **两步**，其间存在窗口：
    /// 在飞的持有者 A 可在此刻把计数减到 0 并摘除该闸，于是后者的 Increment 落在**孤儿对象**上，
    /// 而下一个到达者会为同一 uploadId **新建一把闸** → 两人各持不同信号量、**并发写同一 .part**。
    /// 临界区**不含 await**（纯内存操作），故普通 lock 足够。
    /// </para>
    /// </summary>
    private readonly object _uploadGateSync = new();

    private WebApplication? _app;
    private string _shareDirectory = string.Empty;
    private int _port;
    private bool _httpsEnabled;
    private int _httpsPort;
    /// <summary>启动时探测的局域网 IP（<see cref="LanUrl"/> 计算属性用）。</summary>
    private string _lanIp = string.Empty;
    /// <summary>访问令牌。volatile：StartAsync 与 RotateToken 在非请求线程写，鉴权中间件在请求线程读。</summary>
    private volatile string _token = string.Empty;

    /// <inheritdoc/>
    public bool IsRunning => _app is not null;

    /// <inheritdoc/>
    /// <remarks>委托给注入的 <see cref="PairingService"/>（惰性生成、到期自动轮换）；
    /// 未运行时返回空串。</remarks>
    public string PairCode => IsRunning ? _pairing.CurrentCode : string.Empty;

    /// <inheritdoc/>
    public int Port => _port;

    /// <inheritdoc/>
    public bool IsHttps => _httpsEnabled;

    /// <inheritdoc/>
    public string Url => IsRunning
        ? _httpsEnabled
            ? $"https://localhost:{_httpsPort}/?t={_token}"
            : $"http://localhost:{_port}/?t={_token}"
        : string.Empty;

    /// <inheritdoc/>
    /// <remarks>
    /// 必须是<b>计算属性</b>而非 StartAsync 一次赋值的缓存：
    /// 配对码过期会惰性轮换，缓存的 LanUrl 会让运行超期后展开的二维码携带已过期配对码
    /// （手机扫码必 401）。getter 实时取 <see cref="PairCode"/>，保证任何时刻取到的都是当前有效码。
    /// </remarks>
    public string LanUrl => IsRunning
        ? $"{(_httpsEnabled ? "https" : "http")}://{_lanIp}:{(_httpsEnabled ? _httpsPort : _port)}/?c={PairCode}"
        : string.Empty;

    /// <inheritdoc/>
    public string Token => _token;

    /// <summary>
    /// 构造 Web 文件服务。
    /// </summary>
    /// <param name="discovery">设备发现服务（可选；注入后 <c>/api/devices</c> 返回其在线快照，缺省返回空列表）。</param>
    /// <param name="pairing">配对码服务（可选，缺省自建一个；注入共享实例可让 Web 通道与桌面 TCP 通道使用同一枚配对码）。</param>
    /// <param name="logger">日志（可选，缺省静默——测试场景用）。</param>
    public FileWebServer(IDeviceDiscoveryService? discovery = null, PairingService? pairing = null,
        SystemToolkit.Core.Contracts.ILogger? logger = null)
    {
        _discovery = discovery;
        _pairing = pairing ?? new PairingService();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task StartAsync(TransferSettings settings, string shareDirectory, CancellationToken ct = default)
    {
        if (_app is not null)
        {
            throw new InvalidOperationException("Web 服务已在运行，请先调用 StopAsync。");
        }

        _shareDirectory = shareDirectory;
        _port = settings.WebPort;
        _httpsEnabled = settings.UseHttps;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string lanIp = DetectLanIp();
        _lanIp = lanIp;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        // 桥接框架日志 → Core ILogger：WPF 进程没有控制台，Kestrel 的日志（含启动失败真因）默认进黑洞
        builder.Logging.AddProvider(new ForwardingLoggerProvider(_logger));

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (_httpsEnabled)
            {
                // HTTPS 为主通道；同时保留 HTTP 端口做兼容与跳转，
                // 这样已配对过的旧手机（存的是 http 地址）打开时会收到 301 而不是连接失败。
                _httpsPort = settings.HttpsPort > 0 ? settings.HttpsPort : 18891;
                options.Listen(IPAddress.Any, _httpsPort,
                    listen => listen.UseHttps(SelfSignedCertificate.GetOrCreate(lanIp)));
                options.Listen(IPAddress.Any, _port);
            }
            else
            {
                options.Listen(IPAddress.Any, _port);
            }

            // 单请求体上限 10GB + 余量（请求头/分块边界）。流式直写不缓冲，内存占用恒定。
            options.Limits.MaxRequestBodySize = UploadLimitBytes + 2L * 1024 * 1024;

            // 大文件上传必配：默认 MinRequestBodyDataRate 为 240 字节/秒，
            // 手机 Wi-Fi 抖动若持续 5 秒低于该速率，Kestrel 会直接掐断连接
            // （表现为"传到一半失败"，且日志里看不出所以然）。置 null 即关闭该下限。
            options.Limits.MinRequestBodyDataRate = null;
        });

        WebApplication app = builder.Build();

        // ── WebSocket 升级支持（🟡 审查 2026-09-10：补上 /ws 实时推送端点）──
        // 升级请求同样要走下方令牌中间件（/ws 不在免令牌白名单内）
        app.UseWebSockets();

        // ── 安全响应头：全局注入 ──
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        // ── HTTP → HTTPS 跳转（仅在启用 HTTPS 时）──
        // 必须早于令牌中间件：否则明文请求会先被 401 拦下，重定向永远发不出去。
        //
        // 🔴 S-4（审查 2026-09-06）：明文 HTTP 是令牌的泄露面，两处都必须堵：
        //   ① 跳转 Location 剥离 ?t= —— 旧实现原样回显查询串，等于把 32-hex 长期令牌
        //      又写进一个**响应头**里；同 LAN 的 MiTM 抓一次 301 就能拿到全量读写权限。
        //      （代价：已配对手机里存的旧 http 链接跳转后需重新配对——安全优先于这个便利。）
        //   ② 请求本身携带 ?t= 说明令牌已明文上过线路，泄露既成事实，唯一止损手段是
        //      立即作废（轮换）并让客户端重新配对：配对码 6 位 / 60s，重配对代价可接受。
        if (_httpsEnabled)
        {
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Scheme == "http")
                {
                    if (ctx.Request.Query.ContainsKey("t"))
                    {
                        RotateToken("HTTP 明文请求携带访问令牌");
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await ctx.Response.WriteAsync(
                            "token leaked over plain HTTP: rotated, please pair again", ctx.RequestAborted);
                        return;
                    }

                    ctx.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                    ctx.Response.Headers.Location = BuildHttpsLocation(ctx);
                    return;
                }
                await next();
            });
        }

        // ── 令牌认证：除白名单外所有路径（静态 API）统一要求 ?t=<token> ──
        app.Use(async (ctx, next) =>
        {
            // 免令牌白名单（详见 IsPublicAsset 说明）
            if (IsPublicAsset(ctx.Request.Path))
            {
                await next();
                return;
            }

            // 时间常量比较，规避时序侧信道
            byte[] provided = Encoding.UTF8.GetBytes(ctx.Request.Query["t"].ToString());
            byte[] expected = Encoding.UTF8.GetBytes(_token);
            if (!CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    // 前端 fetch/XHR 要能解析出错误原因，给 HTML 页会让它只能报「上传失败」
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync("{\"error\":\"需要访问令牌，请重新扫码配对。\"}");
                }
                else
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync(UnauthorizedPageHtml);
                }
                return;
            }
            await next();
        });

        // ── 静态前端资源（从嵌入资源提供）──
        app.MapGet("/", () => ServeEmbedded("index.html", "text/html; charset=utf-8"));
        app.MapGet("/index.html", () => ServeEmbedded("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => ServeEmbedded("app.js", "application/javascript; charset=utf-8"));
        app.MapGet("/style.css", () => ServeEmbedded("style.css", "text/css; charset=utf-8"));

        // ── 实时推送（🟡-5）：设备上下线推给已连接浏览器（协议见 FileWebServer.WebSocket.cs）──
        app.MapGet("/ws", HandleWebSocketAsync);

        // ── RESTful API ──
        app.MapGet("/api/files", (string? path) =>
        {
            string root = ShareRoot;
            // 端点层独立检查：越界/不存在 → 404；空目录 → 200 + 空数组
            if (!IsSafeUnderRoot(root, path))
            {
                return Results.NotFound(new { error = "路径越界或不存在" });
            }
            string target = Path.GetFullPath(Path.Combine(root, path ?? string.Empty));
            if (!Directory.Exists(target))
            {
                return Results.NotFound(new { error = "路径越界或不存在" });
            }
            return Results.Ok(Browse(path));
        });
        app.MapGet("/api/files/download", (HttpContext ctx, string path) => DownloadFile(ctx, path));
        // 末段带文件名的等价路由：部分手机浏览器/系统下载器**忽略 Content-Disposition**，
        // 退化用 URL 末段命名文件——带上真名可避免下载成 "download"（2026-09-11 主人反馈）。
        // 真实路径仍以 path 为准，name 仅用于客户端命名（不在服务端参与任何路径拼接）。
        app.MapGet("/api/files/download/{name}", (HttpContext ctx, string name, string path) => DownloadFile(ctx, path));

        // ── 多选打包下载（流式 ZIP，不落临时文件）──
        // paths 为「|」分隔的相对路径，目录会递归展开。
        app.MapGet("/api/files/download-zip", async (HttpContext ctx, string paths, CancellationToken reqCt) =>
        {
            try
            {
                // ZipArchive 写入（含 Dispose 时写 central directory）是同步 IO，而 Kestrel 默认禁止。
                // 只对本端点按请求放开，不设全局 AllowSynchronousIO——那是给所有端点开后门。
                ctx.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

                string root = ShareRoot;
                var entries = new List<(string Absolute, string EntryName)>();

                void AddEntry(string file)
                {
                    if (entries.Count >= MaxZipEntries)
                    {
                        return;
                    }
                    // ZIP 内统一用正斜杠，避免 Windows 反斜杠在某些解压工具里变成乱码目录
                    entries.Add((file, Path.GetRelativePath(root, file).Replace('\\', '/')));
                }

                foreach (string raw in paths.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    // 越界项静默跳过：不泄露「哪些路径越界」，也不因一项非法让整包失败
                    if (!IsSafeUnderRoot(root, raw))
                    {
                        continue;
                    }

                    string abs = Path.GetFullPath(Path.Combine(root, raw));
                    if (Directory.Exists(abs))
                    {
                        foreach (string file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                        {
                            AddEntry(file);
                            if (entries.Count >= MaxZipEntries)
                            {
                                break;
                            }
                        }
                    }
                    else if (File.Exists(abs))
                    {
                        AddEntry(abs);
                    }
                    if (entries.Count >= MaxZipEntries)
                    {
                        break;
                    }
                }

                if (entries.Count == 0)
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                ctx.Response.ContentType = "application/zip";
                ctx.Response.Headers.ContentDisposition = "attachment; filename=\"SystemToolkit-files.zip\"";
                // leaveOpen: true —— 不能让 ZipArchive 把 Kestrel 的响应流关掉
                using var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
                foreach ((string absolute, string entryName) in entries)
                {
                    reqCt.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(absolute, entryName);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Info("打包下载已取消（客户端断开）。");
            }
            catch (Exception ex)
            {
                // 响应流可能已经写了部分数据，此时无法再改状态码，只能留痕后交给 Kestrel 收尾
                _logger.Warn($"打包下载失败：{ex}");
                throw;
            }
        });

        // ── 分块上传（支持断点续传）──
        // 协议：① GET /api/files/upload-status 查「已传偏移」② POST /api/files/upload-chunk 逐块追加
        // ③ 最后一块写完即定稿（算 SHA-256 + 原子改名）。
        // 分块标识 uploadId 由服务端按「文件名+大小+修改时间」指纹算出（不接受客户端传），
        // 因此即使手机端页面刷新、重新选择同一个文件，也能接着上次的进度传。
        app.MapGet("/api/files/upload-status", (string name, long size, long mtime) =>
        {
            // 与 chunk 端点同一套净化规则：
            // ① 指纹必须用净化后的路径（正反斜杠差异不应改变断点身份）
            // ② 非法路径直接 400，不给探测机会
            string? rel = SanitizeRelativePath(name);
            if (rel is null)
            {
                return Results.BadRequest(new { error = "非法的上传路径。" });
            }

            string root = ShareRoot;
            Directory.CreateDirectory(root);
            string uploadId = ComputeUploadId(rel, size, mtime);
            string partPath = Path.Combine(root, $".upload_{uploadId}.part");
            long received = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            return Results.Ok(new { uploadId, received, total = size, chunkSize = ChunkSizeBytes });
        });

        app.MapPost("/api/files/upload-chunk", async (HttpContext ctx, CancellationToken reqCt) =>
        {
            string name = ctx.Request.Query["name"].ToString();
            long.TryParse(ctx.Request.Query["size"], out long size);
            long.TryParse(ctx.Request.Query["mtime"], out long mtime);
            long.TryParse(ctx.Request.Query["offset"], out long offset);
            try
            {
                if (size <= 0 || size > UploadLimitBytes)
                {
                    return Results.Json(
                        new { error = $"文件大小非法或超过 {UploadLimitBytes / (1024L * 1024 * 1024)}GB 上限。" },
                        statusCode: 413);
                }

                // 防目录穿越：允许子目录（目录上传），但必须是共享目录之内的安全相对路径
                string? rel = SanitizeRelativePath(name);
                if (rel is null)
                {
                    return Results.BadRequest(new { error = "非法的上传路径。" });
                }

                string root = ShareRoot;
                Directory.CreateDirectory(root);

                // 磁盘预检只在第一块做：后续块的剩余空间已在首块核算过，重复检查徒增 IO
                if (offset == 0 && DiskSpaceUtil.Check(root, size) == DiskSpaceCheck.Insufficient)
                {
                    return Results.Json(new { error = "接收目录所在磁盘空间不足，请清理后重试。" }, statusCode: 507);
                }

                // 指纹与 status 端点一致，统一用净化后的 rel
                string uploadId = ComputeUploadId(rel, size, mtime);
                string partPath = Path.Combine(root, $".upload_{uploadId}.part");

                // 同一 uploadId 串行化：避免并发块写入把文件写乱（前端本就逐块传，这里防的是异常重试与多端同传）
                // 🟠 审查 2026-09-11（🟠-1）：闸门改为**引用计数**托管（见 UploadGate 注释）。
                // 原"失败即 TryRemove"会摘掉仍被排队者持有的闸（B 还在等旧对象），
                // 新请求随后拿到新闸即可与 B 并发写同一个 .part。
                UploadGate gate;
                lock (_uploadGateSync)
                {
                    // F-3：取闸与计数必须同处一个临界区——否则在飞持有者可在两步之间摘除该闸
                    gate = _uploadGates.GetOrAdd(uploadId, static _ => new UploadGate());
                    gate.RefCount++;
                }

                try
                {
                    await gate.Semaphore.WaitAsync(reqCt);
                    try
                    {
                        long current = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                        if (current != offset)
                        {
                            // 偏移与服务端已接收长度不符：客户端进度过期，重新查 status 再续
                            return Results.Json(
                                new { error = "偏移不匹配，请重新查询已传进度。", received = current },
                                statusCode: 409);
                        }

                        long incoming = ctx.Request.ContentLength ?? 0;
                        if (incoming <= 0 || offset + incoming > size)
                        {
                            return Results.BadRequest(new { error = "分块长度非法。" });
                        }

                        await AppendChunkAsync(partPath, ctx.Request.Body, incoming, reqCt);
                    }
                    finally
                    {
                        gate.Semaphore.Release();
                    }

                    long received = new FileInfo(partPath).Length;
                    if (received < size)
                    {
                        // 还有后续块：返回后由 finally 递减引用计数；若仍有排队者则闸门保留复用
                        return Results.Ok(new { received, total = size, done = false });
                    }

                    // ── 定稿：算哈希 → 原子落定 ──
                    string hash;
                    await using (FileStream fs = new(partPath, FileMode.Open, FileAccess.Read, FileShare.None,
                        bufferSize: 81920, useAsync: true))
                    {
                        hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, reqCt)).ToLowerInvariant();
                    }

                    // 🟡-11：定稿失败时**保留** .part —— 它不是垃圾而是断点载体：
                    // 重传同一文件（rel/size/mtime 相同）会命中同一 uploadId 与 .part，可直接定稿、免二次上传。
                    // 故此处分歧于"失败即删"的直觉做法，只补一条可诊断的日志说明文件去向。
                    string finalName;
                    try
                    {
                        finalName = FinalizeUpload(root, partPath, rel, mtime);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[Web] 上传定稿失败，已保留断点文件供重传定稿：{partPath}（{ex.Message}）");
                        throw;
                    }

                    _logger.Info($"Web 分块上传完成：{finalName}（{size:N0} 字节，SHA256={hash[..12]}…）。");
                    return Results.Ok(new { received, total = size, done = true, name = finalName, hash });
                }
                finally
                {
                    // 🟠-1：引用计数归零才摘除，且只摘「值仍是本对象」的那条
                    // （TryRemove 的 KeyValuePair 重载做原子比较）。
                    // F-3：减计数与摘除同处一个临界区——与获取侧配对，消除「摘除仍在被取用的闸」的窗口。
                    lock (_uploadGateSync)
                    {
                        if (--gate.RefCount == 0)
                        {
                            _uploadGates.TryRemove(new KeyValuePair<string, UploadGate>(uploadId, gate));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (IOException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 507);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Web 上传异常：{ex.Message}");
                return Results.Json(new { error = "上传失败，" + ex.Message }, statusCode: 500);
            }
        });

        // ── 配对：用短期配对码换取长期令牌 ──
        // 二维码里只放配对码（短期），令牌换到后由手机存 localStorage，不再出现在 URL 与浏览器历史里。
        // 校验与一次性消费委托给 PairingService（成功即 TryConsume 消费，旧码立即失效）。
        app.MapPost("/api/pair", async (HttpContext ctx) =>
        {
            string code = string.Empty;
            try
            {
                using JsonDocument doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (doc.RootElement.TryGetProperty("code", out JsonElement node))
                {
                    code = node.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "请求体不是合法 JSON。" });
            }

            // 失败限速：同 IP 连续失败 5 次封禁 60 秒，抬高 6 位配对码的暴力尝试成本
            // （局域网内风险本就低，这是纵深防御）
            string clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (!PairThrottle.Allow(clientIp))
            {
                return Results.Json(new { error = "配对失败次数过多，请稍后再试。" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            // 先取过期时间（读取会惰性生成当前码），再走常量时间校验 + 一次性消费
            DateTimeOffset expiresAt = _pairing.ExpiresAt;
            if (!_pairing.TryConsume(code))
            {
                PairThrottle.Fail(clientIp);
                return Results.Json(new { error = "配对码不正确或已过期，请重新扫描电脑上的二维码。" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            PairThrottle.Reset(clientIp);
            int expiresInMinutes = Math.Max(1, (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalMinutes));
            return Results.Ok(new { token = _token, expiresInMinutes });
        });

        app.MapGet("/api/devices", () => Results.Ok(SnapshotDevices()));

        _app = app;
        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            // 启动失败（端口被占用等）：回滚状态、释放资源，避免「假运行」无法重启
            _app = null;
            try
            { await app.DisposeAsync().ConfigureAwait(false); }
            catch { }
            throw;
        }

        // ── 订阅设备变化 → 推给已连接浏览器（🟡-5；停止时在 StopAsync 退订）──
        if (_discovery is not null)
        {
            _deviceChangedHandler = (_, e) => _ = BroadcastDeviceChangeAsync(e);
            _discovery.DeviceChanged += _deviceChangedHandler;
        }

        _logger.Info(
            $"Web 文件服务已启动（共享目录 {Path.GetFullPath(ShareRoot)}）。" +
            $"{(_httpsEnabled ? $"HTTPS={_httpsPort} + HTTP={_port} 双监听" : $"HTTP={_port}")}，" +
            $"手机访问：{LanUrl.Replace(PairCode, "******")}");
    }

    /// <inheritdoc/>
    public IEnumerable<FileShareEntry> Browse(string? relativePath = null)
    {
        string root = ShareRoot;
        if (!IsSafeUnderRoot(root, relativePath)
            || !Directory.Exists(Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty))))
        {
            return Array.Empty<FileShareEntry>();
        }

        string fullTarget = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
        var entries = new List<FileShareEntry>();
        foreach (string dir in Directory.EnumerateDirectories(fullTarget))
        {
            var info = new DirectoryInfo(dir);
            entries.Add(new FileShareEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                Size = 0,
                LastModified = info.LastWriteTimeUtc,
                IsDirectory = true,
                RelativePath = Path.GetRelativePath(root, info.FullName),
            });
        }
        // 过滤 .part 临时文件——上传中的 .upload_{guid}.part 与 TCP 断点续传的 {safeName}.{peerIp}.part，
        // 防止手机端看见并下载半截损坏内容；扩展名比较忽略大小写
        foreach (string file in Directory.EnumerateFiles(fullTarget)
            .Where(file => !Path.GetExtension(file).Equals(".part", StringComparison.OrdinalIgnoreCase)))
        {
            var info = new FileInfo(file);
            entries.Add(new FileShareEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                Size = info.Length,
                LastModified = info.LastWriteTimeUtc,
                IsDirectory = false,
                RelativePath = Path.GetRelativePath(root, info.FullName),
            });
        }
        return entries;
    }

    /// <inheritdoc/>
    public async Task WriteUploadedFileAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        string root = ShareRoot;
        Directory.CreateDirectory(root);

        // 防目录穿越 + 防空名：只取纯文件名，丢弃任何路径前缀
        // 🟡 审查 2026-09-10（🟡-12）：补与 SanitizeRelativePath 同口径的校验——原先只查
        // 空名/./..，漏了非法字符与 Windows 保留设备名（CON/NUL/COM1 及其带扩展名形式，
        // 如 CON.txt）。这类名字写进目录后会让后续访问抛异常甚至挂起（NUL 设备语义）。
        string safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)
            || safeName is "." or ".."
            || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || IsWindowsReservedDeviceName(safeName))
        {
            throw new InvalidOperationException("非法的上传文件名。");
        }

        // 同名不覆盖：临时名写入 + Move(overwrite:false) 原子落定，避免半截文件残留与并发竞态
        string tmpPath = Path.Combine(root, $".upload_{Guid.NewGuid():N}.part");
        string finalPath = Path.Combine(root, safeName);
        try
        {
            await using (FileStream fs = new(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await content.CopyToAsync(fs, ct);
            }

            // 原子落定：同名追加序号（a.txt → a (1).txt）；并发竞态时重试（GetUniqueDestination→Move 之间时间窗）
            int retry = 0;
            while (true)
            {
                finalPath = GetUniqueDestination(root, safeName);
                try
                {
                    File.Move(tmpPath, finalPath, overwrite: false);
                    break;
                }
                catch (IOException) when (retry < 3)
                {
                    // 并发上传同名竞态：换个序号重试
                    retry++;
                }
            }
            _logger.Info($"Web 上传完成：{Path.GetFileName(finalPath)}（{new FileInfo(finalPath).Length:N0} 字节）。");
        }
        catch
        {
            // 写入失败：清理临时文件，避免半截文件留在共享目录
            try
            { File.Delete(tmpPath); }
            catch { }
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }

        WebApplication app = _app;
        _app = null;
        // LanUrl 是计算属性（依赖 IsRunning），_app 置空后自动返回空串

        // 实时推送先收尾（🟡-5）：退订设备变化 + 关闭所有 /ws 连接，避免停机后仍在广播
        await StopWebSocketClientsAsync();

        try
        {
            await app.StopAsync();
        }
        catch (OperationCanceledException)
        {
            // 已停止，忽略
        }
        await app.DisposeAsync();

        _logger.Info("Web 文件服务已停止。");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// 同步释放（容器 <c>Dispose()</c> 走这条路径）。
    /// <para>
    /// 🔴 必须同时实现 <see cref="IDisposable"/>：只实现 <see cref="IAsyncDisposable"/> 的类型，
    /// 在宿主同步调用 <c>ServiceProvider.Dispose()</c> 时会抛
    /// <c>InvalidOperationException: type only implements IAsyncDisposable</c>——
    /// 该异常发生在退出路径，会让「关闭程序」变成一次崩溃（2026-09-06 实测）。
    /// </para>
    /// <para>
    /// ⚠️ 这里用「Task.Run + 2s 有界等待」而不是直接同步阻塞等待异步停止：
    /// 退出时当前线程可能是 UI 线程，Kestrel 停止的续行若要回抛到 Dispatcher 会死锁。
    /// 超时即放弃——进程即将退出，端口与句柄由 OS 回收，不值得为它卡住关闭流程。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(2)))
            {
                _logger.Warn("[FileWebServer] 同步停止超时（2s），进程退出时由 OS 回收端口。");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[FileWebServer] 同步停止失败（进程即将退出，忽略）：{ex.Message}");
        }
    }

    // ===================== 内部工具 =====================

    /// <summary>共享根目录（未配置时退化为当前用户的「下载」文件夹）。</summary>
    private string ShareRoot => string.IsNullOrEmpty(_shareDirectory)
        ? UserFolders.GetDownloadsFolder()
        : _shareDirectory;

    /// <summary>
    /// 把上传路径规范化为「共享目录之内的相对路径」（允许子目录），非法一律返回 <c>null</c>。
    /// <para>
    /// 目录上传（<c>webkitdirectory</c>）会让文件名带上相对路径（如 <c>photos/2024/a.jpg</c>），
    /// 因此不能只取 <c>Path.GetFileName</c>——但放开子目录就等于放开目录穿越，
    /// 必须逐段校验：拒绝绝对路径 / UNC / 盘符 / <c>.</c> / <c>..</c> / 非法文件名字符。
    /// 落盘前还会再用 <see cref="PathUtil.IsUnder"/> 复核一次（纵深防御）。
    /// </para>
    /// </summary>
    /// <summary>
    /// 设备列表快照 = **本机条目** + UDP 发现到的其它设备。
    /// <para>
    /// 【2026-09-11 主人反馈「局域网设备显示 0」】<b>根因</b>：发现服务只收录**其它**设备
    /// （<c>DeviceDiscoveryService.ProcessDatagram</c> 显式过滤自身广播），故「电脑 + 手机浏览器」
    /// 这种最常见的单机场景下列表**恒为空**——而前端 <c>renderDevices</c> 一直带有
    /// <c>dev.isLocal</c> 的「本机」渲染分支（含 <c>is-local</c> 样式与「本机」角标），
    /// 说明设计上本机本就该在列。此处由服务端合成，<b>不改发现服务的语义</b>：
    /// <c>FileTransferService.IsKnownPeer</c> 仍只看真实发现结果（不把「自己」当已知对端）。
    /// </para>
    /// <para>
    /// 本机条目的端口填 Web 端口：对手机而言，本机（电脑）唯一可达的入口就是 Web 服务端口，
    /// 这样 <c>DisplayAddress</c> 与 <c>WebUrl</c> 都指向真实可访问的地址，不会出现 <c>:0</c>。
    /// <c>LastSeen</c> 每次快照重新取值 → 本机恒在线（不依赖心跳）。
    /// </para>
    /// </summary>
    private IReadOnlyList<DiscoveredDevice> SnapshotDevices()
    {
        string localId = _discovery?.LocalDeviceId is { Length: > 0 } id
            ? id
            : System.Environment.MachineName;

        var list = new List<DiscoveredDevice>
        {
            new()
            {
                DeviceId = localId,
                Name = System.Environment.MachineName,
                IPAddress = IPAddress.TryParse(_lanIp, out IPAddress? local) ? local : IPAddress.Loopback,
                TransferPort = _port,
                WebPort = _port,
                LastSeen = DateTimeOffset.UtcNow,
                IsLocal = true,
            },
        };

        if (_discovery is not null)
        {
            list.AddRange(_discovery.Devices);
        }

        return list;
    }

    private static string? SanitizeRelativePath(string raw)
    {
        // 🟡 审查 2026-09-11（🟡-2）：单段长度上限（NTFS 255 UTF-16 码元）
        const int MaxSegmentLength = 255;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string cleaned = raw.Replace('\\', '/');
        // 绝对路径 / UNC（//host/share）/ 盘符（C:）一律拒绝
        if (cleaned.StartsWith("//") || Path.IsPathRooted(cleaned) || cleaned.Contains(':') || cleaned.Contains('\0'))
        {
            return null;
        }

        string[] parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 32)
        {
            return null;
        }
        foreach (string part in parts)
        {
            if (part is "." or "..")
            {
                return null;
            }
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }
            if (IsWindowsReservedDeviceName(part))
            {
                return null;
            }
            // 🟡-2：Win32 落盘会**静默裁掉**尾随的 '.' 与空格 —— 后果是日志/响应里报的文件名
            // ≠ 磁盘上的实际名，且 GetUniqueDestination 的「重名探测」按未裁剪的名字去探、
            // 与真实落点错位。此处**拒绝**而非悄悄裁剪改名（改名会让用户找不到自己的文件）。
            if (part.EndsWith('.') || part.EndsWith(' '))
            {
                return null;
            }
            // 🟡-2：超长段会在落盘中途抛 PathTooLongException，在入口拒绝更易排查
            if (part.Length > MaxSegmentLength)
            {
                return null;
            }
        }

        return Path.Combine(parts);
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
    /// 免令牌白名单：① 配对端点 ② 静态前端资源（页面 / JS / CSS）。
    /// <para>
    /// <b>为什么静态资源不再要求令牌</b>：它们只是「应用外壳」，不含任何用户数据，
    /// 真正的数据边界是 <c>/api</c>。旧实现要求它们也带令牌，靠内联 JS 从 URL 读
    /// <c>?t=</c> 再拼到 <c>style.css?t=</c> / <c>app.js?t=</c> 上——二维码改为
    /// <summary>
    /// 构造 HTTP → HTTPS 的 301 Location：<b>剔除 <c>t</c>（访问令牌）</b>，其余查询串保留。
    /// <para>
    /// 令牌一旦出现在 Location 响应头，就等于在同 LAN 明文广播一次长期凭据（S-4）。
    /// 其余参数（如前端 UI 状态）无敏感性，保留以保证跳转后体验不退化。
    /// </para>
    /// </summary>
    private string BuildHttpsLocation(HttpContext ctx)
    {
        string query = string.Join('&', ctx.Request.Query
            .Where(kv => !kv.Key.Equals("t", StringComparison.OrdinalIgnoreCase))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}"));

        // 审查 Y7（2026-09-10）：用服务端自身 _lanIp，不用客户端可控的 ctx.Request.Host.Host（Host 头注入）
        return $"https://{_lanIp}:{_httpsPort}{ctx.Request.Path}"
               + (query.Length > 0 ? "?" + query : string.Empty);
    }

    /// <summary>
    /// 作废旧令牌并生成新令牌（令牌泄露止损）。
    /// <para>
    /// 影响面：所有已配对设备立即 401，须重新扫码配对。这是<b>故意的 fail-closed</b>——
    /// 与"让泄露的令牌继续可用到会话结束"相比，宁可让合法用户重配一次。
    /// 桌面端 <see cref="Url"/> / <see cref="LanUrl"/> 均为计算属性，下一次取值即自动带上新令牌。
    /// </para>
    /// </summary>
    private void RotateToken(string reason)
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _logger.Warn($"[FileWebServer] 访问令牌已轮换（{reason}）；已配对设备需重新配对。");
    }

    /// 只放<b>配对码</b>（<c>?c=</c>）后该方案直接失效：首屏就被 401 拦下，前端没机会执行配对流程。
    /// 放开后：未配对也能加载页面并得到明确提示，配对过则从 localStorage 取令牌直连。
    /// </para>
    /// </summary>
    private static bool IsPublicAsset(PathString path)
        => path.StartsWithSegments("/api/pair")
           || path.Equals("/", StringComparison.Ordinal)
           || path.Equals("/index.html", StringComparison.Ordinal)
           || path.Equals("/app.js", StringComparison.Ordinal)
           || path.Equals("/style.css", StringComparison.Ordinal);

    /// <summary>
    /// 计算上传任务标识：文件名 + 大小 + 修改时间 的指纹。
    /// <para>
    /// 由服务端计算而非客户端传入——客户端传 uploadId 可被伪造来续写他人的半截文件。
    /// 用「文件名+大小+mtime」三元组而非仅文件名：手机端重新选择同一个文件时能命中同一个断点，
    /// 换了个同名但内容不同的文件则会另起炉灶（指纹不同）。
    /// </para>
    /// </summary>
    private static string ComputeUploadId(string name, long size, long mtime)
    {
        string fingerprint = $"{name}|{size}|{mtime}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>
    /// 追加写一个分块到 <c>.part</c> 临时文件。
    /// <para>写入字节数与声明的 Content-Length 不符时（网络截断 / 客户端提前断开），
    /// 必须把文件长度回退到写入前——否则半截块会永久污染续传基线，后续每一块的偏移都对不上。</para>
    /// </summary>
    private static async Task AppendChunkAsync(string partPath, Stream content, long expected, CancellationToken ct)
    {
        await using FileStream fs = new(partPath, FileMode.Append, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        long before = fs.Length;
        try
        {
            await content.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
            if (fs.Length - before != expected)
            {
                throw new IOException($"分块写入长度不符（声明 {expected}，实得 {fs.Length - before}）。");
            }
        }
        catch
        {
            try
            { fs.SetLength(before); }
            catch { /* 回退失败也只能放弃，下次续传会因偏移不匹配报错 */ }
            throw;
        }
    }

    /// <summary>
    /// 定稿：把 <c>.part</c> 原子改名为最终文件（同名追加序号，不覆盖已有文件）。
    /// <para><paramref name="relPath"/> 可含子目录（目录上传场景），会自动创建缺失的中间目录；
    /// 落定目录先用 <see cref="PathUtil.IsUnder"/> 复核（纵深防御）。</para>
    /// </summary>
    private static string FinalizeUpload(string root, string partPath, string relPath, long mtime = 0)
    {
        string relDir = Path.GetDirectoryName(relPath) ?? string.Empty;
        string targetDir = string.IsNullOrEmpty(relDir) ? root : Path.Combine(root, relDir);
        if (!PathUtil.IsUnder(targetDir, root))
        {
            throw new InvalidOperationException("落定目录越界，已拒绝写入。");
        }
        Directory.CreateDirectory(targetDir);

        int retry = 0;
        while (true)
        {
            string finalPath = GetUniqueDestination(targetDir, Path.GetFileName(relPath));
            try
            {
                File.Move(partPath, finalPath, overwrite: false);
                ApplyOriginalMtime(finalPath, mtime);
                return Path.GetFileName(finalPath);
            }
            catch (IOException) when (retry < 3)
            {
                // 与定名前的时间窗内被同名文件抢先：换个序号重试
                retry++;
            }
        }
    }

    /// <summary>
    /// 把文件的修改时间还原为手机端的原始值（Unix 毫秒）。
    /// <para>
    /// 不这么做的话，传到电脑的照片视频一律显示为「刚刚」，按时间排序的相册会全乱套。
    /// 失败（时间非法 / 只读文件 / 不支持的文件系统）一律静默忽略——
    /// 修改时间只是锦上添花，绝不能因为它让一次已经成功的上传报失败。
    /// </para>
    /// </summary>
    private static void ApplyOriginalMtime(string path, long mtimeUnixMs)
    {
        if (mtimeUnixMs <= 0)
        {
            return;
        }
        try
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(mtimeUnixMs);
            // 拒绝明显越界的值（1980 年前或超过当前时间一天以上），避免把文件时间刷成荒唐年份
            if (when.Year < 1980 || when > DateTimeOffset.Now.AddDays(1))
            {
                return;
            }
            File.SetLastWriteTime(path, when.LocalDateTime);
        }
        catch
        {
            // 静默：修改时间不是关键路径
        }
    }

    /// <summary>
    /// 下载文件：返回文件流。路径越界 / 不存在一律 404（不泄露越界原因）。
    /// </summary>
    private IResult DownloadFile(HttpContext ctx, string path)
    {
        string root = ShareRoot;
        if (!IsSafeUnderRoot(root, path))
        {
            _logger.Warn($"已拦截越界下载请求：{path}");
            return Results.NotFound(new { error = "文件不存在或路径越界" });
        }

        string fullTarget = Path.GetFullPath(Path.Combine(root, path));
        if (!File.Exists(fullTarget))
        {
            return Results.NotFound(new { error = "文件不存在或路径越界" });
        }

        string fileName = Path.GetFileName(fullTarget);
        string contentType = new FileShareEntry { Name = fileName }.ContentType;

        // 【2026-09-11 主人反馈：手机端下载下来的文件名被改】
        // 原先走 Results.File(..., fileDownloadName)：其内部用
        // ContentDispositionHeaderValue.SetHttpFileName，非 ASCII 字符只会出现在
        // filename*（RFC 5987）段里，filename 段被转义成 %XX。
        // 支持 filename* 的浏览器没问题；**但部分国产手机浏览器/系统下载器不认 filename***，
        // 拿到的是转义串甚至退化用 URL 末段当名字 → 表现为「文件名被修改」。
        // 改为手工构造，两类客户端各取所需：
        //   filename=   ASCII 回退（保扩展名，非 ASCII 替换为 '_'）
        //   filename*=  UTF-8 正确原名
        // 再配合 URL 末段带文件名（见 /api/files/download/{name} 路由）构成三重保险。
        ctx.Response.Headers.ContentDisposition =
            $"attachment; filename=\"{ToAsciiFallbackFileName(fileName)}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";

        return Results.File(fullTarget, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// 生成 <c>Content-Disposition</c> 的 ASCII 回退文件名：非 ASCII 与不安全字符替换为 '_'，
    /// <b>保留扩展名</b>（丢扩展名会让手机下载器无法交给正确的应用打开）。
    /// </summary>
    private static string ToAsciiFallbackFileName(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (char c in fileName)
        {
            // 可打印 ASCII 且非引号/反斜杠/分号（这三个会破坏 Content-Disposition 语法）
            sb.Append(c is >= ' ' and <= '~' && c is not '"' and not '\\' and not ';' ? c : '_');
        }

        string ascii = sb.ToString().Trim();
        // 全被替换（纯中文名）时至少给个可用名，别产生 "_ _ .txt" 这类空壳
        return ascii.Trim('.', ' ', '_').Length == 0 ? "download" + Path.GetExtension(fileName) : ascii;
    }

    /// <summary>
    /// 从程序集嵌入资源提供静态前端文件。
    /// </summary>
    private static IResult ServeEmbedded(string resourceName, string contentType)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        // 嵌入资源命名：SystemToolkit.Infrastructure.FileTransfer.wwwroot.{文件名}
        string full = $"SystemToolkit.Infrastructure.FileTransfer.wwwroot.{resourceName}";
        Stream? stream = assembly.GetManifestResourceStream(full);
        if (stream is null)
        {
            // 资源不存在时返回内联占位 HTML（防御：嵌入资源配置被误删时手机端仍有可用首页）
            return resourceName == "index.html"
                ? Results.Content(FallbackIndexHtml, "text/html; charset=utf-8")
                : Results.NotFound();
        }
        return Results.Stream(stream, contentType);
    }

    /// <summary>
    /// 401 访问验证页（「登录页」）：完全内联样式（无令牌时 css/js 都会被令牌中间件拦截）。
    /// 视觉与主界面同源：暖纸底 + 档案蓝。只做前端跳转（拼 /?t= 重开），
    /// 令牌校验逻辑仍由中间件完成，不新增任何接口。
    /// </summary>
    private static readonly string UnauthorizedPageHtml = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover"">
<meta name=""theme-color"" content=""#2C4258"">
<meta name=""referrer"" content=""no-referrer"">
<title>SystemToolkit · 访问验证</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','PingFang SC','Microsoft YaHei',sans-serif;
     font-size:14px;line-height:1.5;color:#2A2520;background:#F7F3EC;
     min-height:100vh;min-height:100dvh;display:flex;flex-direction:column}
.bar{background:#2C4258;color:rgba(255,255,255,.85);font-size:12px;
     padding:8px 16px;border-bottom:1px solid #334D63}
.bar b{color:#fff;font-size:18px;font-weight:600;margin-right:auto}
.bar{display:flex;align-items:center;gap:8px}
.wrap{flex:1;display:flex;align-items:center;justify-content:center;padding:16px;
      padding-bottom:calc(16px + env(safe-area-inset-bottom,0))}
.card{width:100%;max-width:380px;background:#FDFCF9;border:1px solid #E2DBCE;border-radius:10px;
      padding:28px 22px;box-shadow:0 1px 3px rgba(63,92,120,.10);text-align:center}
.icon{width:56px;height:56px;margin:0 auto 14px;border-radius:50%;
      background:#E9EEF4;display:flex;align-items:center;justify-content:center;color:#3F5C78}
h1{font-size:16px;font-weight:600;color:#2A2520;margin-bottom:6px}
.sub{font-size:12px;color:#5B554A;line-height:1.7}
.hr{height:1px;background:#EDE7DC;margin:18px 0}
label{display:block;text-align:left;font-size:12px;color:#5B554A;margin-bottom:6px}
input{width:100%;height:44px;padding:0 12px;font-size:16px;color:#2A2520;
      background:#EFE9DC;border:1px solid #DFD3BF;border-radius:6px;outline:none;
      -webkit-appearance:none;appearance:none}
input:focus{border-color:#3F5C78;background:#FFFFFF;
      box-shadow:0 0 0 3px rgba(63,92,120,.15)}
button{width:100%;height:44px;margin-top:12px;border:none;border-radius:6px;
       background:#3F5C78;color:#fff;font-size:14px;font-weight:600;
       font-family:inherit;cursor:pointer;-webkit-appearance:none;appearance:none;
       transition:background 120ms ease-out}
button:active{background:#334D63}
.err{display:none;margin-top:12px;padding:8px 12px;font-size:12px;text-align:left;
     color:#92400E;background:rgba(154,100,21,.12);border-radius:6px}
.err.is-show{display:block}
.tip{margin-top:16px;font-size:12px;color:#6E675D;line-height:1.7}
.foot{padding:12px;text-align:center;font-size:12px;color:rgba(255,255,255,.55)}
</style>
</head>
<body>
<div class=""bar""><b>SystemToolkit</b><span>文件互传</span></div>
<div class=""wrap""><div class=""card"">
  <div class=""icon""><svg width=""28"" height=""28"" viewBox=""0 0 24 24"" fill=""none""
       stroke=""currentColor"" stroke-width=""1.6"" stroke-linecap=""round"" stroke-linejoin=""round"">
       <rect x=""4"" y=""10"" width=""16"" height=""11"" rx=""2""/>
       <path d=""M8 10V7a4 4 0 018 0v3""/><circle cx=""12"" cy=""15.5"" r=""1.4""/></svg></div>
  <h1>需要访问令牌</h1>
  <div class=""sub"">请使用桌面端「文件互传」页面展示的<br>二维码扫码，或复制带令牌的完整链接打开。</div>
  <div class=""hr""></div>
  <form id=""f"">
    <label for=""t"">或手动输入访问令牌</label>
    <input id=""t"" name=""t"" type=""text"" inputmode=""latin"" autocomplete=""off""
           spellcheck=""false"" placeholder=""32 位十六进制令牌""/>
    <button type=""submit"">打开文件互传</button>
  </form>
  <div class=""err"" id=""err""></div>
  <div class=""tip"">提示：桌面端每次启动 Web 服务后令牌会更新，<br>旧链接失效属正常现象，请重新扫码。</div>
</div></div>
<div class=""foot"">SystemToolkit · 局域网文件互传</div>
<script>
(function(){
  var err = document.getElementById('err');
  // URL 里已带 t 却仍被拒：说明令牌不正确或已过期，给出明确回显
  if (new URLSearchParams(window.location.search).get('t')) {
    err.textContent = '令牌不正确或已过期：桌面端重启 Web 服务后令牌会刷新，请重新扫码获取。';
    err.classList.add('is-show');
  }
  document.getElementById('f').addEventListener('submit', function (e) {
    e.preventDefault();
    var v = document.getElementById('t').value.trim();
    if (!v) { err.textContent = '请先输入访问令牌。'; err.classList.add('is-show'); return; }
    location.replace('/?t=' + encodeURIComponent(v));
  });
})();
</script>
</body>
</html>";

    /// <summary>当 index.html 嵌入资源缺失时的内联兜底页面。</summary>
    private static readonly string FallbackIndexHtml =
        "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>SystemToolkit 文件互传</title>" +
        "<link rel=\"stylesheet\" href=\"/style.css\"></head>" +
        "<body><div class=\"app\"><header class=\"status-bar\">" +
        "<span class=\"status-bar__title\">SystemToolkit 文件互传</span>" +
        "<span class=\"status-bar__conn\"><span class=\"status-bar__dot is-online\">" +
        "</span>已连接</span></header><main class=\"main\">" +
        "<div class=\"tab-panel is-active\"><p class=\"empty-state\">" +
        "Web 前端资源构建中，请使用 API 接口访问文件。</p>" +
        "</div></main></div></body></html>";

    /// <summary>
    /// 防目录穿越：目标解析后必须落在共享根目录之内（或等于根目录本身）。
    /// <para>先合并再取全路径，可同时拦截 <c>../</c> 相对穿越与「绝对路径替换」
    /// （Path.Combine 第二参数为 rooted path 时会整体替换）。</para>
    /// </summary>
    private static bool IsSafeUnderRoot(string root, string? relative)
    {
        try
        {
            string fullRoot = Path.GetFullPath(root);
            string fullTarget = Path.GetFullPath(Path.Combine(fullRoot, relative ?? string.Empty));
            if (string.Equals(fullTarget, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            if (!fullTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 安全硬化：共享根内若存在 symlink/junction，会让人从根外读取文件（download/zip），也影响写入落点。
            // 词法 GetFullPath 不解析 reparse，此处沿相对路径逐段查找并拒绝。
            if (HasReparsePointUnderRoot(fullRoot, fullTarget))
            {
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            // 非法字符等 Path 异常一律视为越界
            return false;
        }
    }

    /// <summary>沿 fullRoot 到 fullTarget 的相对路径逐段检查；任一已存在段为 reparse point 则视为不安全。</summary>
    private static bool HasReparsePointUnderRoot(string root, string target)
    {
        string rel = target.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = root;
        foreach (string segment in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 权限/命名等异常交由实际 IO 决定，不在此误判
            }
        }
        return false;
    }

    /// <summary>目标目录内取不冲突的落定路径：同名时追加 " (n)" 序号，绝不覆盖已有文件。</summary>
    private static string GetUniqueDestination(string dir, string fileName)
    {
        string candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 1; ; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 探测本机局域网 IPv4：借助 UDP connect 让系统选出默认路由的源地址（不实际发包）。
    /// 失败时回退主机名解析，再失败回退 127.0.0.1。
    /// </summary>
    private static string DetectLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep && !IPAddress.Any.Equals(ep.Address))
            {
                return ep.Address.ToString();
            }
        }
        catch
        {
            // 无默认路由等情况，走回退
        }

        try
        {
            System.Net.IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            IPAddress? ip = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is not null)
            {
                return ip.ToString();
            }
        }
        catch
        {
            // 解析失败
        }

        return "127.0.0.1";
    }

    /// <summary>
    /// 配对失败限速（进程内简单实现）：同 IP 连续失败 ≥5 次 → 封禁 60 秒；成功即清零。
    /// </summary>
    private static class PairThrottle
    {
        private const int MaxFails = 5;
        private static readonly TimeSpan BanWindow = TimeSpan.FromSeconds(60);
        private static readonly object Sync = new();
        private static readonly Dictionary<string, (int Fails, DateTimeOffset BannedUntil)> Map = new();

        /// <summary>该 IP 当前是否允许尝试配对。</summary>
        public static bool Allow(string ip)
        {
            lock (Sync)
            {
                if (!Map.TryGetValue(ip, out (int Fails, DateTimeOffset BannedUntil) e))
                {
                    return true;
                }
                return DateTimeOffset.Now >= e.BannedUntil;
            }
        }

        /// <summary>条目数达到该阈值时触发过期清扫（🟡-9：Map 按 IP 累积、原无回收机制）。</summary>
        private const int PruneThreshold = 256;

        /// <summary>记录一次配对失败；连续失败达到阈值即进入封禁窗口。</summary>
        public static void Fail(string ip)
        {
            lock (Sync)
            {
                PruneExpired();

                (int fails, DateTimeOffset banned) = Map.TryGetValue(ip,
                    out (int Fails, DateTimeOffset BannedUntil) e) ? e : (0, DateTimeOffset.MinValue);
                fails++;
                Map[ip] = (fails, fails >= MaxFails ? DateTimeOffset.Now + BanWindow : banned);
            }
        }

        /// <summary>
        /// 清扫「封禁期已结束」的条目（🟡 审查 2026-09-10）。调用方须已持有 <see cref="Sync"/>。
        /// <para>
        /// 🔴 判据必须是「曾经封禁且已过期」（<c>BannedUntil != MinValue</c>）：
        /// 从未触发封禁的条目（<c>BannedUntil == MinValue</c>）代表"失败计数仍在累积中"，
        /// 若一并清除，攻击者只要在计数达到 MaxFails 之前触发一次清扫就能把计数清零、绕过限流。
        /// </para>
        /// <para>仅在条目数达阈值时才全表扫描，避免每次失败都遍历。</para>
        /// </summary>
        private static void PruneExpired()
        {
            if (Map.Count < PruneThreshold)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            List<string> expired = [];
            foreach (KeyValuePair<string, (int Fails, DateTimeOffset BannedUntil)> entry in Map)
            {
                if (entry.Value.BannedUntil != DateTimeOffset.MinValue && now >= entry.Value.BannedUntil)
                {
                    expired.Add(entry.Key);
                }
            }

            foreach (string key in expired)
            {
                Map.Remove(key);
            }
        }

        /// <summary>配对成功，清除该 IP 的失败计数。</summary>
        public static void Reset(string ip)
        {
            lock (Sync)
            {
                Map.Remove(ip);
            }
        }
    }

    /// <summary>
    /// 自签证书管理：为 Web 服务的 HTTPS 模式生成并复用一张证书（默认关闭 HTTPS，理由见 TransferSettings.UseHttps）。
    /// <para>
    /// 局域网内没有公共 CA 能给「192.168.x.x」签发证书，必须用自签；代价是手机首次访问
    /// 会看到「证书不受信任」警告。存 Windows 当前用户证书存储而非 PFX 文件：
    /// PFX 需要额外保护导出密码（密码硬编码等于没保护），系统存储由 OS 保护私钥。
    /// </para>
    /// </summary>
    private static class SelfSignedCertificate
    {
        /// <summary>证书主题名（同时用于在存储中查找已有证书）。</summary>
        private const string SubjectName = "CN=SystemToolkit FileTransfer";

        /// <summary>有效期：3 年。到期后会自动重新生成。</summary>
        private const int ValidYears = 3;

        /// <summary>
        /// 取得可用的自签证书（已存在且未过期则复用，否则生成新的并存入当前用户证书存储）。
        /// </summary>
        /// <param name="lanIp">局域网 IP，会写入 SAN；浏览器按 IP 访问时必须有对应 SAN，否则证书校验失败。</param>
        public static X509Certificate2 GetOrCreate(string lanIp)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            X509Certificate2? existing = FindValid(store, lanIp);
            if (existing is not null)
            {
                return existing;
            }

            // 网络不匹配：先清理同主题旧证书（注意顺序——必须在 Add 新证书之前，否则会把新证书一并清掉）
            foreach (X509Certificate2 stale in store.Certificates.Find(
                X509FindType.FindBySubjectDistinguishedName, SubjectName, false))
            {
                try
                { store.Remove(stale); }
                catch { /* 清理失败不影响后续生成 */ }
                stale.Dispose();
            }

            using X509Certificate2 created = Create(lanIp);
            try
            {
                // PersistKeySet：不加这个标志私钥不会持久化，下次启动证书能找到但无法用于 HTTPS。
                // 用 X509CertificateLoader 而非 X509Certificate2 构造函数（后者在 .NET 9+ 已过时，SYSLIB0057）。
                store.Add(X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx),
                    ReadOnlySpan<char>.Empty, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet));
            }
            finally
            {
                created.Dispose();
            }

            return FindValid(store, lanIp)
                   ?? throw new InvalidOperationException("自签证书创建后无法从证书存储读回，请检查用户证书存储权限。");
        }

        /// <summary>
        /// 找到「有效且与当前网络匹配」的证书。
        /// <para>此前只查有效期——证书 SAN 是创建时写入的局域网 IP，换网络（IP 变化）后仍复用
        /// 旧证书，浏览器会判「证书与地址不匹配」。现在同时要求 SAN 包含当前 LAN IP 与主机名。</para>
        /// </summary>
        private static X509Certificate2? FindValid(X509Store store, string lanIp)
        {
            foreach (X509Certificate2 cert in store.Certificates.Find(
                X509FindType.FindBySubjectDistinguishedName, SubjectName, false))
            {
                if (cert.HasPrivateKey && cert.NotAfter > DateTime.Now.AddDays(30) && SanMatches(cert, lanIp))
                {
                    return cert;
                }
                cert.Dispose();
            }
            return null;
        }

        /// <summary>检查证书 SAN 是否覆盖当前主机名与局域网 IP。</summary>
        private static bool SanMatches(X509Certificate2 cert, string lanIp)
        {
            try
            {
                X509SubjectAlternativeNameExtension? san =
                    cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                if (san is null)
                {
                    return false;
                }
                return san.EnumerateDnsNames().Any(n =>
                           string.Equals(n, System.Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(n, "localhost", StringComparison.OrdinalIgnoreCase))
                       && san.EnumerateIPAddresses().Any(ip =>
                           string.Equals(ip.ToString(), lanIp, StringComparison.Ordinal));
            }
            catch
            {
                // SAN 解析异常按不匹配处理（走重新生成）
                return false;
            }
        }

        private static X509Certificate2 Create(string lanIp)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // SAN 必须覆盖所有可能的访问方式：机器名 / 局域网 IP / 本机回环 / localhost，
            // 少一个都会让浏览器判定「证书与地址不匹配」
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(System.Environment.MachineName);
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            if (IPAddress.TryParse(lanIp, out IPAddress? ip))
            {
                san.AddIpAddress(ip);
            }
            request.CertificateExtensions.Add(san.Build());

            // 标记为服务端用途（客户端认证不需要）
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));

            return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(ValidYears));
        }
    }

    /// <summary>
    /// 框架日志桥接：把 Microsoft.Extensions.Logging 的 Kestrel/ASP.NET 日志转发到 Core 的最小日志契约。
    /// <para>宿主是 WPF 进程（无控制台），不桥接的话框架日志（含启动失败真因）默认进黑洞。</para>
    /// </summary>
    private sealed class ForwardingLoggerProvider : ILoggerProvider
    {
        private readonly SystemToolkit.Core.Contracts.ILogger _sink;

        public ForwardingLoggerProvider(SystemToolkit.Core.Contracts.ILogger sink)
        {
            _sink = sink;
        }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        {
            return new ForwardingLogger(categoryName, _sink);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>按类别转发单条框架日志到 Core 日志契约（Information→Info / Warning→Warn / Error+→Error）。</summary>
    private sealed class ForwardingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly string _category;
        private readonly SystemToolkit.Core.Contracts.ILogger _sink;

        public ForwardingLogger(string category, SystemToolkit.Core.Contracts.ILogger sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        {
            return logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;
        }

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = $"{_category}: {formatter(state, exception)}";
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
            {
                _sink.Error(message, exception);
            }
            else if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                _sink.Warn(message);
            }
            else
            {
                _sink.Info(message);
            }
        }
    }
}
