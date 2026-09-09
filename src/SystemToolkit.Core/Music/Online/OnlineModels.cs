namespace SystemToolkit.Core.Music.Online;

//（PlayState/PlayMode 未移植：新项目 Core/Music.Models 已有同语义类型。
//  原「在线队列专用 PlayMode（含 Heartbeat 心动模式）」在 OM-4 接线时移除——
//  本地/在线共用同一队列与同一播放模式切换（Models.PlayMode），心跳模式属超范围裁定外能力，
//  如后续要做心动模式，随对应里程碑以独立类型引入，避免与本地 PlayMode 二义性。）

/// <summary>音质选项（对照 NexBox OnlinePlaybackQuality；值即 API level 参数）。</summary>
public enum OnlinePlaybackQuality
{
    /// <summary>标准（128k）</summary>
    Standard,
    /// <summary>较高（320k）</summary>
    Exhigh,
    /// <summary>极高/无损（FLAC）</summary>
    Lossless,
    /// <summary>Hi-Res</summary>
    Hires,
}

/// <summary>音乐源（Local 只用于本地收藏/本地歌单键值分离，不进入平台切换 UI）。</summary>
public enum OnlineProvider
{
    /// <summary>网易云音乐。</summary>
    NetEase,
    /// <summary>QQ 音乐。</summary>
    QQMusic,
    /// <summary>本地音乐（不在平台切换下拉里展示；只用在 LikeKey / 收藏 CacheKey 防止和 QQ/网易重名冲突）。</summary>
    Local,
}

/// <summary>统一歌曲结构（跨 QQ 音乐 / 网易云）。</summary>
public sealed record OnlineTrack
{
    /// <summary>歌曲来源平台。</summary>
    public OnlineProvider Provider { get; init; }

    /// <summary>平台侧歌曲 ID（网易云为数字 id 字符串；QQ 为 song mid）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>QQ 音乐 song mid（与 <see cref="Id"/> 冗余时取其一；网易云不使用）。</summary>
    public string? Mid { get; init; }

    /// <summary>QQ 音乐 media mid（部分 URL 接口要求 media_mid 而非 mid）。</summary>
    public string? MediaMid { get; init; }

    /// <summary>歌曲名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>艺术家（多艺术家以「/」拼接的展示串）。</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>专辑名。</summary>
    public string Album { get; init; } = string.Empty;

    /// <summary>封面图 URL（直链；展示前经代理补 Referer）。</summary>
    public string Cover { get; init; } = string.Empty;

    /// <summary>时长（毫秒）。</summary>
    public ulong DurationMs { get; init; }

    /// <summary>0=免费, 1=VIP, 8=试听片段。</summary>
    public int Fee { get; init; }

    /// <summary>客户端预判的可播性（false 时 UI 置灰，点击后仍以 URL 结果为准）。</summary>
    public bool Playable { get; init; }

    /// <summary>QQ 音乐数字 ID（网易云不使用）。</summary>
    public long? QqSongId { get; init; }

    /// <summary>本地歌曲路径（provider=Local 时使用；混合队列与在线曲目同一容器）。</summary>
    public string? LocalPath { get; init; }
}

/// <summary>歌手信息。</summary>
public sealed record OnlineArtist
{
    /// <summary>歌手 ID（网易云数字 id；QQ 为空则用 mid）。</summary>
    public string? Id { get; init; }

    /// <summary>QQ 歌手 mid。</summary>
    public string? Mid { get; init; }

    /// <summary>歌手名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>头像 URL。</summary>
    public string? Avatar { get; init; }

    /// <summary>歌曲数（搜索结果可能为 0）。</summary>
    public int SongCount { get; init; }

    /// <summary>专辑数（搜索结果可能为 0）。</summary>
    public int AlbumCount { get; init; }

    /// <summary>简介（详情接口才有）。</summary>
    public string? Brief { get; init; }
}

/// <summary>歌单信息。</summary>
public sealed record OnlinePlaylist
{
    /// <summary>歌单来源平台。</summary>
    public OnlineProvider Provider { get; init; }

    /// <summary>歌单 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>歌单名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>封面图 URL。</summary>
    public string Cover { get; init; } = string.Empty;

    /// <summary>曲目总数。</summary>
    public uint TrackCount { get; init; }

    /// <summary>创建者昵称。</summary>
    public string Creator { get; init; } = string.Empty;

    /// <summary>歌单描述（详情接口才有）。</summary>
    public string? Description { get; init; }

    /// <summary>播放次数（平台提供时非 0）。</summary>
    public int PlayCount { get; init; }
}

/// <summary>专辑信息。</summary>
public sealed record OnlineAlbum
{
    /// <summary>专辑来源平台。</summary>
    public OnlineProvider Provider { get; init; }

    /// <summary>专辑 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>QQ 专辑 mid。</summary>
    public string Mid { get; init; } = string.Empty;

    /// <summary>专辑名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>主艺术家。</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>封面图 URL。</summary>
    public string Cover { get; init; } = string.Empty;

    /// <summary>发行日期文本（平台格式原样）。</summary>
    public string? PublishDate { get; init; }

    /// <summary>专辑描述。</summary>
    public string? Description { get; init; }

    /// <summary>曲目数。</summary>
    public int SongCount { get; init; }
}

/// <summary>播放地址获取结果（语义对齐 NexBox SongUrlResult）。</summary>
public sealed record OnlineSongUrlResult
{
    /// <summary>播放直链（不可播时为 null）。</summary>
    public string? Url { get; init; }

    /// <summary>是否可播（false 时 UI 应自动跳过并提示 <see cref="Reason"/>/<see cref="Message"/>）。</summary>
    public bool Playable { get; init; }

    /// <summary>是否试听片段（VIP 曲目的低音质试听）。</summary>
    public bool Trial { get; init; }

    /// <summary>实际命中的音质 level（如 hires/lossless/exhigh/standard）。</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>实际命中的音质展示名。</summary>
    public string Quality { get; init; } = string.Empty;

    /// <summary>比特率（bps）。</summary>
    public ulong Br { get; init; }

    /// <summary>不可播原因分类（如 QQ_URL_UNAVAILABLE）。</summary>
    public string? Reason { get; init; }

    /// <summary>不可播的人类可读说明（版权/会员等）。</summary>
    public string? Message { get; init; }

    /// <summary>该曲收费类型（平台下发时非空）。</summary>
    public int? Fee { get; init; }
}

/// <summary>歌词（含翻译/罗马音/逐行 YRC）。</summary>
public sealed record OnlineLyrics
{
    /// <summary>原文 LRC 文本。</summary>
    public string Lyric { get; init; } = string.Empty;

    /// <summary>翻译歌词（可能为 null）。</summary>
    public string? Translation { get; init; }

    /// <summary>罗马音歌词（可能为 null）。</summary>
    public string? Roma { get; init; }

    /// <summary>逐字歌词 YRC（网易云独有，可能为 null）。</summary>
    public string? Yrc { get; init; }
}

/// <summary>登录信息（单平台）。</summary>
public sealed record OnlineLoginInfo
{
    /// <summary>所属平台。</summary>
    public OnlineProvider Provider { get; init; }

    /// <summary>是否已登录。</summary>
    public bool LoggedIn { get; init; }

    /// <summary>昵称。</summary>
    public string? Nickname { get; init; }

    /// <summary>头像 URL。</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>平台用户 ID。</summary>
    public string? Uid { get; init; }

    /// <summary>VIP 等级：0=非会员，1-9=VIP，&gt;=10=SVIP（网易云 vipType 口径；QQ 侧取探针最积极判定）。</summary>
    public int VipType { get; init; }
}

/// <summary>歌手详情。</summary>
public sealed record OnlineArtistDetail
{
    /// <summary>歌手 ID。</summary>
    public string? Id { get; init; }

    /// <summary>歌手名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>头像 URL。</summary>
    public string? Avatar { get; init; }

    /// <summary>简介。</summary>
    public string? Brief { get; init; }

    /// <summary>歌曲数。</summary>
    public int SongCount { get; init; }

    /// <summary>专辑数。</summary>
    public int AlbumCount { get; init; }

    /// <summary>粉丝数。</summary>
    public int FanCount { get; init; }
}

/// <summary>MV 信息。</summary>
public sealed record OnlineMv
{
    /// <summary>MV ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>MV 名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>主艺术家。</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>封面 URL。</summary>
    public string Cover { get; init; } = string.Empty;

    /// <summary>时长（毫秒）。</summary>
    public ulong DurationMs { get; init; }

    /// <summary>播放次数。</summary>
    public int PlayCount { get; init; }
}

/// <summary>二维码登录：返回的 key 与二维码 URL。</summary>
public sealed record OnlineQrLoginResult
{
    /// <summary>轮询用的登录 key。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>二维码内容（一般为待渲染的登录 URL）。</summary>
    public string QrUrl { get; init; } = string.Empty;
}

/// <summary>二维码扫码状态（对照 NexBox OnlineQrCheckResult）。
/// 800=过期，801=等待扫码，802=已扫码待确认，803=登录成功（自动设置 Set-Cookie）。</summary>
public sealed record OnlineQrCheckResult
{
    /// <summary>平台状态码（800/801/802/803）。</summary>
    public int Code { get; init; }

    /// <summary>状态说明。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>登录成功（Code=803）。</summary>
    public bool Success => Code == 803;

    /// <summary>等待中（已扫码待确认或未扫码）。</summary>
    public bool Waiting => Code == 801 || Code == 802;

    /// <summary>二维码已过期（需重新获取 key）。</summary>
    public bool Expired => Code == 800;

    /// <summary>登录成功时捕获的 Cookie（调用方负责 DPAPI 加密存储）。</summary>
    public string? CapturedCookie { get; init; }

    /// <summary>登录成功/已扫码时返回的用户昵称（部分服务器形态在 802/803 才给）。</summary>
    public string? Nickname { get; init; }

    /// <summary>登录成功/已扫码时返回的头像 URL；空字符串表示服务器没下发。</summary>
    public string? Avatar { get; init; }
}

/// <summary>歌单元数据 + 全部 trackIds 结果（对照 NexBox playlist_info_with_track_ids）。</summary>
public sealed record OnlinePlaylistWithTrackIds
{
    /// <summary>歌单元数据（可能为 null：接口仅返回 id 列表时）。</summary>
    public OnlinePlaylist? Meta { get; init; }

    /// <summary>全部曲目 ID（按歌单顺序）。</summary>
    public List<string> TrackIds { get; init; } = [];

    /// <summary>曲目总数。</summary>
    public int TotalTracks => TrackIds.Count;
}
