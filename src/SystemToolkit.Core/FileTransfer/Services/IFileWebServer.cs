using SystemToolkit.Core.FileTransfer.Models;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// Web 文件服务接口：通过 HTTP 暴露共享目录，供手机浏览器访问。
/// 由 Kestrel 实现具体逻辑（定义在模块层，Core 仅声明契约）。
/// </summary>
public interface IFileWebServer : IAsyncDisposable
{
    /// <summary>Web 服务是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>当前监听端口。</summary>
    int Port { get; }

    /// <summary>是否启用 HTTPS（自签证书）。<c>false</c> 表示仅 HTTP 明文，访问令牌可能被同网段嗅探。</summary>
    bool IsHttps { get; }

    /// <summary>本机访问 URL（localhost，供「打开网页」按钮使用）。</summary>
    string Url { get; }

    /// <summary>
    /// 局域网访问 URL（二维码内容），形如 http://&lt;局域网IP&gt;:&lt;端口&gt;/?c=&lt;配对码&gt;。
    /// <para>
    /// 这里放的是**短期配对码**而不是长期令牌：二维码极易被截图、被旁人拍到，
    /// 长期令牌一旦外泄即永久失守。手机扫码后用配对码换取令牌并自行保存，令牌不再出现在 URL 里。
    /// </para>
    /// 未运行时为空串。
    /// </summary>
    string LanUrl { get; }

    /// <summary>本次运行周期内的访问令牌（每次 StartAsync 重新生成）。</summary>
    /// <remarks>
    /// 🔴 2026-09-13 起语义收窄：它现在只是**本机预览会话**的令牌（桌面「打开网页」用），
    /// 不再是"全网唯一令牌"。手机端经配对码各自换取自己的会话令牌（见 <see cref="Sessions"/>）。
    /// </remarks>
    string Token { get; }

    /// <summary>
    /// 当前已授权的远程访问会话（手机等浏览器），含最近访问时间；不含已撤销者。
    /// </summary>
    /// <remarks>设计依据：协议 §5.3。本机预览会话不在其中（它不是"来访设备"）。</remarks>
    IReadOnlyList<WebSessionInfo> Sessions { get; }

    /// <summary>会话集合发生变化（签发 / 撤销 / 访问刷新）时触发，供 UI 刷新列表。</summary>
    event EventHandler? SessionsChanged;

    /// <summary>
    /// 浏览器（手机端）经 <c>POST /api/text</c> 发来文本时触发（W2）。
    /// <para>
    /// 服务端只做「转达」：<b>不写剪贴板、不弹窗、不落文件</b>——这三件事都属于订阅方的决策。
    /// 订阅方抛出的异常会被吞掉并留痕，不会让已返回 200 的请求变成 500。
    /// </para>
    /// </summary>
    event EventHandler<WebTextReceivedEventArgs>? TextReceived;

    /// <summary>
    /// 撤销指定会话（"踢出"）。返回是否真的撤销了——令牌随之**立即失效**，
    /// 该设备需要重新扫码配对。
    /// </summary>
    bool RevokeSession(string sessionId);

    /// <summary>
    /// 撤销全部**远程**会话（手机等），保留本机预览会话（否则桌面「打开网页」会失效）。
    /// 返回被撤销的会话数。
    /// </summary>
    int RevokeAllSessions();

    /// <summary>
    /// 当前 HTTPS 自签证书的 SHA-256 指纹（仅 hex，冒号由展示层加）。
    /// 未启用 HTTPS 时为空串。
    /// </summary>
    /// <remarks>
    /// 协议 §6.3 要求"证书变更时页面明确提示重新信任"——手机端拿不到 TLS 证书指纹
    /// （JS 无此 API），只能由服务端告知并与上次比对。
    /// </remarks>
    string CertFingerprint { get; }

    /// <summary>
    /// 当前有效的配对码（过期时惰性轮换）。未运行/未生成时为空串。
    /// </summary>
    string PairCode { get; }

    /// <summary>
    /// 服务端**实际使用**的共享根目录（绝对路径）——共享目录的**唯一真源**。
    /// <para>
    /// 🔴 为什么必须由服务端给出（🟠 审查 v8-🟠-1）：桌面侧另有一份独立持久化的
    /// 「接收目录」（<c>TransferSettings.ReceiveDirectory</c>），手机侧配置的是
    /// <c>MobileConfig.ShareDirectory</c>。两者是**两份互不同步的字段**，各自被不同入口读取：
    /// </para>
    /// <list type="bullet">
    /// <item>只配了共享目录（启动 Web 服务的**必要**条件）时，桌面侧按自己的字段判"未设置"
    /// → 「发文件到手机」直接失效；</item>
    /// <item>两者指向不同目录时，文件被复制到 A，随后 <c>PublishFileOfferAsync</c> 去 B 里找
    /// → 抛"共享目录里找不到该文件" → 推送全败。</item>
    /// </list>
    /// <para>
    /// 以本属性为准，则「复制目标」与「<c>/api/files</c> 的可见范围」恒为同一个目录。
    /// 未配置共享目录时退化为当前用户的「下载」文件夹（与 <c>Browse</c> 的根一致）。
    /// </para>
    /// </summary>
    string SharedRoot { get; }

    /// <summary>
    /// 启动 Web 服务。
    /// </summary>
    /// <param name="settings">传输配置。</param>
    /// <param name="shareDirectory">共享目录路径。</param>
    /// <param name="ct">取消令牌。</param>
    Task StartAsync(TransferSettings settings, string shareDirectory, CancellationToken ct = default);

    /// <summary>停止 Web 服务。</summary>
    Task StopAsync();

    /// <summary>
    /// 浏览共享目录。
    /// </summary>
    IEnumerable<FileShareEntry> Browse(string? relativePath = null);

    /// <summary>
    /// 添加文件到共享目录（供 Web 端上传写入）。
    /// </summary>
    Task WriteUploadedFileAsync(string fileName, Stream content, CancellationToken ct = default);

    /// <summary>
    /// 推一条文本给**所有已连接的浏览器**（电脑 → 手机，W2）；WS 消息类型 <c>chatMessage</c>。
    /// <para>
    /// 🔴 与 <c>FileTransferService.SendTextAsync</c> 同一校验口径：空文本与超过
    /// <see cref="TransferText.MaxBytes"/> 的文本一律**抛 <see cref="ArgumentException"/>**，
    /// 绝不静默截断后回一个假的送达数（截断会把一条长链接变成失效链接，而界面却显示"已送达"）。
    /// </para>
    /// <para>
    /// <b>为什么是广播而不是"发给指定会话"</b>：WS 连接与 HTTP 会话之间没有服务端侧的绑定
    /// （一次配对可以在同一台手机上开多个标签页），指定会话在服务端无从落地。
    /// 局域网内已配对的浏览器是个位数，广播的代价比"伪造一个精确的假象"低得多。
    /// </para>
    /// </summary>
    /// <param name="text">待推送文本（需先通过 <see cref="TransferText.Validate"/>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际送达的连接数（0 = 无人在线；**不代表对方已读**，只代表帧已写出）。</returns>
    Task<int> BroadcastTextAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 把一个**共享目录内**的文件推送给所有浏览器（电脑 → 手机，W3）；WS 消息类型 <c>fileOffered</c>。
    /// <para>
    /// 推的是"**邀请下载**"而不是文件本身：手机端会看到一个带「下载」按钮的气泡，
    /// 点击走既有的 <c>GET /api/files/download</c>。理由：手机浏览器无法把"推送的字节"存成文件
    /// （没有用户手势就触发不了保存），而"推送 → 用户点 → 正常下载"既有进度条也符合浏览器安全模型。
    /// </para>
    /// <para>
    /// 🔴 路径必须落在共享目录内且真实存在，否则**抛 <see cref="ArgumentException"/>**
    /// （与 <see cref="BroadcastTextAsync"/> 同口径：拒绝而不是静默）。不校验的话，
    /// 手机端会拿到一个点了必然 404 的气泡——用户以为文件已经在那儿了。
    /// </para>
    /// </summary>
    /// <param name="relativePath">共享目录内的相对路径（如 <c>report.pdf</c> 或 <c>docs/a.txt</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际送达的连接数（0 = 无人在线）。</returns>
    Task<int> PublishFileOfferAsync(string relativePath, CancellationToken ct = default);
}
