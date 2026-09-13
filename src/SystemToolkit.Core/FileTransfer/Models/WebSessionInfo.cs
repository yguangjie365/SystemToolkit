namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// Web 通道的**已授权访问会话**（手机/其它浏览器用一次性配对码换来的长期凭据）。
/// <para>
/// 设计依据：协议 §5.3「会话（Session）」——配对成功即签发会话；UI 可踢出会话；
/// 设备删除时级联撤销其全部会话。
/// </para>
/// <para>
/// 🔴 本模型是**只读展示投影，不含令牌本身**：令牌是凭据，界面只需要"哪台设备 / 何时来 / 能不能踢"。
/// 曾考虑把令牌摘要也带上，但那只是把凭据换个地方暴露，没有任何展示价值。
/// </para>
/// </summary>
/// <param name="Id">会话标识（短 hex，供「踢出」操作引用）。</param>
/// <param name="Label">设备标签（由 User-Agent 归纳，如 <c>Android · Chrome</c>；认不出时为 <c>未知设备</c>）。</param>
/// <param name="Ip">最近一次请求的来源 IP。</param>
/// <param name="CreatedAt">签发时间。</param>
/// <param name="LastSeenAt">最近一次访问时间（用来判断"这台还在用吗"）。</param>
/// <param name="IsLocalPreview">是否为本机预览会话（桌面「打开网页」用）——不列入可踢出的手机列表。</param>
public sealed record WebSessionInfo(
    string Id,
    string Label,
    string Ip,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool IsLocalPreview = false);
