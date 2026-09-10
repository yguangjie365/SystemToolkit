using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Online;

namespace SystemToolkit.Infrastructure.Music.Online;

/// <summary>
/// QQ 音乐 API 客户端。
/// 移植自 NexBox src-tauri/src/music_api/qqmusic.rs。
/// 仅实现搜索 / 歌曲链接 / 歌词三个核心功能（用户 R1 拍板）。
/// </summary>
public sealed class QQMusicOnlineClient : IOnlineMusicClient, IQqMusicOnlineApi, IDisposable
{
    private const string MusicuUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string LyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";
    /// <summary>歌单曲目旧版 CGI（对照 NexBox QQ_PLAYLIST_TRACKS_URL）——公开歌单免登录也能抓。</summary>
    private const string QqPlaylistTracksUrl = "https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg";
    /// <summary>歌单曲目接口的 Referer（NexBox 固定值，缺了会被判非法来源）。</summary>
    private const string QqPlaylistReferer = "https://y.qq.com/n/yqq/playlist";
    private const string HeadersUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string SearchUa = "QQMusic 14090508(android 12)";
    private const string Referer = "https://y.qq.com/";

    /// <summary>搜索专用 UA（对照 NexBox QQ_SEARCH_UA：musics.fcg 按移动端校验）。</summary>
    private const string QqSearchUa = "QQMusic 14090508(android 12)";

    /// <summary>搜索专用端点（对照 NexBox：musics.fcg?sign=，不是 musicu.fcg）。</summary>
    private const string QqSearchUrl = "https://u.y.qq.com/cgi-bin/musics.fcg";

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public OnlineProvider Provider => OnlineProvider.QQMusic;

    public QQMusicOnlineClient(ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /// <summary>搜索歌曲（通过 musicu.fcg 搜索接口）。</summary>
    /// <summary>
    /// 搜索歌曲。2026-09-10 对照 NexBox <c>full_song_search</c>（qqmusic.rs:821）重写。
    /// <para>
    /// 🔴 旧实现走 <c>musicu.fcg</c> + 顶层键 <c>music.search.SearchFReq</c>——该路径已失效，
    /// 实测在 QQ 平台点搜索恒返回空列表（"点了没结果"）。NexBox 现役实现：
    /// ① 端点 <c>musics.fcg?sign=</c>；② module/method =
    /// <c>music.search.SearchCgiService</c> / <c>DoSearchForQQMusicMobile</c>，顶层键 <c>req</c>；
    /// ③ comm 必须完整（ct 11 / cv 14090508 / tmeAppID qqmusic…）；
    /// ④ 签名入参是**整个请求体**（QqSearchSign 算法与 NexBox qq_search_sign 同构）；
    /// ⑤ 解析 <c>req.data.body.item_song</c>（每项先取 <c>track_info</c>），
    ///    兼容 <c>body.song.list</c> / <c>body.list</c>；命中名含 &lt;em&gt; 高亮标签需剥离。
    /// </para>
    /// </summary>
    public async Task<List<OnlineTrack>> SearchAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string kw = (keywords ?? string.Empty).Trim();
            if (kw.Length == 0)
            {
                return [];
            }

            int numPerPage = Math.Clamp(limit, 1, 30);
            string searchId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                + Random.Shared.Next(0, 1000000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

            var payload = new
            {
                comm = new
                {
                    ct = "11",
                    cv = "14090508",
                    v = "14090508",
                    tmeAppID = "qqmusic",
                    phonetype = "EBG-AN10",
                    os_ver = "12",
                    QIMEI36 = "0",
                    uid = "0",
                    modeSwitch = "6",
                    ui_mode = "2",
                    nettype = "1020",
                },
                req = new
                {
                    module = "music.search.SearchCgiService",
                    method = "DoSearchForQQMusicMobile",
                    param = new
                    {
                        search_type = 0,
                        searchid = searchId,
                        query = kw,
                        page_num = 1,
                        num_per_page = numPerPage,
                        highlight = 0,
                        nqc_flag = 0,
                        multi_zhida = 0,
                        cat = 2,
                        grp = 1,
                        sin = 0,
                        sem = 0,
                    },
                },
            };

            string body = JsonSerializer.Serialize(payload);
            string sign = QqSearchSign(body);
            string url = QqSearchUrl + "?sign=" + Uri.EscapeDataString(sign);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("User-Agent", QqSearchUa);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(ParseJsonp(text)).RootElement;

            var list = new List<OnlineTrack>();
            if (!json.TryGetProperty("req", out JsonElement reqNode))
            {
                _logger.Warn("[QQMusic] 搜索响应无 req 节点：" + Shorten(text));
                return list;
            }

            JsonElement data = reqNode.TryGetProperty("data", out JsonElement d) ? d : reqNode;
            JsonElement bodyEl = data.TryGetProperty("body", out JsonElement b) ? b : data;

            JsonElement items = default;
            if (bodyEl.TryGetProperty("item_song", out JsonElement itemSong) && itemSong.ValueKind == JsonValueKind.Array)
            {
                items = itemSong;
            }
            else if (bodyEl.TryGetProperty("song", out JsonElement song)
                     && song.TryGetProperty("list", out JsonElement songList) && songList.ValueKind == JsonValueKind.Array)
            {
                items = songList;
            }
            else if (bodyEl.TryGetProperty("list", out JsonElement plainList) && plainList.ValueKind == JsonValueKind.Array)
            {
                items = plainList;
            }

            if (items.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn("[QQMusic] 搜索响应无歌曲列表：" + Shorten(text));
                return list;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                // item_song 的元素包一层 track_info（NexBox 同处理）
                JsonElement track = item.TryGetProperty("track_info", out JsonElement ti) ? ti : item;
                OnlineTrack mapped = MapQqTrack(track);
                // NexBox 过滤口径：名称非空且（mid 或 id 任一有值）；只认 mid 会误杀部分条目
                if (string.IsNullOrEmpty(mapped.Name)
                    || (string.IsNullOrEmpty(mapped.Mid) && string.IsNullOrEmpty(mapped.Id)))
                {
                    continue;
                }

                list.Add(mapped with { Name = StripHighlightTags(mapped.Name) });
            }

            _logger.Info($"[QQMusic] 搜索「{kw}」命中 {list.Count} 首");
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 搜索失败", e);
            return [];
        }
    }

    /// <summary>剥掉搜索结果里的高亮标签（QQ 返回 &lt;em&gt;周杰伦&lt;/em&gt; 形态，NexBox strip_html_tags 同义）。</summary>
    private static string StripHighlightTags(string name)
        => System.Text.RegularExpressions.Regex.Replace(name, "<[^>]+>", string.Empty);

    /// <summary>日志用：截断长响应，避免刷屏。</summary>
    private static string Shorten(string text)
        => text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>获取播放地址（通过 vkey 请求）。</summary>
    /// <remarks>
    /// 2026-09-09 对照 NexBox song_url 重写（此前"无 data"恒失败，歌单能看不能播）：
    /// ① module 必须是 <c>vkey.GetVkeyServer</c>——旧实现写 <c>music.vkey.GetVkeyServer</c>，
    ///    该模块名不存在（与 PlaylistBaseRead 同类坑），musicu 对未知模块恒不回 data；
    /// ② param 必须带 filename 候选（质量模板前缀 + mediaMid/songmid + 扩展名），否则空 purl；
    /// ③ comm 带 uin（cookie 提取，缺省 0），有凭据时作 authst（ct=19）。
    /// 2026-09-09b：filename 候选从请求音质起降级（对齐 NexBox normalize_quality 起点），
    /// 无绿钻账号带 RS01/F000 等高音质候选可能污染整个响应。
    /// </remarks>
    public async Task<OnlineSongUrlResult> GetSongUrlAsync(string songMid, string? mediaMid = null, string preferredQuality = "standard", string cookie = "", CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(songMid))
            {
                return Fail("songMid 为空");
            }

            string uin = ExtractUin(cookie);
            if (string.IsNullOrEmpty(uin))
            {
                uin = "0";
            }

            // authst（对照 NexBox cookie.rs qq_extract_music_key 九键候选链）：
            // qm_keyst 为标准播放密钥；缺失时按链降级（扫码登录可能只带回 skey/p_skey 等）
            string authSt = ExtractCookieValueChain(cookie, "qm_keyst", "qqmusic_key", "music_key",
                "p_skey", "skey", "psrf_qqaccess_token", "psrf_qqrefresh_token", "wxrefresh_token", "wxskey");

            // 质量模板（对照 NexBox QQ_QUALITY_TEMPLATES；mediaMid 缺省用 songMid 兜底——NexBox 同策略）。
            // 从请求音质所在档起降级尝试（与 NexBox templates = &TEMPLATES[quality_start..] 一致）
            string mediaId = !string.IsNullOrWhiteSpace(mediaMid) ? mediaMid : songMid;
            (string Prefix, string Ext, string Level)[] templates =
            [
                ("RS01", ".flac", "hires"), ("F000", ".flac", "lossless"),
                ("M800", ".mp3", "exhigh"), ("M500", ".mp3", "standard"), ("C400", ".m4a", "aac"),
            ];
            int start = Array.FindIndex(templates, t => t.Level == NormalizeQqQuality(preferredQuality));
            if (start < 0)
            {
                start = templates.Length - 2; // standard（M500）兜底
            }

            var filenames = templates[start..]
                .Select(t => $"{t.Prefix}{mediaId}{t.Ext}")
                .ToList();

            object comm = string.IsNullOrEmpty(authSt)
                ? new { uin, format = "json", ct = 24, cv = 0 }
                : new { uin, format = "json", ct = 19, cv = 0, authst = authSt };

            string guid = Random.Shared.Next(10_000_000, 99_999_999).ToString();
            var payload = new
            {
                comm,
                req_0 = new
                {
                    module = "vkey.GetVkeyServer", // 🔴 对照 NexBox：不是 music.vkey.GetVkeyServer
                    method = "CgiGetVkey",
                    param = new
                    {
                        guid,
                        songmid = new[] { songMid },
                        songtype = new[] { 0 },
                        uin,
                        loginflag = 1,
                        platform = "20",
                        filename = filenames,
                    },
                },
            };

            string body = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, MusicuUrl);
            req.Headers.Add("Referer", Referer);
            req.Headers.Add("User-Agent", HeadersUa);
            if (!string.IsNullOrEmpty(cookie))
            {
                req.Headers.Add("Cookie", cookie);
            }
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(ParseJsonp(text)).RootElement;

            if (!json.TryGetProperty("req_0", out JsonElement req0))
            {
                return Fail("无 req_0");
            }
            if (!req0.TryGetProperty("data", out JsonElement data))
            {
                string? code = req0.TryGetProperty("code", out JsonElement cEl) ? cEl.ToString() : null;
                return Fail($"无 data（code={code}）");
            }

            JsonElement midurlinfo = data.TryGetProperty("midurlinfo", out JsonElement mu) ? mu : default;
            if (midurlinfo.ValueKind != JsonValueKind.Array || midurlinfo.GetArrayLength() == 0)
            {
                return Fail("无 midurlinfo");
            }

            // 依次取第一个非空 purl（对应 filename 候选序 = 质量从高到低）
            string sip = data.TryGetProperty("sip", out JsonElement sipArr) && sipArr.ValueKind == JsonValueKind.Array && sipArr.GetArrayLength() > 0
                ? sipArr[0].GetString() ?? "https://ws.stream.qqmusic.qq.com/"
                : "https://ws.stream.qqmusic.qq.com/";

            int emptyPurl = 0;
            foreach (JsonElement info in midurlinfo.EnumerateArray())
            {
                string purl = info.GetStr("purl", "");
                if (string.IsNullOrEmpty(purl))
                {
                    emptyPurl++;
                    continue;
                }

                return new OnlineSongUrlResult
                {
                    Url = sip + purl,
                    Playable = true,
                    Trial = false,
                    Level = "standard",
                    Quality = "标准",
                    Br = info.GetULong("br"),
                };
            }

            // 📋 诊断留痕：purl 全空 = 未登录（无 qm_keyst）/ VIP 版权限制；登录态可见与否直接看日志
            _logger.Warn($"[QQMusic] purl 全空（{emptyPurl}/{midurlinfo.GetArrayLength()}）mid={songMid} " +
                         $"uin={uin} authst={(string.IsNullOrEmpty(authSt) ? "无" : "有")}——多为未登录或 VIP 版权限制");
            return new OnlineSongUrlResult { Playable = false, Reason = "url_unavailable", Message = "该歌曲可能需要 VIP 或登录" };
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取播放地址失败", e);
            return Fail(e.Message);
        }
    }

    /// <summary>QQ 音质名归一（对照 NexBox normalize_quality；未知回退 standard）。</summary>
    private static string NormalizeQqQuality(string quality) => quality?.Trim().ToLowerInvariant() switch
    {
        "hires" or "jymaster" => "hires",
        "lossless" => "lossless",
        "exhigh" => "exhigh",
        "aac" => "aac",
        _ => "standard",
    };

    /// <summary>安全取字符串属性：值非 String 类型（QQ 会给 0 数字哨兵）返回空串不抛异常。</summary>
    private static string SafeStr(JsonElement e, string key)
        => e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>从 Cookie 串按候选键链提取第一个非空值（不分大小写；全缺返回空串）。</summary>
    private static string ExtractCookieValueChain(string cookie, params string[] keys)
    {
        foreach (string key in keys)
        {
            string v = ExtractCookieValue(cookie, key);
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }
        }
        return "";
    }

    /// <summary>从 Cookie 串提取指定键的值（不分大小写；无则空串）。</summary>
    private static string ExtractCookieValue(string cookie, string key)
    {
        foreach (string part in (cookie ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim();
            }
        }
        return "";
    }

    /// <summary>获取歌词。</summary>
    public async Task<OnlineLyrics> GetLyricsAsync(string songMid, string cookie = "", CancellationToken ct = default)
    {
        // 优先 musicu GetPlayLyricInfo（含 qrc 逐字歌词，对照 NexBox handleQQLyric；2026-09-09 逐字卡拉OK）
        try
        {
            string payloadJson =
                "{\"comm\":{\"ct\":24,\"cv\":0}," +
                "\"lyric\":{\"module\":\"music.musichallSong.PlayLyricInfo\"," +
                "\"method\":\"GetPlayLyricInfo\"," +
                "\"param\":{\"songMID\":\"" + songMid.Replace("\"", "") + "\"}}}";
            JsonElement musicuJson = await PostMusicuAsync(payloadJson, cookie, ct);
            JsonElement musicuData = musicuJson.TryGetProperty("lyric", out JsonElement lyricProp)
                && lyricProp.TryGetProperty("data", out JsonElement dataProp)
                    ? dataProp
                    : default;
            if (musicuData.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // 🔴 字段值必须是字符串才 GetString——QQ 会给 "trans":0/"qrc":0 数字哨兵值，
                // 直接 GetString 抛 InvalidOperationException → 每首歌词都降级旧接口（2026-09-09 日志实证）
                string musicuLyric = DecodeBase64(SafeStr(musicuData, "lyric"));
                string? musicuTrans = musicuData.TryGetProperty("trans", out JsonElement mt) && mt.ValueKind == JsonValueKind.String
                    ? DecodeBase64(mt.GetString() ?? "")
                    : null;
                string? qrc = musicuData.TryGetProperty("qrc", out JsonElement qEl) && qEl.ValueKind == JsonValueKind.String
                    ? DecodeBase64(qEl.GetString() ?? "")
                    : null;
                if (!string.IsNullOrEmpty(musicuLyric) || !string.IsNullOrEmpty(qrc))
                {
                    return new OnlineLyrics
                    {
                        Lyric = musicuLyric,
                        Translation = string.IsNullOrEmpty(musicuTrans) ? null : musicuTrans,
                        Yrc = string.IsNullOrEmpty(qrc) ? null : qrc,
                    };
                }
            }
        }
        catch (Exception musicuEx)
        {
            // 降级到旧版接口（🔴 降级路径可见）
            _logger.Warn($"[QQMusic] musicu 歌词获取失败，降级旧版接口：{musicuEx.Message}");
        }

        // 旧版 yqq 歌词接口（无 qrc）
        try
        {
            string qs = $"?songmid={Uri.EscapeDataString(songMid)}&pcachetime={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0&format=json";
            using var req = new HttpRequestMessage(HttpMethod.Get, LyricUrl + qs);
            req.Headers.Add("Referer", Referer);
            req.Headers.Add("User-Agent", HeadersUa);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.Add("Cookie", cookie);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement json = JsonDocument.Parse(ParseJsonp(text)).RootElement;

            string lyric = json.TryGetProperty("lyric", out JsonElement lEl) ? DecodeBase64(lEl.GetString() ?? "") : "";
            string? translation = json.TryGetProperty("trans", out JsonElement tEl) ? DecodeBase64(tEl.GetString() ?? "") : null;

            return new OnlineLyrics { Lyric = lyric, Translation = string.IsNullOrEmpty(translation) ? null : translation };
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取歌词失败", e);
            return new OnlineLyrics();
        }
    }

    /// <summary>QQ 音乐搜索签名（对照 qqSearchSign）。</summary>
    private static string QqSearchSign(string text)
    {
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        byte[] hashBytes = Encoding.UTF8.GetBytes(hash);

        int[] part1Idx = [23, 14, 6, 36, 16, 40, 7, 19];
        int[] part2Idx = [16, 1, 32, 12, 19, 27, 8, 5];
        byte[] scramble = [89, 39, 179, 150, 218, 82, 58, 252, 177, 52, 186, 123, 120, 64, 242, 133, 143, 161, 121, 179];

        string part1 = new string(part1Idx.Where(i => i < hashBytes.Length).Select(i => (char)hashBytes[i]).ToArray());
        string part2 = new string(part2Idx.Where(i => i < hashBytes.Length).Select(i => (char)hashBytes[i]).ToArray());

        byte[] bytes = scramble.Select((v, i) =>
        {
            string hexPair = hash.Substring(i * 2, 2);
            return (byte)(v ^ Convert.ToByte(hexPair, 16));
        }).ToArray();

        string middle = Convert.ToBase64String(bytes);
        string filtered = new string(middle.Where(c => c != '\\' && c != '/' && c != '+' && c != '=').ToArray());

        return $"zzc{part1}{filtered}{part2}".ToLowerInvariant();
    }

    private static OnlineTrack MapQqTrack(JsonElement track)
    {
        JsonElement album = track.GetElement("album");
        JsonElement singer = track.GetElement("singer");

        var artists = new List<string>();
        if (singer.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in singer.EnumerateArray())
            {
                string name = s.GetStr("name", s.GetStr("title", ""));
                if (!string.IsNullOrEmpty(name))
                    artists.Add(name);
            }
        }

        string mid = track.GetStr("mid", track.GetStr("songmid", ""));
        string mediaMid = track.TryGetProperty("file", out JsonElement file)
            ? file.GetStr("media_mid", track.GetStr("strMediaMid", ""))
            : track.GetStr("strMediaMid", "");

        string albumMid = album.ValueKind == JsonValueKind.Object
            ? album.GetStr("mid", album.GetStr("pmid", ""))
            : track.GetStr("albummid", "");

        string songName = SystemToolkit.Core.Utilities.TextSanitizer.StripInvisible(track.GetStr("name", track.GetStr("title", track.GetStr("songname", "")))) ?? "";
        string albumName = SystemToolkit.Core.Utilities.TextSanitizer.StripInvisible(album.ValueKind == JsonValueKind.Object
            ? album.GetStr("name", album.GetStr("title", track.GetStr("albumname", "")))
            : track.GetStr("albumname", "")) ?? "";

        long interval = track.GetLong("interval");
        long? qqSongId = track.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number
            ? (long?)idEl.GetInt64()
            : track.TryGetProperty("songid", out JsonElement sidEl) && sidEl.ValueKind == JsonValueKind.Number ? (long?)sidEl.GetInt64() : null;

        int fee = 0;
        if (track.TryGetProperty("pay", out JsonElement payEl) && payEl.TryGetProperty("pay_play", out JsonElement ppEl))
            fee = ppEl.ValueKind == JsonValueKind.Number && ppEl.GetInt64() > 0 ? 1 : 0;

        string id = !string.IsNullOrEmpty(mid) ? mid : track.TryGetProperty("id", out JsonElement idEl2) && idEl2.ValueKind == JsonValueKind.Number
            ? idEl2.GetInt64().ToString() : qqSongId?.ToString() ?? "";

        return new OnlineTrack
        {
            Provider = OnlineProvider.QQMusic,
            Id = id,
            Mid = !string.IsNullOrEmpty(mid) ? mid : null,
            MediaMid = !string.IsNullOrEmpty(mediaMid) ? mediaMid : null,
            Name = songName,
            Artist = string.Join(" / ", artists),
            Album = albumName,
            Cover = QqAlbumCover(albumMid, 800),
            DurationMs = (ulong)(interval * 1000),
            Fee = fee,
            Playable = false,
            QqSongId = qqSongId,
        };
    }

    private static string QqAlbumCover(string albumMid, int size) =>
        string.IsNullOrEmpty(albumMid) ? "" : $"https://y.qq.com/music/photo_new/T002R{size}x{size}M000{albumMid}.jpg?max_age=2592000";

    private static string ParseJsonp(string text)
    {
        string raw = text.Trim();
        bool isJsonp = raw.EndsWith(')') || raw.EndsWith(");");
        bool hasCallback = raw.StartsWith("callback") || raw.StartsWith("MusicJsonCallback")
            || raw.StartsWith("jsonCallback") || raw.StartsWith("Callback");
        if (isJsonp && hasCallback)
        {
            int start = raw.IndexOf('(') + 1;
            int end = raw.LastIndexOf(')');
            if (start < end)
                return raw[start..end];
        }
        return raw;
    }

    private static string DecodeBase64(string b64) =>
        string.IsNullOrEmpty(b64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    private static OnlineSongUrlResult Fail(string msg) => new() { Playable = false, Reason = "error", Message = msg };

    // ════════════ musicu.fcg 通用 POST 助手 ════════════

    private async Task<JsonElement> PostMusicuAsync(string payloadJson, string cookie, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, MusicuUrl);
        req.Headers.Add("Referer", Referer);
        req.Headers.Add("User-Agent", HeadersUa);
        if (!string.IsNullOrEmpty(cookie))
            req.Headers.Add("Cookie", cookie);
        req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(ParseJsonp(text)).RootElement;
    }

    /// <summary>从 Cookie 中提取 QQ 音乐 uin（含 o 前缀剥离）。</summary>
    private static string ExtractUin(string cookie)
    {
        if (string.IsNullOrEmpty(cookie))
            return "0";
        foreach (string kv in cookie.Split(';', '&'))
        {
            string part = kv.Trim();
            if (part.StartsWith("uin=", StringComparison.OrdinalIgnoreCase))
                return part[4..].TrimStart('o', 'O');
            if (part.StartsWith("wxuin=", StringComparison.OrdinalIgnoreCase))
                return part[6..];
        }
        return "0";
    }

    /// <summary>c.y.qq.com 旧版 CGI GET（对照 NexBox qq_get_json）：带 Referer/UA/Cookie，JSONP 兼容解析。</summary>
    private async Task<JsonElement> QqGetJsonAsync(string url, string cookie, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer))
            req.Headers.Referrer = new Uri(referer);
        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", HeadersUa);
        if (!string.IsNullOrEmpty(cookie))
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(ParseJsonp(text)).RootElement;
    }

    /// <summary>
    /// 从 QQ 歌单行提取字段（字段名候选链对照 NexBox map_qq_playlist）：
    /// id = dissid|tid|dissId|id|diss_id|dirid；name = diss_name|dissname|name|title；
    /// cover = diss_cover|dissCover|logo|picurl|cover；count = song_cnt|songCnt|songnum|songNum|total_song_num|song_count|songCount；
    /// creator = hostname|nick|creator|nickname。
    /// </summary>
    private static OnlinePlaylist MapQqPlaylistRow(JsonElement item)
    {
        static string FirstStr(JsonElement e, params string[] keys)
        {
            foreach (string k in keys)
                if (e.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                    return v.GetString()!;
            return "";
        }
        // 🔴 id 候选链必须同时接受数字值（2026-09-09 实测事故）：自建歌单行的 tid（全局 disstid，
        // 十位数大数）是 JSON number，只收字符串会跳过它落到 dirid（用户本地位小数字，如 3），
        // 拿 dirid 当 disstid 请求曲目 → cdlist_len=0 → 歌单永远空白（日志 id=3/205 实证）。
        static string FirstId(JsonElement e, params string[] keys)
        {
            foreach (string k in keys)
            {
                if (!e.TryGetProperty(k, out JsonElement v))
                    continue;
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                    return v.GetString()!;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l))
                    return l.ToString();
            }
            return "";
        }
        static long FirstInt(JsonElement e, params string[] keys)
        {
            foreach (string k in keys)
                if (e.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l))
                    return l;
            return 0;
        }

        long dirid = 0;
        if (item.TryGetProperty("dirid", out JsonElement diridEl) && diridEl.ValueKind == JsonValueKind.Number)
        {
            dirid = diridEl.GetInt64();
        }
        else if (item.TryGetProperty("dir_id", out JsonElement dirIdEl) && dirIdEl.ValueKind == JsonValueKind.Number)
        {
            dirid = dirIdEl.GetInt64();
        }

        // 🔴 "我喜欢"归一（对照 NexBox map_qq_playlist 开头的 liked 分支）：dirid=201 的虚拟歌单
        // 行的 tid/dissid 常为 0，旧映射得到 id="0" → 用 disstid=0 请求曲目 → cdlist_len=0 空白
        // （2026-09-09 日志实锤 id=0）。识别后强制 Id="liked"，走 LoadLikedListAsync 喜欢列表接口。
        bool liked = dirid == 201;
        if (!liked)
        {
            string normName = FirstStr(item, "diss_name", "dissname", "name", "title")
                .Replace("·", "").Replace("•", "").Replace(" ", "").Replace("　", "");
            liked = normName is "我喜欢" or "我的喜欢" or "喜欢的音乐" or "qq音乐我喜欢" or "qq音乐我的喜欢" or "qq音乐喜欢的音乐";
        }

        string id = liked ? "liked" : FirstId(item, "dissid", "tid", "dissId", "id", "diss_id");
        // 🔴 id="0" 同样是"我喜欢"的特征（dirid 字段缺失时 tid/dissid 常为 0；正常 disstid 不会是 0）——
        // 不归一就会拿 disstid=0 请求曲目 → cdlist_len=0 空白（2026-09-09 二次实证）
        if (!liked && id == "0")
        {
            liked = true;
            id = "liked";
        }
        if (string.IsNullOrEmpty(id) && dirid > 0)
        {
            id = dirid.ToString();
        }
        string name = liked ? "我喜欢的音乐" : FirstStr(item, "diss_name", "dissname", "name", "title");
        string cover = FirstStr(item, "diss_cover", "dissCover", "logo", "picurl", "cover");
        long count = FirstInt(item, "song_cnt", "songCnt", "songnum", "songNum", "total_song_num", "song_count", "songCount");
        string creator = FirstStr(item, "hostname", "nick", "creator", "nickname");

        return new OnlinePlaylist
        {
            Provider = OnlineProvider.QQMusic,
            Id = id,
            Name = name,
            Cover = cover,
            TrackCount = (uint)Math.Max(0, count),
            Creator = string.IsNullOrEmpty(creator) ? "QQ 音乐" : creator,
            PlayCount = 0,
        };
    }

    // ════════════ 歌手 ════════════

    /// <summary>搜索歌手（music.search.SearchFReq，grp=[2,7]）。</summary>
    public async Task<List<OnlineArtist>> SearchArtistsAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string sign = QqSearchSign(keywords);
            string escKw = keywords.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string payloadJson = "{\"music.search.SearchFReq\":{\"query\":\"" + escKw + "\",\"page_size\":" + limit + ",\"page_num\":1,\"grp\":[2,7],\"search_id\":\"" + sign + "\"},\"comm\":{\"ct\":24,\"cv\":0}}";
            JsonElement json = await PostMusicuAsync(payloadJson, cookie, ct);

            var list = new List<OnlineArtist>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            if (!data.TryGetProperty("body", out JsonElement bodyEl))
                return list;
            if (!bodyEl.TryGetProperty("singer", out JsonElement singer))
                return list;
            if (!singer.TryGetProperty("list", out JsonElement singerList))
                return list;

            foreach (JsonElement item in singerList.EnumerateArray())
            {
                string name = item.GetStr("name", item.GetStr("singername", ""));
                if (string.IsNullOrEmpty(name))
                    continue;
                string mid = item.GetStr("mid", item.GetStr("singermid", ""));
                list.Add(new OnlineArtist
                {
                    Id = item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : mid,
                    Mid = mid,
                    Name = name,
                    Avatar = item.GetStr("pic", item.GetStr("singerpic", "")),
                    SongCount = item.GetInt("song_num"),
                    AlbumCount = item.GetInt("album_num"),
                    Brief = item.GetStr("desc", ""),
                });
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 搜索歌手失败", e);
            return [];
        }
    }

    /// <summary>获取歌手歌曲列表（GetSongListBySinger）。</summary>
    public async Task<List<OnlineTrack>> LoadArtistSongsAsync(string artistId, int offset = 0, int limit = 50, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musichallSong.PlaySongList",
                    method = "GetSongListBySinger",
                    param = new { singermid = artistId, start = offset, num = limit },
                },
                comm = new { ct = 24, cv = 0 },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            var list = new List<OnlineTrack>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            if (!data.TryGetProperty("songList", out JsonElement songList))
                return list;

            foreach (JsonElement track in songList.EnumerateArray())
            {
                OnlineTrack mapped = MapQqTrack(track);
                if (!string.IsNullOrEmpty(mapped.Name))
                    list.Add(mapped);
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取歌手歌曲失败", e);
            return [];
        }
    }

    // ════════════ 歌单 ════════════

    /// <summary>搜索歌单（music.search.SearchFReq，grp=[4,5]）。</summary>
    public async Task<List<OnlinePlaylist>> SearchPlaylistsAsync(string keywords, int limit = 30, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string sign = QqSearchSign(keywords);
            string escKw = keywords.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string payloadJson = "{\"music.search.SearchFReq\":{\"query\":\"" + escKw + "\",\"page_size\":" + limit + ",\"page_num\":1,\"grp\":[4,5],\"search_id\":\"" + sign + "\"},\"comm\":{\"ct\":24,\"cv\":0}}";
            JsonElement json = await PostMusicuAsync(payloadJson, cookie, ct);

            var list = new List<OnlinePlaylist>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            if (!data.TryGetProperty("body", out JsonElement bodyEl))
                return list;
            if (!bodyEl.TryGetProperty("songlist", out JsonElement sl))
                return list;
            if (!sl.TryGetProperty("list", out JsonElement plList))
                return list;

            foreach (JsonElement item in plList.EnumerateArray())
            {
                string name = item.GetStr("dissname", item.GetStr("title", ""));
                if (string.IsNullOrEmpty(name))
                    continue;
                list.Add(new OnlinePlaylist
                {
                    Provider = OnlineProvider.QQMusic,
                    Id = item.GetStr("dissid", item.GetStr("disstid", item.GetStr("id", ""))),
                    Name = name,
                    Cover = item.GetStr("logo", item.GetStr("picurl", item.GetStr("imgurl", ""))),
                    TrackCount = (uint)item.GetInt("song_num"),
                    Creator = item.GetElement("creator").GetStr("name", ""),
                    PlayCount = item.GetInt("listennum"),
                });
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 搜索歌单失败", e);
            return [];
        }
    }

    /// <summary>
    /// D1.1：分页获取用户歌单（1:1 对照 NexBox qqmusic.rs：创建歌单走 fcg_user_created_diss，
    /// 收藏歌单走 fcg_get_profile_order_asset reqtype=3，空则回退 musicu PlaylistBaseRead reqtype=2）。
    /// 【坑】旧实现用 module=music.musicasset.Playlist/GetPlaylistByUin dirId=0 —— 该模块名不存在，
    /// 永远返回空（表现为"登录成功但歌单为空"，2026-09-03 用户实测定位）。
    /// <para>limit&lt;=0 视为"一次全量返回"。</para>
    /// </summary>
    public async Task<(List<OnlinePlaylist> playlists, int total)> LoadUserPlaylistsPageAsync(string cookie = "", int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        const int PageSize = 200;
        const int MaxPages = 25;
        try
        {
            string uin = ExtractUin(cookie);
            var list = new List<OnlinePlaylist>();
            if (string.IsNullOrEmpty(uin) || uin == "0")
                return (list, 0);
            string profileReferer = "https://y.qq.com/portal/profile.html";

            // ── ① 创建的歌单：fcg_user_created_diss ──
            for (int page = 0; page < MaxPages; page++)
            {
                int sin = page * PageSize;
                string url = "https://c.y.qq.com/rsc/fcgi-bin/fcg_user_created_diss" +
                          $"?hostUin=0&hostuin={uin}&sin={sin}&size={PageSize}&g_tk=5381&loginUin={uin}" +
                          "&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
                JsonElement body;
                try
                { body = await QqGetJsonAsync(url, cookie, profileReferer, ct); }
                catch (Exception e) { _logger.Warn($"[QQMusic] created playlists page {page} failed: {e.Message}"); break; }

                if (!body.TryGetProperty("data", out JsonElement data) || !data.TryGetProperty("disslist", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                    break;
                int len = rows.GetArrayLength();
                foreach (JsonElement item in rows.EnumerateArray())
                {
                    OnlinePlaylist pl = MapQqPlaylistRow(item);
                    if (!string.IsNullOrEmpty(pl.Id) && !string.IsNullOrEmpty(pl.Name))
                        list.Add(pl);
                }
                if (len < PageSize)
                    break;
            }

            // ── ② 收藏的歌单：旧版 fcg_get_profile_order_asset reqtype=3 ──
            for (int page = 0; page < MaxPages; page++)
            {
                int sin = page * PageSize;
                int ein = sin + PageSize - 1;
                string url = "https://c.y.qq.com/fav/fcgi-bin/fcg_get_profile_order_asset.fcg" +
                          $"?ct=20&cid=205360956&userid={uin}&reqtype=3&sin={sin}&ein={ein}";
                JsonElement body;
                try
                { body = await QqGetJsonAsync(url, cookie, profileReferer, ct); }
                catch (Exception e) { _logger.Warn($"[QQMusic] collected playlists page {page} failed: {e.Message}"); break; }

                if (!body.TryGetProperty("data", out JsonElement data))
                    break;
                JsonElement? rows = null;
                foreach (string? key in new[] { "cdlist", "v_playlist", "playlist", "disslist" })
                    if (data.TryGetProperty(key, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    { rows = arr; break; }
                if (rows is null)
                    break;
                int len = rows.Value.GetArrayLength();
                foreach (JsonElement item in rows.Value.EnumerateArray())
                {
                    OnlinePlaylist pl = MapQqPlaylistRow(item);
                    if (!string.IsNullOrEmpty(pl.Id) && !string.IsNullOrEmpty(pl.Name) && list.All(x => x.Id != pl.Id))
                        list.Add(pl);
                }
                if (len < PageSize)
                    break;
            }

            // ── ③ 旧版接口空 → 回退 musicu PlaylistBaseRead/GetPlaylistByUin reqtype=2 ──
            if (list.Count == 0)
            {
                for (int page = 1; page <= MaxPages; page++)
                {
                    var payload = new
                    {
                        comm = new { ct = 24, cv = 0 },
                        req_0 = new
                        {
                            module = "music.musicasset.PlaylistBaseRead",
                            method = "GetPlaylistByUin",
                            param = new { hostUin = uin, reqtype = 2, page, size = PageSize, order = 5 },
                        },
                    };
                    JsonElement json;
                    try
                    { json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct); }
                    catch (Exception e) { _logger.Warn($"[QQMusic] collected musicu page {page} failed: {e.Message}"); break; }

                    if (!json.TryGetProperty("req_0", out JsonElement req0) || !req0.TryGetProperty("data", out JsonElement data))
                        break;
                    int code = req0.TryGetProperty("code", out JsonElement codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int cV) ? cV : 0;
                    if (code != 0)
                    { _logger.Warn($"[QQMusic] collected musicu page {page} code={code}"); break; }

                    JsonElement? rows = null;
                    foreach (string? key in new[] { "v_playlist", "cdlist", "playlist", "disslist" })
                        if (data.TryGetProperty(key, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                        { rows = arr; break; }
                    if (rows is null)
                        break;
                    int len = rows.Value.GetArrayLength();
                    foreach (JsonElement item in rows.Value.EnumerateArray())
                    {
                        OnlinePlaylist pl = MapQqPlaylistRow(item);
                        if (!string.IsNullOrEmpty(pl.Id) && !string.IsNullOrEmpty(pl.Name) && list.All(x => x.Id != pl.Id))
                            list.Add(pl);
                    }
                    if (len < PageSize)
                        break;
                }
            }

            // ── ③ 「我喜欢」卡片置顶（对照 NexBox get_liked_playlist_card：dirid=201 的虚拟歌单，
            //    不在任何 disslist 里返回，必须单独插卡——缺了它用户歌单里永远看不到"我喜欢"） ──
            if (list.All(x => !IsQqLikedPlaylistId(x.Id)))
            {
                uint likedCount = 0;
                try
                {
                    string likedPayload = JsonSerializer.Serialize(new
                    {
                        comm = new { ct = 24, cv = 0 },
                        req_0 = new
                        {
                            module = "music.srfDissInfo.DissInfo",
                            method = "CgiGetDiss",
                            param = new { disstid = 0, dirid = 201, tag = 1, song_begin = 0, song_num = 1, userinfo = 1, orderlist = 1 },
                        },
                    });
                    JsonElement likedJson = await PostMusicuAsync(likedPayload, cookie, ct);
                    if (likedJson.TryGetProperty("req_0", out JsonElement lReq)
                        && lReq.TryGetProperty("data", out JsonElement lData)
                        && lData.TryGetProperty("total_song_num", out JsonElement lCnt)
                        && lCnt.TryGetUInt32(out uint lv))
                    {
                        likedCount = lv;
                    }
                }
                catch (Exception e)
                {
                    _logger.Warn($"[QQMusic] liked playlist card failed: {e.Message}"); // 拿不到计数也显示卡片（count=0）
                }

                list.Insert(0, new OnlinePlaylist
                {
                    Provider = OnlineProvider.QQMusic,
                    Id = "liked",
                    Name = "我喜欢的音乐",
                    Cover = "https://y.gtimg.cn/mediastyle/global/img/cover_like.png",
                    TrackCount = likedCount,
                    Creator = string.IsNullOrEmpty(uin) ? "QQ 音乐" : uin,
                    PlayCount = 0,
                });
            }

            return (list, list.Count);
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取用户歌单失败", e);
            return ([], 0);
        }
    }

    // 保持向后兼容（limit=0 → 一次全量返回；老 UI 不改调用签名也能受益）
    public async Task<List<OnlinePlaylist>> LoadUserPlaylistsAsync(string cookie = "", CancellationToken ct = default)
        => (await LoadUserPlaylistsPageAsync(cookie, 0, limit: 0, ct).ConfigureAwait(true)).playlists;


    /// <summary>获取歌单内全部歌曲（GetPlaylistTotalSong）。</summary>
    /// <summary>
    /// 加载歌单曲目（2026-09-03 重写：旧实现用的 `music.musicasset.Playlist / GetPlaylistTotalSong`
    /// 是**不存在的模块名**（与已修的歌单列表同族），服务器恒定报错 → 点歌单永远空白。
    /// 改回 NexBox 的旧版 CGI（qqmusic.rs playlist_tracks_legacy）：
    /// GET `qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg`（type/utf8/disstid/song_begin/song_num/loginUin/…），
    /// Referer = https://y.qq.com/n/yqq/playlist，解析 `cdlist[0].songlist`。
    /// </summary>
    public async Task<List<OnlineTrack>> LoadPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 100, string cookie = "", CancellationToken ct = default)
        => await LoadPlaylistTracksCoreAsync(playlistId, offset < 0 ? 0 : offset, limit <= 0 ? 100 : limit, cookie, ct);

    /// <summary>分页加载歌单曲目（同上，走 NexBox 旧版 CGI）。</summary>
    public async Task<List<OnlineTrack>> LoadPlaylistTracksRangeAsync(string playlistId, int start, int count, string cookie = "", CancellationToken ct = default)
        => await LoadPlaylistTracksCoreAsync(playlistId, start < 0 ? 0 : start, count <= 0 ? 100 : count, cookie, ct);

    private async Task<List<OnlineTrack>> LoadPlaylistTracksCoreAsync(string playlistId, int start, int count, string cookie, CancellationToken ct)
    {
        try
        {
            string pid = (playlistId ?? "").Trim();
            if (string.IsNullOrEmpty(pid))
                return [];

            // 「我喜欢的」是虚拟歌单，走喜欢列表接口（NexBox is_qq_liked_playlist_id）
            if (IsQqLikedPlaylistId(pid))
            {
                List<OnlineTrack> liked = await LoadLikedListAsync(start, count, cookie, ct);
                return liked;
            }

            string uin = ExtractUin(cookie);
            if (string.IsNullOrEmpty(uin))
                uin = "0";
            string url = $"{QqPlaylistTracksUrl}?type=1&utf8=1&disstid={Uri.EscapeDataString(pid)}"
                    + $"&song_begin={start}&song_num={count}&loginUin={Uri.EscapeDataString(uin)}"
                    + "&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";

            JsonElement body = await QqGetJsonAsync(url, cookie, QqPlaylistReferer, ct);

            // 诊断留痕：subcode/msg/cdlist 长度，便于下次"歌单打不开"直接定位
            int subcode = body.TryGetProperty("subcode", out JsonElement sc) && sc.TryGetInt32(out int scv) ? scv : 0;
            string msg = body.GetStr("msg", body.GetStr("errmsg", ""));
            JsonElement cdlist = body.TryGetProperty("cdlist", out JsonElement cl) && cl.ValueKind == JsonValueKind.Array ? cl : default;
            int cdlistLen = cdlist.ValueKind == JsonValueKind.Array ? cdlist.GetArrayLength() : 0;
            if (subcode != 0 || !string.IsNullOrEmpty(msg) || cdlistLen == 0)
            {
                _logger.Warn($"[QQMusic] playlist tracks subcode={subcode} msg={msg} cdlist_len={cdlistLen} id={pid}");
                return [];
            }

            JsonElement detail = cdlist[0];
            if (!detail.TryGetProperty("songlist", out JsonElement songList) || songList.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn($"[QQMusic] 歌单 {pid} 无 songlist 字段");
                return [];
            }

            var list = new List<OnlineTrack>();
            foreach (JsonElement track in songList.EnumerateArray())
            {
                OnlineTrack mapped = MapQqTrack(track);
                if (!string.IsNullOrEmpty(mapped.Name))
                    list.Add(mapped);
            }
            _logger.Info($"[QQMusic] 歌单 {pid} 曲目 {list.Count} 首（song_begin={start}, song_num={count}）");
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取歌单歌曲失败", e);
            return [];
        }
    }

    /// <summary>「我喜欢的」虚拟歌单 id（对照 NexBox QQ_LIKED_PLAYLIST_ID / QQ_LIKED_DIRID）。</summary>
    private static bool IsQqLikedPlaylistId(string id)
    {
        string v = (id ?? "").Trim().ToLowerInvariant();
        return v is "liked" or "qq-liked" or "201";
    }

    // ════════════ 榜单 / 推荐 ════════════

    /// <summary>获取所有官方排行榜（Toplist.GetAllToplist）。</summary>
    public async Task<List<OnlinePlaylist>> LoadOfficialChartsAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musicasset.Toplist",
                    method = "GetAllToplist",
                    param = new { },
                },
                comm = new { ct = 24, cv = 0 },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            var list = new List<OnlinePlaylist>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            if (!data.TryGetProperty("v_group", out JsonElement groups))
                return list;

            foreach (JsonElement grp in groups.EnumerateArray())
            {
                if (!grp.TryGetProperty("toplist", out JsonElement toplists))
                    continue;
                foreach (JsonElement t in toplists.EnumerateArray())
                {
                    string name = t.GetStr("title", t.GetStr("name", ""));
                    if (string.IsNullOrEmpty(name))
                        continue;
                    list.Add(new OnlinePlaylist
                    {
                        Provider = OnlineProvider.QQMusic,
                        Id = t.GetStr("topId", t.GetStr("id", "")),
                        Name = name,
                        Cover = t.GetStr("picUrl", t.GetStr("frontPicUrl", t.GetStr("pic", ""))),
                        TrackCount = (uint)t.GetInt("songCount", t.GetInt("songNum", 0)),
                        Creator = grp.GetStr("groupName", ""),
                        PlayCount = t.GetInt("listenNum", 0),
                    });
                }
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取排行榜失败", e);
            return [];
        }
    }

    /// <summary>
    /// 获取推荐歌单。主路径对照 NexBox recommend_playlists：CGI fcg_get_diss_by_tag.fcg
    /// （categoryId=10000000, order=play, size=20，解析 data.list：dissid/dissname/imgurl/songnum）；
    /// 旧 musicu RecommendFeed 路径保留为降级。
    /// </summary>
    public async Task<List<OnlinePlaylist>> LoadRecommendationsAsync(string cookie = "", CancellationToken ct = default)
    {
        // ── 主路径：NexBox 现役 CGI ──
        try
        {
            string url = "https://c.y.qq.com/splcloud/fcgi-bin/fcg_get_diss_by_tag.fcg" +
                "?g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8&outCharset=utf-8" +
                "&notice=0&platform=yqq&needNewCode=0" +
                "&categoryId=10000000&sin=0&size=20&order=play&format=json";
            JsonElement json = await QqGetJsonAsync(url, cookie, Referer, ct);
            if (json.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("list", out JsonElement listArr)
                && listArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<OnlinePlaylist>();
                foreach (JsonElement item in listArr.EnumerateArray())
                {
                    string id = item.GetStr("dissid", "");
                    string name = item.GetStr("dissname", "");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                        continue;
                    string creator = item.TryGetProperty("creator", out JsonElement cEl)
                        ? (cEl.ValueKind == JsonValueKind.String ? cEl.GetString() ?? "" : cEl.GetStr("name", ""))
                        : "";
                    list.Add(new OnlinePlaylist
                    {
                        Provider = OnlineProvider.QQMusic,
                        Id = id,
                        Name = name,
                        Cover = item.GetStr("imgurl", ""),
                        TrackCount = (uint)Math.Max(0, item.GetInt("songnum", 0)),
                        Creator = string.IsNullOrEmpty(creator) ? "QQ 音乐" : creator,
                        PlayCount = 0,
                    });
                }

                if (list.Count > 0)
                {
                    _logger.Info($"[QQMusic] 推荐歌单 {list.Count} 个（diss_by_tag）");
                    return list;
                }
            }
        }
        catch (Exception e)
        {
            _logger.Warn($"[QQMusic] 推荐歌单 diss_by_tag 路径失败，降级 musicu：{e.Message}");
        }

        // ── 降级：旧 musicu RecommendFeed ──
        try
        {
            var payload = new
            {
                req_0 = new
                {
                    module = "music.recommend.RecommendFeed",
                    method = "GetRecommendPlaylist",
                    param = new { from = 0, size = 30 },
                },
                comm = new { ct = 24, cv = 0 },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            var list = new List<OnlinePlaylist>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            // 推荐歌单可能在 data.playlist 或 data.v_playlist
            JsonElement plArr = data.TryGetProperty("playlist", out JsonElement pl) ? pl
                : data.TryGetProperty("v_playlist", out JsonElement vp) ? vp : default;
            if (plArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (JsonElement item in plArr.EnumerateArray())
            {
                string name = item.GetStr("title", item.GetStr("dissname", item.GetStr("name", "")));
                if (string.IsNullOrEmpty(name))
                    continue;
                list.Add(new OnlinePlaylist
                {
                    Provider = OnlineProvider.QQMusic,
                    Id = item.GetStr("id", item.GetStr("dissid", item.GetStr("contentId", ""))),
                    Name = name,
                    Cover = item.GetStr("pic", item.GetStr("logo", item.GetStr("cover", ""))),
                    TrackCount = (uint)item.GetInt("song_num", item.GetInt("songNum", 0)),
                    Creator = item.GetElement("creator").GetStr("name", ""),
                    PlayCount = item.GetInt("listennum", item.GetInt("playCount", 0)),
                });
            }
            _logger.Info($"[QQMusic] 推荐歌单 {list.Count} 个（musicu 降级）");
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取推荐歌单失败", e);
            return [];
        }
    }

    /// <summary>获取每日推荐歌曲（daily.RecommendSong.GetDailySong）。</summary>
    public async Task<List<OnlineTrack>> LoadDailyRecommendSongsAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                req_0 = new
                {
                    module = "music.recommend.daily.RecommendSong",
                    method = "GetDailySong",
                    param = new { },
                },
                comm = new { ct = 24, cv = 0 },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            var list = new List<OnlineTrack>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            // 每日推荐歌曲在 data.v_song 或 data.songlist
            JsonElement songArr = data.TryGetProperty("v_song", out JsonElement vs) ? vs
                : data.TryGetProperty("songlist", out JsonElement sl) ? sl : default;
            if (songArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (JsonElement track in songArr.EnumerateArray())
            {
                OnlineTrack mapped = MapQqTrack(track);
                if (!string.IsNullOrEmpty(mapped.Name))
                    list.Add(mapped);
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取每日推荐失败", e);
            return [];
        }
    }

    // ════════════ 喜欢 ════════════

    /// <summary>切换喜欢歌曲（FavSong.AddSong / DelSong，需 Cookie）。</summary>
    public async Task<bool> ToggleLikeAsync(string songId, bool like, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string uin = ExtractUin(cookie);
            string method = like ? "AddSong" : "DelSong";
            var payload = new
            {
                req_0 = new
                {
                    module = "music.fav.FavSong",
                    method,
                    param = new { mid = songId, uin },
                },
                comm = new { ct = 24, cv = 0, uin },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return false;
            int code = req0.TryGetProperty("code", out JsonElement codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int cV) ? cV : -1;
            return code == 0;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 切换喜欢失败", e);
            return false;
        }
    }

    /// <summary>
    /// 获取我喜欢的歌曲列表（对照 NexBox liked_playlist_tracks：music.srfDissInfo.DissInfo/CgiGetDiss，
    /// dirid=201 虚拟歌单，按 song_begin/song_num 分页）。
    /// 🔴 不用 music.fav.FavSong/GetMyFavSong（老项目遗留，NexBox 现役实现未用它）。
    /// </summary>
    public async Task<List<OnlineTrack>> LoadLikedListAsync(int start, int count, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                comm = new { ct = 24, cv = 0 },
                req_0 = new
                {
                    module = "music.srfDissInfo.DissInfo",
                    method = "CgiGetDiss",
                    param = new
                    {
                        disstid = 0,
                        dirid = 201,
                        tag = 1,
                        song_begin = start,
                        song_num = count,
                        userinfo = 1,
                        orderlist = 1,
                    },
                },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            var list = new List<OnlineTrack>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
            {
                _logger.Warn("[QQMusic] 喜欢列表：无 req_0");
                return list;
            }
            if (!req0.TryGetProperty("data", out JsonElement data))
            {
                _logger.Warn("[QQMusic] 喜欢列表：无 data");
                return list;
            }
            if (!data.TryGetProperty("songlist", out JsonElement songList) || songList.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn("[QQMusic] 喜欢列表：无 songlist 字段（多半未登录或凭据残缺）");
                return list;
            }

            foreach (JsonElement track in songList.EnumerateArray())
            {
                OnlineTrack mapped = MapQqTrack(track);
                if (!string.IsNullOrEmpty(mapped.Name))
                    list.Add(mapped);
            }
            _logger.Info($"[QQMusic] 喜欢列表 {list.Count} 首（song_begin={start}, song_num={count}）");
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取喜欢列表失败", e);
            return [];
        }
    }

    // ════════════ 专辑 ════════════

    /// <summary>获取专辑歌曲列表（AlbumSongList.GetAlbumSongList）。</summary>
    public async Task<List<OnlineTrack>> LoadAlbumTracksAsync(string albumId, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musichallAlbum.AlbumSongList",
                    method = "GetAlbumSongList",
                    param = new { albumMid = albumId, albumId = 0, start = 0, num = 200 },
                },
                comm = new { ct = 24, cv = 0 },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            var list = new List<OnlineTrack>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            if (!data.TryGetProperty("list", out JsonElement songList))
                return list;

            foreach (JsonElement item in songList.EnumerateArray())
            {
                // 部分 API 版本将歌曲包裹在 songInfo 中
                JsonElement track = item.TryGetProperty("songInfo", out JsonElement si) ? si : item;
                OnlineTrack mapped = MapQqTrack(track);
                if (!string.IsNullOrEmpty(mapped.Name))
                    list.Add(mapped);
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取专辑歌曲失败", e);
            return [];
        }
    }

    // ════════════ 登录 ════════════

    /// <summary>获取登录状态（从 Cookie 解析 uin 并尝试拉取用户信息）。</summary>
    public async Task<OnlineLoginInfo> GetLoginStatusAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string uin = ExtractUin(cookie);
            if (string.IsNullOrEmpty(uin) || uin == "0")
                return new OnlineLoginInfo { Provider = OnlineProvider.QQMusic, LoggedIn = false };

            var payload = new
            {
                req_0 = new
                {
                    module = "music.UserInfo.userInfo",
                    method = "GetUserInfo",
                    param = new { },
                },
                comm = new { ct = 24, cv = 0, uin },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            if (!json.TryGetProperty("req_0", out JsonElement req0) || !req0.TryGetProperty("data", out JsonElement data))
                return new OnlineLoginInfo { Provider = OnlineProvider.QQMusic, LoggedIn = true, Uid = uin };

            return new OnlineLoginInfo
            {
                Provider = OnlineProvider.QQMusic,
                LoggedIn = true,
                Nickname = data.GetStr("nick", data.GetStr("nickname", "")),
                AvatarUrl = data.GetStr("head", data.GetStr("avatar", "")),
                Uid = uin,
            };
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取登录状态失败", e);
            return new OnlineLoginInfo { Provider = OnlineProvider.QQMusic, LoggedIn = false };
        }
    }

    /// <summary>用 Cookie 登录（校验 Cookie 是否有效）。</summary>
    public async Task<bool> LoginWithCookieAsync(string cookie, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cookie))
                return false;
            string uin = ExtractUin(cookie);
            if (string.IsNullOrEmpty(uin) || uin == "0")
                return false;

            // 校验模块对照 NexBox：music.musicasset.PlaylistBaseRead/GetPlaylistByUin reqtype=2
            //（旧实现 music.musicasset.Playlist 模块名不存在，永远 code!=0 → 校验恒失败）
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musicasset.PlaylistBaseRead",
                    method = "GetPlaylistByUin",
                    param = new { hostUin = uin, reqtype = 2, page = 1, size = 1, order = 5 },
                },
                comm = new { ct = 24, cv = 0, uin },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);

            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return false;
            int code = req0.TryGetProperty("code", out JsonElement codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int cV) ? cV : -1;
            return code == 0;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] Cookie 登录校验失败", e);
            return false;
        }
    }

    // ════════════ 登出 ════════════

    /// <summary>QQ 音乐退出登录（服务端失效 + UI 清空本地 Cookie）。</summary>
    public async Task<bool> LogoutAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string uin = ExtractUin(cookie);
            var payload = new
            {
                req_0 = new
                {
                    module = "music.login.LoginServer",
                    method = "Logout",
                    param = new { },
                },
                comm = new { ct = 24, cv = 0, uin },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return false;
            int code = req0.TryGetProperty("code", out JsonElement codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int cV) ? cV : -1;
            return code == 0;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 退出登录失败", e);
            return false;
        }
    }

    // ════════════ 歌单分页 + trackIds + 歌曲详情批量 ════════════

    /// <summary>
    /// 获取歌单元数据 + 全部曲目 id（2026-09-03 重写：旧实现用的 `music.musicasset.Playlist / GetPlaylistInfo`
    /// 在 NexBox 里**不存在**（NexBox 只用了 PlaylistBaseRead），恒定失败。
    /// 改为复用旧版 CGI 一次调用同时产出元数据与曲目 id（NexBox 也是单次调用出两者）。
    /// </summary>
    public async Task<OnlinePlaylistWithTrackIds?> GetPlaylistInfoWithTrackIdsAsync(string playlistId, string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string pid = (playlistId ?? "").Trim();
            if (string.IsNullOrEmpty(pid))
                return null;

            string uin = ExtractUin(cookie);
            if (string.IsNullOrEmpty(uin))
                uin = "0";
            string url = $"{QqPlaylistTracksUrl}?type=1&utf8=1&disstid={Uri.EscapeDataString(pid)}"
                    + $"&song_begin=0&song_num=1000&loginUin={Uri.EscapeDataString(uin)}"
                    + "&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";

            JsonElement body = await QqGetJsonAsync(url, cookie, QqPlaylistReferer, ct);
            if (!body.TryGetProperty("cdlist", out JsonElement cdlist) || cdlist.ValueKind != JsonValueKind.Array || cdlist.GetArrayLength() == 0)
                return null;

            JsonElement detail = cdlist[0];
            var ids = new List<string>();
            if (detail.TryGetProperty("songlist", out JsonElement slist) && slist.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in slist.EnumerateArray())
                {
                    if (t.TryGetProperty("id", out JsonElement idEl))
                    {
                        if (idEl.ValueKind == JsonValueKind.Number)
                            ids.Add(idEl.GetInt64().ToString());
                        else if (idEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(idEl.GetString()))
                            ids.Add(idEl.GetString()!);
                    }
                }
            }

            int total = detail.GetInt("total_song_num", detail.GetInt("songnum", detail.GetInt("song_cnt", ids.Count)));
            var meta = new OnlinePlaylist
            {
                Provider = OnlineProvider.QQMusic,
                Id = pid,
                Name = detail.GetStr("dissname", detail.GetStr("diss_name", detail.GetStr("name", ""))),
                Cover = detail.GetStr("logo", detail.GetStr("diss_cover", "")),
                TrackCount = (uint)Math.Max(0, total),
                Creator = detail.GetStr("nickname", detail.GetStr("creator", "QQ 音乐")),
            };
            return new OnlinePlaylistWithTrackIds { Meta = meta, TrackIds = ids };
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取歌单详情+trackIds 失败", e);
            return null;
        }
    }

    /// <summary>批量获取歌曲详情（QQ GetSongInfoList，用于歌单内搜索后补全）。</summary>
    public async Task<List<OnlineTrack>> LoadSongsByIdsAsync(List<string> ids, string cookie = "", CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];
        try
        {
            string idsStr = "[" + string.Join(",", ids.Select(i => $"\"{i}\"")) + "]";
            string payloadJson =
                "{\"req_0\":{\"module\":\"music.musicasset.SongInfo\",\"method\":\"GetSongInfoList\"," +
                "\"param\":{\"songmid_list\":" + idsStr + "}},\"comm\":{\"ct\":24,\"cv\":0}}";
            JsonElement json = await PostMusicuAsync(payloadJson, cookie, ct);

            var list = new List<OnlineTrack>();
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return list;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return list;
            if (!data.TryGetProperty("songInfoList", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (JsonElement t in arr.EnumerateArray())
            {
                OnlineTrack mapped = MapQqTrack(t);
                if (!string.IsNullOrEmpty(mapped.Name))
                    list.Add(mapped);
            }
            return list;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 批量获取歌曲详情失败", e);
            return [];
        }
    }

    // ════════════ 二维码登录（QQ 音乐）════════════

    /// <summary>QQ 音乐 Step 1：获取二维码 key 与 qrurl。</summary>
    public async Task<OnlineQrLoginResult?> GetQrKeyAsync(string cookie = "", CancellationToken ct = default)
    {
        try
        {
            string url = "https://ssl.ptlogin2.qq.com/ptqrshow?appid=716027609&e=2&l=M&s=3&d=72&v=4&t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", Referer);
            req.Headers.Add("User-Agent", HeadersUa);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            // qrsig 在响应 cookie 里
            string qrsig = "";
            if (resp.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
            {
                foreach (string c in cookies)
                {
                    string part = c.Split(';')[0].Trim();
                    if (part.StartsWith("qrsig=", StringComparison.OrdinalIgnoreCase))
                        qrsig = part[6..];
                }
            }
            if (string.IsNullOrEmpty(qrsig))
            {
                _logger.Warn("[QQMusic] 获取二维码 qrsig 失败：响应无 Set-Cookie qrsig");
                return null;
            }
            return new OnlineQrLoginResult
            {
                Key = qrsig,
                QrUrl = $"data:image/png;base64,{Convert.ToBase64String(await resp.Content.ReadAsByteArrayAsync(ct))}",
            };
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 获取二维码失败", e);
            return null;
        }
    }

    /// <summary>QQ 音乐 Step 2：二维码 key 存在即返回（与 GetQrKeyAsync 复用）。</summary>
    public Task<OnlineQrLoginResult?> CreateQrAsync(string key, string cookie = "", CancellationToken ct = default)
        => Task.FromResult(string.IsNullOrEmpty(key)
            ? null
            : new OnlineQrLoginResult { Key = key, QrUrl = $"https://ssl.ptlogin2.qq.com/ptqrshow?ptqrtoken={ComputeQqPtqToken(key)}" });

    /// <summary>QQ 音乐 Step 3：轮询扫码状态，成功时返回 uin+cookie。</summary>
    public async Task<OnlineQrCheckResult> CheckQrAsync(string key, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            return new OnlineQrCheckResult { Code = 800, Message = "key 为空" };
        try
        {
            int ptqrtoken = ComputeQqPtqToken(key);
            string qs = string.Join("&",
                "u1=" + Uri.EscapeDataString("https://y.qq.com/"),
                "ptredirect=0",
                "h=1",
                "t=1",
                "g=1",
                "from_ui=1",
                "ptlang=2052",
                $"ptqrtoken={ptqrtoken}",
                "action=0-0-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "js_ver=24062611",
                "js_type=1",
                "login_sig=",
                "pt_uistyle=40",
                "aid=716027609",
                "daid=383",
                "o1vId=");
            string url = $"https://xui.ptlogin2.qq.com/ssl/ptqrlogin?{qs}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", "https://xui.ptlogin2.qq.com/");
            req.Headers.Add("User-Agent", HeadersUa);
            req.Headers.Add("Cookie", $"qrsig={key};" + cookie);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            // ptuiCB('66','0','','0','二维码未失效。', '')
            // ptuiCB('67','0','','0','二维码认证中。', '')
            // ptuiCB('0','0','https://ptlogin2.qq.com/jump?...','0','登录成功！', 'xxx')
            int start = text.IndexOf("ptuiCB('", StringComparison.Ordinal);
            if (start < 0)
            {
                _logger.Info($"[QQMusic] 轮询扫码: raw={(text.Length <= 160 ? text : text.Substring(0, 160) + "…")}");
                return new OnlineQrCheckResult { Code = -1, Message = text };
            }
            string inner = text.Substring(start + 8).TrimEnd('\'').TrimEnd(')').TrimEnd(';');
            string[] parts = inner.Split("','");
            if (parts.Length < 6)
                return new OnlineQrCheckResult { Code = -1, Message = text };
            string code = parts[0].Trim('\'');
            string message = parts[4].Trim('\'');
            string nick = parts.Length >= 6 ? parts[5].Trim('\'') : "";
            int codeVal = code switch
            {
                "0" => 803, // 成功
                "66" => 801, // 等待
                "67" => 802, // 已扫码
                "65" => 800, // 过期
                _ => -1,
            };

            string? captured = null;
            if (codeVal == 803 && resp.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
            {
                captured = string.Join("; ", setCookies.Select(c => c.Split(';')[0].Trim()));
            }
            // 与 NetEase 对齐的诊断日志：每次轮询都打 code/message/昵称；
            // 审查 R2（2026-09-10）：captured 在 803 时含 qqmusic_key/skey/uin 会话令牌，
            // 严禁写进日志（引导用户贴日志排错 → 明文令牌会泄露）。只记"是否捕获到"，不落值/原始响应体
            bool cookieCaptured = captured is not null;
            _logger.Info($"[QQMusic] 轮询扫码: code={codeVal} msg={message} nick={(string.IsNullOrEmpty(nick) ? "(无)" : nick)} cookieCaptured={cookieCaptured}");

            return new OnlineQrCheckResult
            {
                Code = codeVal,
                Message = message,
                CapturedCookie = captured,
                Nickname = nick,
                Avatar = "",
            };
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 检查二维码状态失败", e);
            return new OnlineQrCheckResult { Code = -1, Message = e.Message };
        }
    }

    /// <summary>QQ ptqrtoken 算法（基于 qrsig 计算哈希，对照 NexBox ptQrToken）。</summary>
    private static int ComputeQqPtqToken(string qrsig)
    {
        int hash = 0;
        for (int i = 0; i < qrsig.Length; i++)
        {
            hash += (hash << 5) + (int)qrsig[i];
        }
        return hash & int.MaxValue;
    }

    // ════════════ C1.1 歌单收藏（QQ 音乐 musicu：Playlist.{Add,Del}PlaySongDir） ════════════
    // 说明：QQ 音乐官方 API 无"订阅别人歌单"语义，这里等价于"添加到我的收藏歌单目录"
    // （取关 = 从我的收藏目录中移除）。与网易云 SubscribePlaylistAsync 行为对齐：
    // - subscribe=true  调用 AddPlaySongDir(dirId, uin)
    // - subscribe=false 调用 DelPlaySongDir(dirId, uin)
    // 若 playlistId 非数字（QQ dirId 必数字）→ 直接返回 false 并日志提示。

    /// <summary>C1：QQ 音乐收藏 / 取消收藏歌单。</summary>
    public async Task<bool> SubscribePlaylistAsync(string playlistId, bool subscribe, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            _logger.Warn("[QQMusic] 收藏歌单失败：需要登录");
            return false;
        }
        if (!long.TryParse(playlistId, out long dirId) || dirId <= 0)
        {
            _logger.Warn($"[QQMusic] 收藏歌单失败：dirId 非法 ({playlistId})");
            return false;
        }
        try
        {
            string uin = ExtractUin(cookie);
            string method = subscribe ? "AddPlaySongDir" : "DelPlaySongDir";
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musicasset.Playlist",
                    method,
                    param = new { dirId, uin, cleanMode = 0 },
                },
                comm = new { ct = 24, cv = 0, uin },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return false;
            int code = req0.TryGetProperty("code", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.Number
                ? cEl.GetInt32() : -1;
            return code == 0;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 收藏歌单失败", e);
            return false;
        }
    }

    /// <summary>D2.1：QQ 音乐新建歌单（Playlist/CreatePlaylistDir；dirName 为空返回 null）。成功返回新建 dirId（十进制字符串）。</summary>
    public async Task<string?> CreatePlaylistAsync(string name, bool privacy = false, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(cookie))
            return null;
        try
        {
            string uin = ExtractUin(cookie);
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musicasset.Playlist",
                    method = "CreatePlaylistDir",
                    param = new
                    {
                        dirName = name,
                        uin,
                        // QQ 端 0 通常继承用户默认权限；1=私有
                        privacy = privacy ? 1 : 0,
                    }
                },
                comm = new { ct = 24, cv = 0, uin },
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return null;
            int code = req0.TryGetProperty("code", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.Number && cEl.TryGetInt32(out int c2V) ? c2V : -1;
            if (code != 0)
                return null;
            if (!req0.TryGetProperty("data", out JsonElement data))
                return null;

            if (data.TryGetProperty("dirId", out JsonElement didEl))
            {
                if (didEl.ValueKind == JsonValueKind.Number)
                    return didEl.GetInt64().ToString();
                if (didEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(didEl.GetString()))
                    return didEl.GetString();
            }
            if (data.TryGetProperty("id", out JsonElement idEl))
            {
                if (idEl.ValueKind == JsonValueKind.Number)
                    return idEl.GetInt64().ToString();
                if (idEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(idEl.GetString()))
                    return idEl.GetString();
            }
            // 兜底：部分后端把新建 dir 放到 v_playlist 最后一项
            if (data.TryGetProperty("v_playlist", out JsonElement plArr) && plArr.ValueKind == JsonValueKind.Array && plArr.GetArrayLength() > 0)
            {
                JsonElement last = plArr.EnumerateArray().Last();
                if (last.TryGetProperty("dirId", out JsonElement d))
                    return d.ValueKind == JsonValueKind.Number ? d.GetInt64().ToString() : d.GetString();
            }
            return null;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 新建歌单失败", e);
            return null;
        }
    }

    /// <summary>D2.1：QQ 音乐删除自建歌单（Playlist/DelPlaylistDir）。</summary>
    public async Task<bool> DeletePlaylistAsync(string playlistId, string cookie = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cookie))
            return false;
        if (!long.TryParse(playlistId, out long dirId) || dirId <= 0)
            return false;
        try
        {
            string uin = ExtractUin(cookie);
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musicasset.Playlist",
                    method = "DelPlaylistDir",
                    param = new { dirId, uin }
                },
                comm = new { ct = 24, cv = 0, uin }
            };
            JsonElement json = await PostMusicuAsync(JsonSerializer.Serialize(payload), cookie, ct);
            if (!json.TryGetProperty("req_0", out JsonElement req0))
                return false;
            int code = req0.TryGetProperty("code", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.Number && cEl.TryGetInt32(out int c2V) ? c2V : -1;
            return code == 0;
        }
        catch (Exception e)
        {
            _logger.Error("[QQMusic] 删除歌单失败", e);
            return false;
        }
    }

    public void Dispose() => _http?.Dispose();
}
