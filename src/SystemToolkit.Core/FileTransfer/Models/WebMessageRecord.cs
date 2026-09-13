namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 一条推给浏览器的**会话内容消息**（W3 重连补拉用）。
/// <para>
/// 🔴 <b>它存在的理由是一条浏览器侧硬约束</b>：手机锁屏/切后台时 WebSocket 会被系统回收，
/// 断连期间电脑发来的消息**收不到、重连也不会自动补** —— 不做补拉就是**静默丢消息**
/// （违反本仓「禁止静默失败」红线）。
/// </para>
/// <para>
/// <b>只记"会话内容"</b>（<c>chatMessage</c> / <c>fileOffered</c>），不记瞬时状态：
/// 设备列表、在线浏览器、传输进度都是"当下的事实"，补拉一条过期进度只会误导用户。
/// 那些状态在重连时会由服务端的首帧三连重新给出。
/// </para>
/// <para>
/// <b>内存态、进程内</b>：服务重启即失，这是有意的（持久化会话涉及隐私与清理策略，
/// 方案里列为可选项）。序号在重启后归零，补拉端点会如实回一个 <c>truncated</c> 标志告诉前端"有缺口"。
/// </para>
/// </summary>
/// <param name="Seq">自增序号（从 1 开始；0 保留给"未入流水"的消息）。</param>
/// <param name="Type">消息类型（与 WS 帧的 <c>type</c> 同名：<c>chatMessage</c> / <c>fileOffered</c>）。</param>
/// <param name="PayloadJson">已序列化的 payload（**与 WS 帧同形**，于是前端能用同一段渲染代码消费）。</param>
/// <param name="At">产生时刻（UTC）。</param>
public sealed record WebMessageRecord(long Seq, string Type, string PayloadJson, DateTimeOffset At);
