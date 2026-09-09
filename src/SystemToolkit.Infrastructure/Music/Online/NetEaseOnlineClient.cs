using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Online;

namespace SystemToolkit.Infrastructure.Music.Online;

/// <summary>
/// 网易云音乐 API 客户端（A 方案：打包固定 EAPI 密钥）。
/// 移植自 NexBox src-tauri/src/music_api/netease.rs。
/// 仅实现搜索 / 歌曲链接 / 歌词三个核心功能（用户 R1 拍板）。
/// </summary>
public sealed class NetEaseOnlineClient : IOnlineMusicClient, INetEaseOnlineApi, IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Linux; Android 9; PCT-AL10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.64 HuaweiBrowser/10.0.3.311 Mobile Safari/537.36";
    private const string EapiBase = "https://interface3.music.163.com/eapi";

    /// <summary>与 NetEaseCrypto.RustAlignedJsonOptions 同语义：序列化时对 " ' & 等不做 \uXXXX 转义，
    /// 生成的 payloadText 与 NexBox serde_json 逐字符一致（否则 MD5 签名错 → 服务器 400）。</summary>
    private static readonly JsonSerializerOptions RustAlignedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public OnlineProvider Provider => OnlineProvider.NetEase;

    public NetEaseOnlineClient(ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Add("Referer", "https://music.163.com/");
    }

    /// <summary>搜索歌曲。</summary>
    public async Task<List<OnlineTrack>> SearchAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://music.163.com/api/cloudsearch/get/web";
            string body = $"s={Uri.EscapeDataString(keywords)}&type=1&limit={limit}&offset=0";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("result", out JsonElement result) || !result.TryGetProperty("songs", out JsonElement songs))
            {
                _logger.Warn($"[NetEase] 搜索无结果: {keywords}");
                return [];
            }

            var list = new List<OnlineTrack>();
            foreach (JsonElement s in songs.EnumerateArray())
                list.Add(MapSongRecord(s));
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 搜索失败", e);
            return [];
        }
    }

    /// <summary>获取播放地址（带音质降级）。</summary>
    public async Task<OnlineSongUrlResult> GetSongUrlAsync(string id, string preferredQuality = "exhigh", string cookie = "", CancellationToken ct = default)
    {
        string[] candidates = GetQualityCandidates(preferredQuality);
        OnlineSongUrlResult? trialFallback = null;

        foreach (string q in candidates)
        {
            try
            {
                string idsJson = $"[\"{id}\"]";
                string url = "https://music.163.com/api/song/enhance/player/url/v1";
                string body = $"ids={idsJson}&level={q}&encodeType=flac&csrf_token=";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(cookie))
                    req.Headers.Add("Cookie", cookie);
                req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

                using HttpResponseMessage resp = await _http.SendAsync(req, ct);
                string text = await resp.Content.ReadAsStringAsync(ct);
                JsonElement json = JsonDocument.Parse(text).RootElement;

                if (!json.TryGetProperty("data", out JsonElement dataArr) || dataArr.GetArrayLength() == 0)
                    continue;
                JsonElement data = dataArr[0];

                string songUrl = data.GetStr("url", "");
                bool freeTrial = data.TryGetProperty("freeTrialInfo", out _);
                ulong br = data.GetULong("br");
                int fee = data.TryGetProperty("fee", out JsonElement feeEl) && feeEl.ValueKind == JsonValueKind.Number && feeEl.TryGetInt32(out int feeV) ? feeV : 0;

                if (!string.IsNullOrEmpty(songUrl) && !freeTrial)
                {
                    return new OnlineSongUrlResult
                    {
                        Url = songUrl,
                        Playable = true,
                        Trial = false,
                        Level = q,
                        Br = br,
                        Fee = fee,
                    };
                }

                if (!string.IsNullOrEmpty(songUrl) && freeTrial && trialFallback is null)
                {
                    trialFallback = new OnlineSongUrlResult
                    {
                        Url = songUrl,
                        Playable = true,
                        Trial = true,
                        Level = q,
                        Br = br,
                        Fee = fee,
                        Message = "仅试听片段",
                    };
                }
            }
            catch (Exception e)
            {
                // 审查 F-3 采纳（2026-09-09）：原为裸 catch——降级链每档失败都不可见，
                // 用户只看到"无法获取播放地址"，排查时无从区分网络/解析/接口变更。
                // 单档失败是预期内的降级，按 Warn 记（不中断后续档位尝试）。
                _logger.Warn($"[NetEase] 音质 {q} 取址失败，继续降级：{e.Message}");
            }
        }

        if (trialFallback is not null)
            return trialFallback;

        return new OnlineSongUrlResult
        {
            Playable = false,
            Reason = "url_unavailable",
            Message = "无法获取播放地址，可能需要登录或 VIP",
        };
    }

    /// <summary>获取歌词。</summary>
    public async Task<OnlineLyrics> GetLyricsAsync(string id, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://music.163.com/api/song/lyric/v1";
            // yv/ytv/yrv=1 → 返回 yrc 逐字歌词（对照 NexBox netease.rs；2026-09-09 逐字卡拉OK）
            string body = $"id={id}&cp=false&lv=0&kv=0&tv=0&rv=0&yv=1&ytv=1&yrv=1&csrf_token=";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            string lyric = json.TryGetProperty("lrc", out JsonElement lrc) && lrc.TryGetProperty("lyric", out JsonElement lyricEl)
                ? lyricEl.GetString() ?? "" : "";
            string? translation = json.TryGetProperty("tlyric", out JsonElement tl) && tl.TryGetProperty("lyric", out JsonElement tlEl)
                ? tlEl.GetString() : null;
            string? roma = json.TryGetProperty("romalrc", out JsonElement rl) && rl.TryGetProperty("lyric", out JsonElement rlEl)
                ? rlEl.GetString() : null;
            string? yrc = json.TryGetProperty("yrc", out JsonElement yc) && yc.TryGetProperty("lyric", out JsonElement ycEl)
                ? ycEl.GetString() : null;

            return new OnlineLyrics { Lyric = lyric, Translation = translation, Roma = roma, Yrc = yrc };
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取歌词失败", e);
            return new OnlineLyrics();
        }
    }

    private static OnlineTrack MapSongRecord(JsonElement s)
    {
        JsonElement ar = s.GetElement("ar");
        JsonElement artistsRaw = ar.ValueKind != JsonValueKind.Undefined ? ar : s.GetElement("artists");
        var artists = new List<string>();
        if (artistsRaw.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in artistsRaw.EnumerateArray())
            {
                string name = a.GetStr("name", "");
                if (!string.IsNullOrEmpty(name))
                    artists.Add(name);
            }
        }

        JsonElement al = s.GetElement("al");
        JsonElement album = al.ValueKind != JsonValueKind.Undefined ? al : s.GetElement("album");
        string id = s.TryGetProperty("id", out JsonElement idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "")
            : "";

        return new OnlineTrack
        {
            Provider = OnlineProvider.NetEase,
            Id = id,
            Name = s.GetStr("name", ""),
            Artist = string.Join(" / ", artists),
            Album = album.GetStr("name", album.GetStr("title", "")),
            Cover = album.GetStr("picUrl", album.GetStr("coverUrl", "")),
            DurationMs = (ulong)s.GetLong("dt", s.GetLong("duration")),
            Fee = s.GetInt("fee"),
            Playable = true,
        };
    }

    private static string[] GetQualityCandidates(string preferred)
    {
        string[] all = new[] { "jymaster", "hires", "lossless", "exhigh", "standard" };
        int idx = Array.IndexOf(all, preferred);
        if (idx < 0)
            idx = 3; // 默认 exhigh
        return all.Skip(idx).ToArray();
    }

    /// <summary>搜索歌手（type=100）。</summary>
    public async Task<List<OnlineArtist>> SearchArtistsAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://music.163.com/api/cloudsearch/get/web";
            string body = $"s={Uri.EscapeDataString(keywords)}&type=100&limit={limit}&offset=0";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("result", out JsonElement result) || !result.TryGetProperty("artists", out JsonElement artists))
            {
                _logger.Warn($"[NetEase] 搜索歌手无结果: {keywords}");
                return [];
            }

            var list = new List<OnlineArtist>();
            foreach (JsonElement a in artists.EnumerateArray())
            {
                string id = a.TryGetProperty("id", out JsonElement idEl)
                    ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "")
                    : "";
                list.Add(new OnlineArtist
                {
                    Id = id,
                    Name = a.GetStr("name", ""),
                    Avatar = a.GetStr("imgurl", a.GetStr("picUrl", "")),
                    SongCount = a.GetInt("musicSize"),
                    AlbumCount = a.GetInt("albumSize"),
                    Brief = a.GetStr("briefDesc", ""),
                });
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 搜索歌手失败", e);
            return [];
        }
    }

    /// <summary>加载歌手热门歌曲。</summary>
    public async Task<List<OnlineTrack>> LoadArtistSongsAsync(string artistId, int offset = 0, int limit = 50, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = $"https://music.163.com/api/artist/songs?id={artistId}&offset={offset}&limit={limit}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("songs", out JsonElement songs))
            {
                _logger.Warn($"[NetEase] 歌手 {artistId} 曲目为空");
                return [];
            }

            var list = new List<OnlineTrack>();
            foreach (JsonElement s in songs.EnumerateArray())
                list.Add(MapSongRecord(s));
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载歌手曲目失败", e);
            return [];
        }
    }

    /// <summary>搜索歌单（type=1000）。</summary>
    public async Task<List<OnlinePlaylist>> SearchPlaylistsAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://music.163.com/api/cloudsearch/get/web";
            string body = $"s={Uri.EscapeDataString(keywords)}&type=1000&limit={limit}&offset=0";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("result", out JsonElement result) || !result.TryGetProperty("playlists", out JsonElement playlists))
            {
                _logger.Warn($"[NetEase] 搜索歌单无结果: {keywords}");
                return [];
            }

            var list = new List<OnlinePlaylist>();
            foreach (JsonElement p in playlists.EnumerateArray())
                list.Add(MapPlaylistRecord(p));
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 搜索歌单失败", e);
            return [];
        }
    }

    /// <summary>
    /// D1.1：分页获取用户歌单（网易云接口支持 limit/offset；单页上限 300，默认一次拿 100）。
    /// <para>如果传了 <c>limit<=0</c> 表示"一直翻页直到全部取回"（最多翻 10 页防死循环）。</para>
    /// </summary>
    public async Task<(List<OnlinePlaylist> playlists, int total)> LoadUserPlaylistsPageAsync(string cookie = "", int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        try
        {
            string uid = TryExtractUidFromCookie(cookie);
            if (string.IsNullOrEmpty(uid))
            {
                OnlineLoginInfo loginInfo = await GetLoginStatusAsync(cookie, ct);
                if (!loginInfo.LoggedIn || string.IsNullOrEmpty(loginInfo.Uid))
                {
                    _logger.Warn("[NetEase] 加载用户歌单失败：未登录或无法获取 uid");
                    return ([], 0);
                }
                uid = loginInfo.Uid;
            }

            // 网易云 user/playlist 单页上限实测约 300，大于 300 按 300；0 视为全量拉取
            int effectiveLimit = limit <= 0 ? 100 : Math.Min(limit, 300);

            // D1.1：自动翻页模式（limit <= 0）—— NexBox 默认不截断"我的歌单"，一次性全量拉取
            if (limit <= 0)
            {
                var all = new List<OnlinePlaylist>();
                int total = -1;
                int curOffset = offset;
                int safetyPages = 10; // 防 API 异常（例如 total 永不衰减）触发无限请求
                do
                {
                    safetyPages--;
                    (List<OnlinePlaylist>? page, int t) = await FetchOne(uid, cookie, curOffset, effectiveLimit, ct);
                    if (total < 0)
                        total = t;
                    if (page.Count == 0)
                        break;
                    all.AddRange(page);
                    curOffset += page.Count;
                    if (t >= 0 && curOffset >= t)
                        break;
                } while (safetyPages > 0);
                return (all, total >= 0 ? total : all.Count);
            }

            (List<OnlinePlaylist>? one, int totalOne) = await FetchOne(uid, cookie, offset, effectiveLimit, ct);
            return (one, totalOne);
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载用户歌单失败", e);
            return ([], 0);
        }

        async Task<(List<OnlinePlaylist> page, int total)> FetchOne(string uid, string cookie, int off, int lim, CancellationToken cancellationToken)
        {
            string url = $"https://music.163.com/api/user/playlist?uid={Uri.EscapeDataString(uid)}&limit={lim}&offset={off}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken);
            string text = await resp.Content.ReadAsStringAsync(cancellationToken);
            JsonElement json = JsonDocument.Parse(text).RootElement;
            int total;
            {
                total = -1;
                if (json.TryGetProperty("total", out JsonElement totEl) && totEl.ValueKind == JsonValueKind.Number && totEl.TryGetInt32(out int totV))
                    total = totV;
            }
            if (!json.TryGetProperty("playlist", out JsonElement playlist))
            {
                _logger.Warn($"[NetEase] 用户 {uid} 歌单为空");
                return (page: (List<OnlinePlaylist>)[], total: total < 0 ? 0 : total);
            }
            var list = new List<OnlinePlaylist>();
            foreach (JsonElement p in playlist.EnumerateArray())
                list.Add(MapPlaylistRecord(p));
            return (page: list, total: total < 0 ? list.Count + off : total);
        }
    }

    // 保持向后兼容（旧签名：无 offset/limit → 默认全量拉取，跨重启 UI 不改也不截断）
    public async Task<List<OnlinePlaylist>> LoadUserPlaylistsAsync(string cookie = "", CancellationToken ct = default)
        => (await LoadUserPlaylistsPageAsync(cookie, 0, limit: 0, ct).ConfigureAwait(true)).playlists;


    /// <summary>加载歌单曲目。</summary>
    /// <summary>
    /// 加载歌单曲目（2026-09-03 修复：NexBox 用的是 <b>POST 表单</b> `/api/v6/playlist/detail`，
    /// 旧实现用 GET 带 offset/limit —— 该接口不接受 GET，返回体无 playlist，表现就是"歌单打不开"。
    /// 参数对照 NexBox：id / n（返回曲目数）/ s（订阅者数，取 0）/ csrf_token。
    /// </summary>
    public async Task<List<OnlineTrack>> LoadPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 100, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            int take = limit <= 0 ? 1000 : limit;
            JsonElement? json = await PostPlaylistDetailAsync(playlistId, n: offset + take, s: 0, cookie, ct);
            if (json is null)
                return [];

            if (!json.Value.TryGetProperty("playlist", out JsonElement playlist) || !playlist.TryGetProperty("tracks", out JsonElement tracks))
            {
                _logger.Warn($"[NetEase] 歌单 {playlistId} 曲目为空");
                return [];
            }

            var all = new List<OnlineTrack>();
            foreach (JsonElement s in tracks.EnumerateArray())
                all.Add(MapSongRecord(s));

            // 接口不支持 offset 参数，本地切片
            if (offset > 0)
                all = all.Skip(offset).ToList();
            if (take > 0 && all.Count > take)
                all = all.Take(take).ToList();
            return all;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载歌单曲目失败", e);
            return [];
        }
    }

    /// <summary>加载官方排行榜列表。</summary>
    public async Task<List<OnlinePlaylist>> LoadOfficialChartsAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://music.163.com/api/toplist";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("list", out JsonElement list))
            {
                _logger.Warn("[NetEase] 排行榜列表为空");
                return [];
            }

            var result = new List<OnlinePlaylist>();
            foreach (JsonElement c in list.EnumerateArray())
                result.Add(MapPlaylistRecord(c));
            return result;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载排行榜失败", e);
            return [];
        }
    }

    /// <summary>加载推荐歌单（个性化，固定 10 条）。</summary>
    public async Task<List<OnlinePlaylist>> LoadRecommendationsAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://music.163.com/api/personalized?limit=10";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("result", out JsonElement result))
            {
                _logger.Warn("[NetEase] 推荐歌单为空");
                return [];
            }

            var list = new List<OnlinePlaylist>();
            foreach (JsonElement p in result.EnumerateArray())
                list.Add(MapPlaylistRecord(p));
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载推荐歌单失败", e);
            return [];
        }
    }

    /// <summary>加载每日推荐歌曲（需要登录）。</summary>
    public async Task<List<OnlineTrack>> LoadDailyRecommendSongsAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(cookie))
            {
                _logger.Warn("[NetEase] 加载每日推荐失败：cookie 为空（需要登录）");
                return [];
            }

            // 2026-09-04 对齐 NexBox（netease.rs:938-952）：/api/v3/discovery/recommend/songs（旧 /api/recommend/songs 已废弃）
            string url = "https://music.163.com/api/v3/discovery/recommend/songs";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("data", out JsonElement data) || !data.TryGetProperty("dailySongs", out JsonElement songs))
            {
                _logger.Warn("[NetEase] 每日推荐为空（可能未登录）");
                return [];
            }

            var list = new List<OnlineTrack>();
            foreach (JsonElement s in songs.EnumerateArray())
                list.Add(MapSongRecord(s));
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载每日推荐失败", e);
            return [];
        }
    }

    /// <summary>
    /// 喜欢/取消喜欢歌曲（需要登录）。
    /// 2026-09-04 对齐 NexBox like()（netease.rs:820-834）：POST 表单 /api/song/like（trackId/like/csrf_token），
    /// 旧 PUT /api/like?trackid= 形态接口不再响应。
    /// </summary>
    public async Task<bool> ToggleLikeAsync(string songId, bool like, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(cookie))
            {
                _logger.Warn("[NetEase] 喜欢操作失败：cookie 为空（需要登录）");
                return false;
            }

            JsonElement? json = await PostFormAsync("https://music.163.com/api/song/like", new[]
            {
                new KeyValuePair<string, string>("trackId", songId),
                new KeyValuePair<string, string>("like", like ? "true" : "false"),
                new KeyValuePair<string, string>("csrf_token", ExtractCsrfToken(cookie)),
            }, cookie, ct);
            if (json is null)
                return false;

            int code = json.Value.TryGetProperty("code", out JsonElement codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int cV) ? cV : 0;
            if (code == 200)
            {
                _logger.Info($"[NetEase] 喜欢操作成功: track={songId} like={like}");
                return true;
            }
            _logger.Warn($"[NetEase] 喜欢操作失败: code={code} track={songId}");
            return false;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 喜欢操作异常", e);
            return false;
        }
    }

    /// <summary>
    /// 加载喜欢列表（需要登录）。
    /// 2026-09-04 对齐 NexBox likelist()（netease.rs:752-817）：不再走旧 /api/likelist 接口，
    /// 改「用户歌单找 specialType==5（我喜欢的音乐）→ v6 playlist/detail 取 trackIds → song/detail 补全」。
    /// </summary>
    public async Task<List<OnlineTrack>> LoadLikedListAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(cookie))
            {
                _logger.Warn("[NetEase] 加载喜欢列表失败：cookie 为空（需要登录）");
                return [];
            }

            string uid = TryExtractUidFromCookie(cookie);
            if (string.IsNullOrEmpty(uid))
            {
                OnlineLoginInfo loginInfo = await GetLoginStatusAsync(cookie, ct);
                if (!loginInfo.LoggedIn || string.IsNullOrEmpty(loginInfo.Uid))
                {
                    _logger.Warn("[NetEase] 加载喜欢列表失败：未登录或无法获取 uid");
                    return [];
                }
                uid = loginInfo.Uid;
            }

            // ① 用户歌单找「我喜欢的音乐」（specialType==5 优先 → 名称含"喜欢" → id==uid 兜底）
            JsonElement userPlJson = await GetApiJsonAsync(
                $"https://music.163.com/api/user/playlist?uid={Uri.EscapeDataString(uid)}&limit=200&offset=0", cookie, ct);
            string likedId = uid; // 兜底：网易云"我喜欢的音乐"歌单 id 历史上等于 uid
            if (userPlJson.TryGetProperty("playlist", out JsonElement pls) && pls.ValueKind == JsonValueKind.Array)
            {
                string? byName = null, byUid = null;
                foreach (JsonElement pl in pls.EnumerateArray())
                {
                    long special = pl.TryGetProperty("specialType", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.Number && stEl.TryGetInt64(out long sV) ? sV : 0;
                    string plId = pl.TryGetProperty("id", out JsonElement idEl2) && idEl2.ValueKind == JsonValueKind.Number ? idEl2.GetInt64().ToString() : "";
                    if (special == 5 && !string.IsNullOrEmpty(plId))
                    { likedId = plId; break; }
                    if (byName is null && pl.TryGetProperty("name", out JsonElement nEl) && nEl.GetString()?.Contains("喜欢") == true)
                        byName = plId;
                    if (byUid is null && plId == uid)
                        byUid = plId;
                }
                if (byName is not null)
                    likedId = byName;
                else if (byUid is not null)
                    likedId = byUid;
            }
            _logger.Info($"[NetEase] liked playlist id: {likedId}");

            // ② 歌单详情取 trackIds（n=0 只取元数据）
            List<string> ids = await FetchPlaylistTrackIdsAsync(likedId, cookie, ct);
            _logger.Info($"[NetEase] likelist loaded {ids.Count} songs from playlist");
            if (ids.Count == 0)
                return [];

            // 上限 200，避免 song/detail 单次请求过大
            if (ids.Count > 200)
                ids = ids.Take(200).ToList();

            return await LoadSongsDetailAsync(ids, cookie, ct);
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载喜欢列表失败", e);
            return [];
        }
    }

    /// <summary>明文 GET JSON（NexBox get_api 形态：直接带 Cookie 请求 /api/*）。</summary>
    private async Task<JsonElement> GetApiJsonAsync(string url, string cookie, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(cookie))
            req.Headers.Add("Cookie", cookie);
        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// 心动模式：基于当前歌曲 id 拉取口味相似的歌曲。
    /// 2026-09-04 对齐 NexBox simi_song（netease.rs:1009-1031）：该接口要求 <b>WEAPI 签名</b>，
    /// 明文 GET /api/v1/discovery/simiSong 拿不到数据——切 PostWeapiAsync（payload songid/limit/offset）。
    /// </summary>
    public async Task<List<OnlineTrack>> GetSimiSongsAsync(string songId, int limit = 50, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(songId))
            {
                _logger.Warn("[NetEase] 心动模式失败：songId 为空");
                return [];
            }

            JsonElement json = await PostWeapiAsync("/api/v1/discovery/simiSong",
                new { songid = songId, limit, offset = 0 }, cookie, ct);

            int code = json.TryGetProperty("code", out JsonElement codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int cV) ? cV : -1;
            if (code != 200)
            {
                _logger.Warn($"[NetEase] 心动模式 code={code}: songId={songId}");
                return [];
            }

            if (!json.TryGetProperty("songs", out JsonElement songsEl) || songsEl.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn($"[NetEase] 心动模式为空: songId={songId}");
                return [];
            }

            var list = new List<OnlineTrack>();
            foreach (JsonElement s in songsEl.EnumerateArray())
                list.Add(MapSongRecord(s));
            _logger.Info($"[NetEase] 心动模式返回 {list.Count} 首（基曲={songId}）");
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 心动模式失败", e);
            return [];
        }
    }

    /// <summary>加载专辑曲目。</summary>
    public async Task<List<OnlineTrack>> LoadAlbumTracksAsync(string albumId, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = $"https://music.163.com/api/album?id={albumId}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            // 兼容 songs 在顶层或在 album.songs 内两种返回结构
            JsonElement songs;
            if (json.TryGetProperty("songs", out JsonElement songsTop))
                songs = songsTop;
            else if (json.TryGetProperty("album", out JsonElement album) && album.TryGetProperty("songs", out JsonElement songsInAlbum))
                songs = songsInAlbum;
            else
            {
                _logger.Warn($"[NetEase] 专辑 {albumId} 曲目为空");
                return [];
            }

            var list = new List<OnlineTrack>();
            if (songs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in songs.EnumerateArray())
                    list.Add(MapSongRecord(s));
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载专辑曲目失败", e);
            return [];
        }
    }

    /// <summary>
    /// 加载歌曲评论（分页，每页 20 条）。
    /// 2026-09-04 对齐 NexBox（netease.rs:1246-1308）：该接口要求 WEAPI 签名，明文 GET 拿不到数据。
    /// </summary>
    public async Task<OnlineCommentPage> LoadCommentsAsync(string songId, int page = 1, string cookie = "", CancellationToken ct = default)
    {
        if (page < 1)
            page = 1;
        try
        {
            int offset = (page - 1) * 20;
            JsonElement json = await PostWeapiAsync($"/api/v1/resource/comments/R_SO_4_{songId}",
                new { rid = songId, offset, limit = 20 }, cookie, ct);

            int total = json.TryGetProperty("total", out JsonElement totalEl) && totalEl.ValueKind == JsonValueKind.Number && totalEl.TryGetInt32(out int totV2) ? totV2 : 0;
            bool hasMore = json.TryGetProperty("hasMore", out JsonElement hmEl) && hmEl.ValueKind == JsonValueKind.True;
            JsonElement commentsEl = json.TryGetProperty("comments", out JsonElement ce) && ce.ValueKind == JsonValueKind.Array
                ? ce : default;

            var list = new List<OnlineComment>();
            if (commentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in commentsEl.EnumerateArray())
                {
                    JsonElement user = c.GetElement("user");
                    string userId = user.TryGetProperty("userId", out JsonElement uIdEl)
                        ? (uIdEl.ValueKind == JsonValueKind.Number ? uIdEl.GetInt64().ToString() : uIdEl.GetString() ?? "")
                        : "";
                    list.Add(new OnlineComment
                    {
                        UserId = userId,
                        UserName = user.GetStr("nickname", ""),
                        Avatar = user.GetStr("avatarUrl", ""),
                        Content = c.GetStr("content", ""),
                        Timestamp = c.GetLong("time"),
                        LikedCount = c.GetInt("likedCount"),
                    });
                }
            }

            return new OnlineCommentPage
            {
                Total = total,
                Page = page,
                HasMore = hasMore,
                Comments = list,
            };
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 加载评论失败", e);
            return new OnlineCommentPage { Page = page };
        }
    }

    /// <summary>获取登录状态（从 cookie 验证）。</summary>
    /// <summary>
    /// 网易云登录态判定（对照 NexBox login_status，2026-09-03 重写）。
    /// 旧实现用 `/api/nuser/for/nn/get`（已下线接口，account/profile 常为 null），
    /// 导致登录后 uid 取不到 → 用户歌单直接返回空。改为：
    /// ① Cookie 含 MUSIC_U 才算可能有登录态；② GET `/api/w/nuser/account/get`；
    /// ③ profile 取 data.profile → profile → data.account.profile → account.profile；
    /// ④ userId 必须存在才算登录。
    /// </summary>
    public async Task<OnlineLoginInfo> GetLoginStatusAsync(string cookie = "", CancellationToken ct = default)
    {
        var notLogged = new OnlineLoginInfo { Provider = OnlineProvider.NetEase, LoggedIn = false };
        try
        {
            if (string.IsNullOrEmpty(cookie) || !NeteaseCookieHasLogin(cookie))
                return notLogged;

            const string url = "https://music.163.com/api/w/nuser/account/get";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", cookie);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            int code = json.TryGetProperty("code", out JsonElement codeEl) && codeEl.TryGetInt32(out int c) ? c : -1;
            _logger.Info($"[NetEase] login_status code={code}");

            // data = result.data ?? result；profile 候选链（Mineradio 口径）
            JsonElement data = json.TryGetProperty("data", out JsonElement dataEl) ? dataEl : json;
            JsonElement profile = default;
            bool found = TryPick(data, out profile, "profile")
                || TryPick(json, out profile, "profile")
                || (data.TryGetProperty("account", out JsonElement dAcc) && TryPick(dAcc, out profile, "profile"))
                || (json.TryGetProperty("account", out JsonElement rAcc) && TryPick(rAcc, out profile, "profile"));

            if (!found || profile.ValueKind != JsonValueKind.Object)
            {
                _logger.Warn($"[NetEase] login_status: profile 为空（code={code}）");
                return notLogged;
            }

            string uid = FirstNumber(profile, "userId", "user_id", "id");
            if (string.IsNullOrEmpty(uid))
            {
                _logger.Warn("[NetEase] login_status: profile 无 userId，视为未登录");
                return notLogged;
            }

            int vipType = 0;
            if (profile.TryGetProperty("vipType", out JsonElement vipEl) && vipEl.TryGetInt32(out int vt))
                vipType = vt;

            string nickname = FirstStr(profile, "nickname", "userName");
            _logger.Info($"[NetEase] login_status success: uid={uid} nickname={nickname} vipType={vipType}");

            return new OnlineLoginInfo
            {
                Provider = OnlineProvider.NetEase,
                LoggedIn = true,
                Nickname = string.IsNullOrEmpty(nickname) ? "网易云用户" : nickname,
                AvatarUrl = FirstStr(profile, "avatarUrl", "avatar"),
                Uid = uid,
                VipType = vipType,
            };
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取登录状态失败", e);
            return notLogged;
        }

        static bool TryPick(JsonElement root, out JsonElement value, string key)
        {
            value = default;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Object)
                return false;
            value = el;
            return true;
        }

        static string FirstStr(JsonElement e, params string[] keys)
        {
            foreach (string k in keys)
                if (e.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String)
                {
                    string s = v.GetString() ?? "";
                    if (s.Length > 0)
                        return s;
                }
            return "";
        }

        static string FirstNumber(JsonElement e, params string[] keys)
        {
            foreach (string k in keys)
            {
                if (!e.TryGetProperty(k, out JsonElement v))
                    continue;
                if (v.ValueKind == JsonValueKind.Number)
                    return v.GetInt64().ToString();
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long n))
                    return n.ToString();
            }
            return "";
        }
    }

    /// <summary>对照 NexBox netease_cookie_has_login：MUSIC_U 是网易云登录态核心标志。</summary>
    private static bool NeteaseCookieHasLogin(string cookie)
    {
        if (string.IsNullOrEmpty(cookie))
            return false;
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == "MUSIC_U" && !string.IsNullOrWhiteSpace(kv[1]))
                return true;
        }
        return false;
    }

    /// <summary>使用 cookie 登录（验证 cookie 有效性，返回是否已登录）。</summary>
    public async Task<bool> LoginWithCookieAsync(string cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            _logger.Warn("[NetEase] Cookie 登录失败：cookie 为空");
            return false;
        }

        OnlineLoginInfo info = await GetLoginStatusAsync(cookie, ct);
        if (info.LoggedIn)
        {
            _logger.Info($"[NetEase] Cookie 登录成功: uid={info.Uid} nickname={info.Nickname}");
            return true;
        }
        _logger.Warn("[NetEase] Cookie 登录失败：cookie 无效或已过期");
        return false;
    }

    /// <summary>获取歌手详情（G8.5：歌手详情面板数据源）。</summary>
    public async Task<OnlineArtistDetail?> GetArtistDetailAsync(string artistId, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = $"https://music.163.com/api/artist/detail?id={artistId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            JsonElement data;
            if (json.TryGetProperty("data", out JsonElement dEl) && dEl.TryGetProperty("artist", out JsonElement aEl))
                data = aEl;
            else if (json.TryGetProperty("artist", out JsonElement artistEl))
                data = artistEl;
            else
                return null;

            return new OnlineArtistDetail
            {
                Id = artistId,
                Name = data.GetStr("name", ""),
                Avatar = data.GetStr("img1v1IdUrl", data.GetStr("picUrl", data.GetStr("cover", ""))),
                Brief = data.GetStr("briefDesc", ""),
                SongCount = data.GetInt("musicSize", 0),
                AlbumCount = data.GetInt("albumSize", 0),
                FanCount = data.GetInt("fansGroupCount", 0) + data.GetInt("followedCnt", 0),
            };
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取歌手详情失败", e);
            return null;
        }
    }

    /// <summary>获取歌手专辑列表（G8.5：歌手详情面板专辑 Tab）。</summary>
    public async Task<List<OnlineAlbum>> GetArtistAlbumsAsync(string artistId, int offset = 0, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = $"https://music.163.com/api/artist/albums/{artistId}?limit={limit}&offset={offset}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("hotAlbums", out JsonElement albums))
                return [];

            var list = new List<OnlineAlbum>();
            foreach (JsonElement a in albums.EnumerateArray())
            {
                list.Add(new OnlineAlbum
                {
                    Provider = OnlineProvider.NetEase,
                    Id = a.GetStr("id", ""),
                    Name = a.GetStr("name", ""),
                    Artist = a.GetStr("artist", a.TryGetProperty("artist", out JsonElement ar) ? ar.GetStr("name", "") : ""),
                    Cover = a.GetStr("picUrl", ""),
                    PublishDate = a.GetStr("publishTime", ""),
                    SongCount = a.GetInt("size", 0),
                });
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取歌手专辑失败", e);
            return [];
        }
    }

    /// <summary>获取歌手 MV 列表（G8.5：歌手详情面板 MV Tab）。</summary>
    public async Task<List<OnlineMv>> GetArtistMvsAsync(string artistId, int offset = 0, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = $"https://music.163.com/api/artist/mvs?id={artistId}&limit={limit}&offset={offset}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("mvs", out JsonElement mvs))
                return [];

            var list = new List<OnlineMv>();
            foreach (JsonElement m in mvs.EnumerateArray())
            {
                string artist = m.TryGetProperty("artist", out JsonElement ar) ? ar.GetStr("name", "") :
                             m.TryGetProperty("artists", out JsonElement ars) && ars.GetArrayLength() > 0
                                 ? ars[0].GetStr("name", "") : "";
                list.Add(new OnlineMv
                {
                    Id = m.GetStr("id", ""),
                    Name = m.GetStr("name", ""),
                    Artist = artist,
                    Cover = m.GetStr("imgurl", m.GetStr("cover", "")),
                    DurationMs = (ulong)m.GetInt("duration", 0),
                    PlayCount = m.GetInt("playCount", 0),
                });
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取歌手 MV 失败", e);
            return [];
        }
    }

    /// <summary>订阅/取消订阅歌单（G8.5：歌单收藏按钮）。</summary>
    public async Task<bool> SubscribePlaylistAsync(string playlistId, bool subscribe, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            _logger.Warn("[NetEase] 订阅歌单失败：需要登录");
            return false;
        }
        try
        {
            int t = subscribe ? 1 : 0;
            string url = $"https://music.163.com/api/playlist/subscribe?t={t}&id={playlistId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;
            int code = json.GetInt("code", -1);
            return code == 200 || code == 0;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 订阅歌单失败", e);
            return false;
        }
    }

    /// <summary>D2.1：网易云新建歌单（/api/playlist/create；privacy 0=公开，10=私有）。成功返回 playlist Id。</summary>
    public async Task<string?> CreatePlaylistAsync(string name, bool privacy = false, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(cookie))
            return null;
        try
        {
            string body = $"name={Uri.EscapeDataString(name)}&privacy={(privacy ? 10 : 0)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/playlist/create")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Headers.Add("Referer", "https://music.163.com/");
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;
            if (json.GetInt("code", -1) is not (200 or 0))
                return null;
            if (json.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number)
                return idEl.GetInt64().ToString();
            if (json.TryGetProperty("playlist", out JsonElement plEl) && plEl.TryGetProperty("id", out JsonElement idEl2))
                return idEl2.ValueKind == JsonValueKind.Number ? idEl2.GetInt64().ToString() : idEl2.GetString();
            return null;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 新建歌单失败", e);
            return null;
        }
    }

    /// <summary>
    /// D2.1：网易云删除自建歌单（/api/playlist/remove，批量 ids=pid1,pid2）。
    /// 注意：网易云端对"系统默认歌单（如我喜欢）"通常返回失败，UI 层应按结果提示。
    /// </summary>
    public async Task<bool> DeletePlaylistAsync(string playlistId, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playlistId) || string.IsNullOrEmpty(cookie))
            return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/playlist/remove")
            {
                Content = new StringContent($"ids={Uri.EscapeDataString(playlistId)}", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Headers.Add("Referer", "https://music.163.com/");
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;
            int code = json.GetInt("code", -1);
            return code == 200 || code == 0 || json.TryGetProperty("message", out JsonElement msg) && msg.GetString() == "ok";
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 删除歌单失败", e);
            return false;
        }
    }


    /// <summary>获取 MV 播放地址（G8.5：MV 链接点击后播放）。</summary>
    public async Task<string?> GetMvUrlAsync(string mvId, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = $"https://music.163.com/api/mv/detail?id={mvId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            // 优先取 mpUrl（高清），降级取 data.url
            if (json.TryGetProperty("mpUrl", out JsonElement mpEl) && mpEl.ValueKind == JsonValueKind.String)
                return mpEl.GetString();
            if (json.TryGetProperty("data", out JsonElement dEl) && dEl.TryGetProperty("url", out JsonElement urlEl) && urlEl.ValueKind == JsonValueKind.String)
                return urlEl.GetString();
            // 尝试 bRs 分辨率列表
            if (json.TryGetProperty("bRs", out JsonElement brs) && brs.GetArrayLength() > 0)
            {
                JsonElement br = brs[0];
                if (br.TryGetProperty("brUrl", out JsonElement brUrl) && brUrl.ValueKind == JsonValueKind.String)
                    return brUrl.GetString();
            }
            return null;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取 MV URL 失败", e);
            return null;
        }
    }

    private static OnlinePlaylist MapPlaylistRecord(JsonElement p)
    {
        string id = p.TryGetProperty("id", out JsonElement idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "")
            : "";
        JsonElement creator = p.GetElement("creator");
        return new OnlinePlaylist
        {
            Provider = OnlineProvider.NetEase,
            Id = id,
            Name = p.GetStr("name", ""),
            Cover = p.GetStr("coverImgUrl", p.GetStr("picUrl", "")),
            TrackCount = (uint)p.GetLong("trackCount"),
            Creator = creator.GetStr("nickname", ""),
            Description = p.TryGetProperty("description", out JsonElement descEl) && descEl.ValueKind == JsonValueKind.String
                ? descEl.GetString() : null,
            PlayCount = p.GetInt("playCount"),
        };
    }

    private static string TryExtractUidFromCookie(string cookie)
    {
        if (string.IsNullOrEmpty(cookie))
            return "";
        // NetEase cookie 通常不含数字 uid（多为 MUSIC_U 鉴权 token），
        // 仅在部分场景下出现 uid / nmc_uid / __uid 这种键，命中即返回。
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;
            string key = kv[0].Trim();
            string value = kv[1].Trim();
            if ((key == "uid" || key == "nmc_uid" || key == "__uid") && long.TryParse(value, out _))
                return value;
        }
        return "";
    }

    private async Task<List<OnlineTrack>> LoadSongsDetailAsync(List<string> ids, string cookie, CancellationToken ct)
    {
        try
        {
            if (ids.Count == 0)
                return [];

            // c 参数格式：[{"id":123},{"id":456}]
            var cBuilder = new StringBuilder("[");
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                    cBuilder.Append(',');
                cBuilder.Append("{\"id\":").Append(ids[i]).Append('}');
            }
            cBuilder.Append(']');

            string url = "https://music.163.com/api/song/detail";
            string body = $"c={Uri.EscapeDataString(cBuilder.ToString())}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("songs", out JsonElement songs))
            {
                _logger.Warn("[NetEase] 歌曲详情为空");
                return [];
            }

            var list = new List<OnlineTrack>();
            foreach (JsonElement s in songs.EnumerateArray())
                list.Add(MapSongRecord(s));
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 批量获取歌曲详情失败", e);
            return [];
        }
    }

    // ════════════ 二维码登录（对照 NexBox login_qr_key / login_qr_create / login_qr_check 1:1）════════════

    /// <summary>
    /// NexBox `post_eapi`/`post_eapi_full` 的等价实现（netease.rs L73-153）：
    ///   URL = EAPI_BASE + api_path[4..]
    ///   full_payload = payload + { "header": serde_json::to_string(&amp;build_eapi_header()) }
    ///   form params = encrypt_eapi_payload(api_path, serde_json::to_string(full_payload))
    ///   Cookie  = header.as_cookie_str(); if !user_cookie.is_empty() { append user_cookie }
    ///   Headers: NETEASE_USER_AGENT + Referer: https://music.163.com/
    ///   Set-Cookie: 仅在 full=true 时解析返回供 CheckQr 803 抓 MUSIC_U
    /// </summary>
    private async Task<(JsonElement json, string capturedCookie, string rawText)> PostEapiAsync(
        string apiPath,
        IReadOnlyDictionary<string, JsonElement> payload,
        string userCookie,
        bool captureSetCookie,
        CancellationToken ct)
    {
        if (!apiPath.StartsWith("/api/", StringComparison.Ordinal))
            throw new ArgumentException($"EAPI apiPath 必须以 /api/ 开头，实测={apiPath}", nameof(apiPath));
        string url = EapiBase + apiPath.Substring(4); // "/api/login/qrcode/unikey" → "/login/qrcode/unikey"

        // 1. 同 NexBox：先把 build_eapi_header() 的 *Map* 序列化成 JSON 字符串（保留 buildver 数字 / versioncode 字符串），
        //    再以 "header": JSON_STRING 的形态塞进 full_payload。顺序与类型不 1:1 → EAPI MD5 错 → 服务器 -460。
        Dictionary<string, JsonElement> headerMap = NetEaseCrypto.BuildEapiHeaderMap();
        string headerJson = NetEaseCrypto.SerializeHeaderMapToJsonString(headerMap);

        var full = new Dictionary<string, object?>(payload.Count + 1);
        foreach (KeyValuePair<string, JsonElement> kv in payload)
        {
            full[kv.Key] = kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString(),
                JsonValueKind.Number when kv.Value.TryGetInt64(out long iv) => iv,
                JsonValueKind.Number when kv.Value.TryGetUInt64(out ulong uv) => uv,
                JsonValueKind.Number when kv.Value.TryGetDouble(out double dv) => dv,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Array => JsonSerializer.Serialize(kv.Value.EnumerateArray().Select(e => e.Clone())),
                JsonValueKind.Object => JsonSerializer.Serialize(kv.Value.Clone()),
                _ => kv.Value.GetRawText(),
            };
        }
        full["header"] = headerJson;

        // 2026-09-03 定位签名错误时的根因发现：
        // System.Text.Json 默认会把内嵌 header JSON 里的双引号转成 \u0022，
        // 而 Rust serde_json 的行为是 \"（反斜杠引号）。服务器端做 MD5 校验是逐字节的，
        // 因此 payloadText 中只要有 1 处 \u0022 vs \" 的差别（4+1 vs 2 字节），
        // digestSource 的输入就完全不同 → 服务器返回 code=400 参数错误。
        // 修法：full payload 的序列化也必须走 UnsafeRelaxedJsonEscaping。
        string payloadText = JsonSerializer.Serialize(full, RustAlignedJsonOptions);
        (string payloadText, string digestSource, string digest, string data, string encryptedHex) probe = NetEaseCrypto.ProbeEncryptEapiPayload(apiPath, payloadText);
        string encrypted = probe.encryptedHex;

        // ⚠️ 传输层必须与 NexBox reqwest 字节一致（服务器侧对 Content-Type 细节 + 顺序也风控）。
        // NexBox 发 form(&[(params, encrypted)])，对应：
        //   Content-Type: application/x-www-form-urlencoded   (不含 charset)
        // 而 .NET FormUrlEncodedContent 默认写 charset=utf-8；实测 400 时无法排除这个因子，
        // 这里显式 new StringContent + 去掉 charset，并且把 body 手动做 form 编码（只 params=xx）。
        string formBody = "params=" + Uri.EscapeDataString(encrypted);
        var content = new StringContent(formBody, Encoding.ASCII, "application/x-www-form-urlencoded");
        // .NET 的 mediaType ctor 可能仍带 charset；手动抹掉。
        content.Headers.ContentType!.CharSet = "";

        string headerCookie = NetEaseCrypto.SerializeHeaderMapToCookieString(headerMap);

        // NexBox netease.rs L98-L102:
        //   full_cookie = if user_cookie.is_empty() { header_cookie } else { format!("{header_cookie}; {user_cookie}") }
        // —— 之前我们 C# 端 else 分支写成 user_cookie; header_cookie 顺序反了！
        //    即使 payload 签名一致，Cookie 顺序不对服务器对 header_map 内 requestId / buildver
        //    做同步校验时也会报 400 参数错误（NexBox 正是靠同一对 requestId 把 payload header 与 Cookie 绑一起）。
        string mergedCookie;
        if (string.IsNullOrEmpty(userCookie))
            mergedCookie = headerCookie;
        else
            mergedCookie = $"{headerCookie}; {userCookie}";

        // 2026-09-03 服务器报 400 "参数错误"时的唯一有效排查：
        // 把签名前后逐字段打出来，让用户贴到日志里和 NexBox 跑同 requestId 时的输出对比。
        // 只在第一次请求时打印一次完整 payloadText（避免反复刷屏）；后续只打印 path+code。
        // payloadText 里包含 header JSON（含 buildver/requestId），两端同 requestId 就能精确比对。
        // 审查 Y16（2026-09-10）：不再打印 headerCookiePreview（cookie 头内容），只留 requestId 比对所需的签名摘要
        _logger.Info($"[NetEase] EAPI 请求签名({apiPath}): payloadText={probe.payloadText} digestSource={probe.digestSource} digest={probe.digest} dataLen={probe.data.Length} encryptedLen={encrypted.Length} userCookieEmpty={string.IsNullOrEmpty(userCookie)}");

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(mergedCookie))
            req.Headers.TryAddWithoutValidation("Cookie", mergedCookie);
        // NexBox netease.rs L30-L34 / L104-L109 显式写：
        //   User-Agent: NETEASE_USER_AGENT（安卓 HuaweiBrowser Mobile 形态）
        //   Referer: https://music.163.com/
        // —— reqwest 不会擅自加多余 header；若服务器对 UA 做风控，这是唯一允许的值。
        //    我们显式写在 HttpRequestMessage 上（不依赖 HttpClient 默认），避免未来改 http ctor 引入偏差。
        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Referrer = new Uri("https://music.163.com/");
        req.Content = content;

        using HttpResponseMessage resp = await _http.SendAsync(req, captureSetCookie ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseContentRead, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        string captured = "";
        if (captureSetCookie && resp.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            captured = string.Join("; ", cookies
                .Select(s => s.Split(';')[0].Trim())
                .Where(s => s.Length > 0));
        }

        JsonElement json = JsonDocument.Parse(text).RootElement.Clone();
        return (json, captured, text);
    }

    private static JsonElement MakeJsonInt(long v)
    {
        using var d = JsonDocument.Parse(v.ToString());
        return d.RootElement.Clone();
    }

    private static JsonElement MakeJsonStr(string s)
    {
        using var d = JsonDocument.Parse(JsonSerializer.Serialize(s));
        return d.RootElement.Clone();
    }

    /// <summary>二维码登录 Step 1：获取 unikey（返回 key 与二维码图片 qrurl）。</summary>
    public async Task<OnlineQrLoginResult?> GetQrKeyAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            // NexBox netease.rs L441-453:
            //   payload = { type: 1, csrf_token: "" }
            //   post_eapi("/api/login/qrcode/unikey", payload, cookie)
            //   result.get("unikey").and_then(|v| v.as_str())   ← 根节点直接取，不在 data 里
            var payload = new Dictionary<string, JsonElement>
            {
                ["type"] = MakeJsonInt(1),
                ["csrf_token"] = MakeJsonStr(""),
            };
            (JsonElement json, string _, string? raw) = await PostEapiAsync("/api/login/qrcode/unikey", payload, cookie, captureSetCookie: false, ct);

            // NexBox 是 result["unikey"] 直接拿；兼容保留下 data.unikey 也试一次。
            string? key = null;
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("unikey", out JsonElement uk) && uk.ValueKind == JsonValueKind.String)
                key = uk.GetString();
            else if (json.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("unikey", out JsonElement uk2) && uk2.ValueKind == JsonValueKind.String)
                key = uk2.GetString();

            if (!string.IsNullOrEmpty(key))
            {
                return new OnlineQrLoginResult
                {
                    Key = key,
                    QrUrl = $"https://music.163.com/login?codekey={key}",
                };
            }
            int code = json.GetInt("code", -1);
            string msg = json.GetStr("msg", "") ?? json.GetStr("message", "");
            _logger.Warn($"[NetEase] 获取二维码 key 失败（code={code} msg={msg} body={raw.Trim()}）");
            return null;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取二维码 key 失败", e);
            return null;
        }
    }

    /// <summary>二维码登录 Step 2：基于 key 生成二维码图片 base64 与可扫码 URL（返回结果含 key）。</summary>
    public async Task<OnlineQrLoginResult?> CreateQrAsync(string key, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        try
        {
            // 网易云新版本不需要独立 create 接口，直接从 key 派生 qrurl
            await Task.CompletedTask;
            return new OnlineQrLoginResult
            {
                Key = key,
                QrUrl = $"https://music.163.com/login?codekey={key}",
            };
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 生成二维码失败", e);
            return null;
        }
    }

    /// <summary>二维码登录 Step 3：轮询扫码状态（801 等待 / 802 已扫 / 803 成功并捕获 Set-Cookie）。</summary>
    public async Task<OnlineQrCheckResult> CheckQrAsync(string key, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            return new OnlineQrCheckResult { Code = 800, Message = "key 为空" };
        try
        {
            // NexBox netease.rs L463-516:
            //   payload = { key, csrf_token: "" }
            //   post_eapi_full("/api/login/qrcode/client/login", payload, cookie)
            //   code = result["code"].as_i64() as i32   (根节点取，兼容 801/802/803)
            //   803: Set-Cookie 响应头 → MUSIC_U / __csrf；若空则降级 result["cookie"]
            var payload = new Dictionary<string, JsonElement>
            {
                ["key"] = MakeJsonStr(key),
                ["csrf_token"] = MakeJsonStr(""),
            };
            (JsonElement json, string? capturedFromHeader, _) = await PostEapiAsync("/api/login/qrcode/client/login", payload, cookie, captureSetCookie: true, ct);

            int code = -1;
            if (json.TryGetProperty("code", out JsonElement codeEl))
            {
                if (codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int cv))
                    code = cv;
                else if (codeEl.ValueKind == JsonValueKind.String && int.TryParse(codeEl.GetString(), out int cv2))
                    code = cv2;
            }
            string message = json.GetStr("message", "");
            string nickname = json.GetStr("nickname", "");
            string avatar = json.GetStr("avatarUrl", "");
            if (string.IsNullOrEmpty(nickname) && json.TryGetProperty("profile", out JsonElement profile))
            {
                if (string.IsNullOrEmpty(nickname))
                    nickname = profile.GetStr("nickname", "");
                if (string.IsNullOrEmpty(avatar))
                    avatar = profile.GetStr("avatarUrl", "");
            }

            string? captured = null;
            if (code == 803)
            {
                if (!string.IsNullOrEmpty(capturedFromHeader))
                    captured = capturedFromHeader;
                else if (json.TryGetProperty("cookie", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String)
                    captured = cEl.GetString();
            }

            // 2026-09-03 修"扫码后无动作"的诊断信息：
            // 每次轮询都把 (code, message) 打到 Info，让用户贴日志就能区分
            //   "801 服务器根本没收到扫码确认" / "802 已经扫到但 UI 没走到 803" / "-1 Parse 失败" / "code=0 服务器返回错"
            // 审查 R2（2026-09-10）：captured 在 803 时是 MUSIC_U 会话令牌，严禁写进日志
            // （项目引导用户贴日志排错 → 明文令牌会泄露）。只记"是否捕获到"，不落值/原始响应体
            bool cookieCaptured = captured is not null;
            _logger.Info($"[NetEase] 轮询扫码: code={code} msg={message} nick={(string.IsNullOrEmpty(nickname) ? "(无)" : nickname)} avatarLen={(string.IsNullOrEmpty(avatar) ? 0 : avatar.Length)} cookieCaptured={cookieCaptured}");

            return new OnlineQrCheckResult
            {
                Code = code,
                Message = message,
                CapturedCookie = captured,
                Nickname = nickname,
                Avatar = avatar,
            };
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 检查二维码状态失败", e);
            return new OnlineQrCheckResult { Code = -1, Message = e.Message };
        }
    }

    // ════════════ 登出 ════════════

    /// <summary>退出登录（服务端失效 + UI 清空本地 Cookie）。</summary>
    public async Task<bool> LogoutAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://music.163.com/api/logout";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;
            int code = json.GetInt("code", -1);
            return code == 200 || code == 0;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 退出登录失败", e);
            return false;
        }
    }

    // ════════════ 发送评论 ════════════

    /// <summary>发表歌曲评论（对照 NexBox send_comment → weapi 加密 /v1/resource/comments/add）。</summary>
    public async Task<bool> SendCommentAsync(string songId, string content, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            _logger.Warn("[NetEase] 发送评论失败：需要登录");
            return false;
        }
        if (string.IsNullOrWhiteSpace(content))
            return false;
        try
        {
            // 2026-09-04 对齐 NexBox（netease.rs:1290-1308）：WEAPI /api/resource/comments/add（threadId/content）
            JsonElement json = await PostWeapiAsync("/api/resource/comments/add",
                new { threadId = $"R_SO_4_{songId}", content }, cookie, ct);
            int code = json.GetInt("code", -1);
            if (code == 200 || code == 0)
            {
                _logger.Info($"[NetEase] 评论发送成功: song={songId}");
                return true;
            }
            _logger.Warn($"[NetEase] 评论发送失败: code={code} song={songId}");
            return false;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 发送评论异常", e);
            return false;
        }
    }

    // ════════════ 歌单分页 + 全部 trackIds ════════════

    /// <summary>分页加载歌单曲目（对照 NexBox playlist_tracks_range）。</summary>
    /// <summary>
    /// 明文 POST 表单（对照 NexBox `post_api`）：/api/xxx 原样打到 https://music.163.com/api/xxx，
    /// 带 UA + Referer + Cookie，form-urlencoded。网易云 v6 详情类接口只认 POST。
    /// </summary>
    private async Task<JsonElement?> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> parameters, string cookie, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(cookie))
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Content = new FormUrlEncodedContent(parameters);
        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException e)
        {
            _logger.Warn($"[NetEase] 解析响应失败（{url}）：{e.Message}");
            return null;
        }
    }

    /// <summary>歌单详情（NexBox：POST /api/v6/playlist/detail，参数 id / n / s / csrf_token）。</summary>
    private async Task<JsonElement?> PostPlaylistDetailAsync(string playlistId, int n, int s, string cookie, CancellationToken ct)
    {
        KeyValuePair<string, string>[] param = new[]
        {
            new KeyValuePair<string, string>("id", playlistId),
            new KeyValuePair<string, string>("n", n.ToString()),
            new KeyValuePair<string, string>("s", s.ToString()),
            new KeyValuePair<string, string>("csrf_token", ExtractCsrfToken(cookie)),
        };
        JsonElement? json = await PostFormAsync("https://music.163.com/api/v6/playlist/detail", param, cookie, ct);
        if (json is null)
            return null;
        int code = json.Value.TryGetProperty("code", out JsonElement codeEl) && codeEl.TryGetInt32(out int c) ? c : -1;
        if (code != 200 && code != 0)
        {
            _logger.Warn($"[NetEase] playlist/detail id={playlistId} code={code}（非 200 视为失败）");
        }
        return json;
    }

    /// <summary>歌单全部 trackIds（n=0 只取元数据，供喜欢列表/分页切片使用；对照 NexBox playlist_info_with_track_ids）。</summary>
    private async Task<List<string>> FetchPlaylistTrackIdsAsync(string playlistId, string cookie, CancellationToken ct)
    {
        var ids = new List<string>();
        JsonElement? json = await PostPlaylistDetailAsync(playlistId, n: 0, s: 0, cookie, ct);
        if (json is null || !json.Value.TryGetProperty("playlist", out JsonElement playlist))
            return ids;
        if (!playlist.TryGetProperty("trackIds", out JsonElement trackIdsEl) || trackIdsEl.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (JsonElement t in trackIdsEl.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out long l))
                ids.Add(l.ToString());
            else if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out long l2))
                ids.Add(l2.ToString());
        }
        return ids;
    }

    /// <summary>从 Cookie 提取 __csrf（写操作与部分详情接口要求与 Cookie 中的 csrf 匹配）。</summary>
    private static string ExtractCsrfToken(string cookie)
    {
        if (string.IsNullOrEmpty(cookie))
            return "";
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=', 2);
            if (kv.Length == 2 && (kv[0].Trim() == "__csrf" || kv[0].Trim() == "csrf_token"))
                return kv[1].Trim();
        }
        return "";
    }

    /// <summary>
    /// 分页加载歌单曲目（2026-09-03 修复：改用 POST /api/v6/playlist/detail，本地切片）。
    /// </summary>
    public async Task<List<OnlineTrack>> LoadPlaylistTracksRangeAsync(string playlistId, int start, int count, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            JsonElement? json = await PostPlaylistDetailAsync(playlistId, n: start + count, s: 0, cookie, ct);
            if (json is null)
                return [];

            if (!json.Value.TryGetProperty("playlist", out JsonElement playlist) || !playlist.TryGetProperty("tracks", out JsonElement tracks))
            {
                _logger.Warn($"[NetEase] 歌单 {playlistId} 分页曲目为空 (start={start},count={count})");
                return [];
            }

            var all = new List<OnlineTrack>();
            foreach (JsonElement s in tracks.EnumerateArray())
                all.Add(MapSongRecord(s));
            if (start > 0)
                all = all.Skip(start).ToList();
            if (count > 0 && all.Count > count)
                all = all.Take(count).ToList();
            return all;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 分页加载歌单曲目失败", e);
            return [];
        }
    }

    /// <summary>获取歌单元数据 + 全部 trackIds（用于歌单内搜索 / 歌单全量预览）。</summary>
    public async Task<OnlinePlaylistWithTrackIds?> GetPlaylistInfoWithTrackIdsAsync(string playlistId, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            // 2026-09-03 修复：同样的 GET→POST 改造（NexBox post_api /api/v6/playlist/detail，n=0 只取元数据 + trackIds）
            JsonElement? detail = await PostPlaylistDetailAsync(playlistId, n: 0, s: 0, cookie, ct);
            if (detail is null)
                return null;
            JsonElement json = detail.Value;
            if (!json.TryGetProperty("playlist", out JsonElement playlist))
                return null;

            // 取 trackIds：优先 trackIds 字段（纯 ID 列表），回退 trackIds 结构化数组
            var ids = new List<string>();
            if (playlist.TryGetProperty("trackIds", out JsonElement trackIdsEl) && trackIdsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in trackIdsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Number)
                        ids.Add(t.GetInt64().ToString());
                    else if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("id", out JsonElement idEl))
                        ids.Add(idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "");
                }
            }

            OnlinePlaylist meta = MapPlaylistRecord(playlist);
            return new OnlinePlaylistWithTrackIds { Meta = meta, TrackIds = ids };
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 获取歌单详情+trackIds 失败", e);
            return null;
        }
    }

    /// <summary>批量获取歌曲详情（根据 ids 返回完整 OnlineTrack 列表，用于歌单内搜索后补全信息）。</summary>
    public async Task<List<OnlineTrack>> LoadSongsByIdsAsync(List<string> ids, string cookie = "", CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];
        // 单次限制 200
        var batch = ids.Take(200).ToList();
        try
        {
            var cBuilder = new StringBuilder("[");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0)
                    cBuilder.Append(',');
                cBuilder.Append("{\"id\":").Append(batch[i]).Append('}');
            }
            cBuilder.Append(']');
            string url = "https://music.163.com/api/song/detail";
            string body = $"c={Uri.EscapeDataString(cBuilder.ToString())}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(text).RootElement;

            if (!json.TryGetProperty("songs", out JsonElement songs))
                return [];
            var list = new List<OnlineTrack>();
            foreach (JsonElement s in songs.EnumerateArray())
                list.Add(MapSongRecord(s));
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[NetEase] 批量获取歌曲详情失败", e);
            return [];
        }
    }

    private static string ExtractCsrfFromCookie(string cookie)
    {
        if (string.IsNullOrEmpty(cookie))
            return "";
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;
            if (kv[0].Trim().Equals("__csrf", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return "";
    }

    /// <summary>网易云 WEAPI 桌面端 UA（对照 netease.rs:156）。</summary>
    private const string WeapiUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36";

    /// <summary>
    /// WEAPI 加密请求（对照 NexBox post_weapi）：/api/xxx → https://music.163.com/weapi/xxx，
    /// payload 注入 cookie 的 __csrf → WEAPI 双层 AES + 裸 RSA → form params/encSecKey。
    /// 【坑】点赞 / 评论 / 心动模式（simiSong）等服务端要求 weapi 签名，明文 /api 形态会被拒。
    /// </summary>
    private async Task<JsonElement> PostWeapiAsync(string apiPath, object payload, string cookie, CancellationToken ct)
    {
        string url = "https://music.163.com/weapi" + apiPath[4..];
        string csrf = ExtractCsrfFromCookie(cookie);
        var full = new Dictionary<string, object?> { ["csrf_token"] = csrf };
        JsonElement el = JsonSerializer.SerializeToElement(payload, RustAlignedJsonOptions);
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in el.EnumerateObject())
            {
                full[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number when p.Value.TryGetInt64(out long l) => l,
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }
        string payloadText = JsonSerializer.Serialize(full, RustAlignedJsonOptions);
        (string ps, string esk) = NetEaseCrypto.EncryptWeapiPayload(payloadText);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", WeapiUserAgent);
        req.Headers.Referrer = new Uri("https://music.163.com/");
        if (!string.IsNullOrEmpty(cookie))
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
        string formBody = "params=" + Uri.EscapeDataString(ps) + "&encSecKey=" + Uri.EscapeDataString(esk);
        var content = new StringContent(formBody, Encoding.ASCII, "application/x-www-form-urlencoded");
        content.Headers.ContentType!.CharSet = "";
        req.Content = content;

        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public void Dispose() => _http?.Dispose();
}

internal static class JsonElementExtensions
{
    public static string GetStr(this JsonElement el, string name, string defaultValue = "")
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? defaultValue
                 : prop.ValueKind == JsonValueKind.Number ? prop.GetRawText()
                 : defaultValue;
        }
        return defaultValue;
    }

    public static long GetLong(this JsonElement el, string name, long defaultValue = 0)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement prop))
        {
            return prop.ValueKind == JsonValueKind.Number ? prop.TryGetInt64(out long v) ? v : defaultValue : defaultValue;
        }
        return defaultValue;
    }

    public static ulong GetULong(this JsonElement el, string name, ulong defaultValue = 0)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement prop))
        {
            return prop.ValueKind == JsonValueKind.Number ? prop.TryGetUInt64(out ulong v) ? v : defaultValue : defaultValue;
        }
        return defaultValue;
    }

    public static int GetInt(this JsonElement el, string name, int defaultValue = 0)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement prop))
        {
            return prop.ValueKind == JsonValueKind.Number ? prop.TryGetInt32(out int v) ? v : defaultValue : defaultValue;
        }
        return defaultValue;
    }

    public static JsonElement GetElement(this JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement prop))
            return prop;
        return default;
    }
}
