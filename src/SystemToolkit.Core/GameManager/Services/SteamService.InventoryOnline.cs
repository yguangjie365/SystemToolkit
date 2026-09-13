using System.Globalization;
using System.Text.Json;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 客户端管理服务 —— <b>库存（B2 在线部分）</b> 分册（批次 4，2026-09-13）。
/// <para>
/// 本地源（<see cref="ScanInventoryLocal"/>）只能看到"本机留下痕迹"的游戏；完整库存是账号维度的，
/// 只能走 Steam 官方 Web API 并**自带 API Key**——免 Key 的三条匿名路径 2026-09-13 实测全不通
/// （社区列表页重定向到登录页、旧 <c>?xml=1</c> 端点同样、<c>GetOwnedGames</c> 无 key 返 HTTP 401）。
/// </para>
/// <para>
/// 🔴 <b>本地为底、在线为可选增强</b>（设计 §3 限定①）：本分册所有失败路径都**不抛异常**，
/// 只回填 <see cref="SteamInventorySnapshot.Error"/> 并保留本地结果；调用方把 <c>Error</c> 如实展示，
/// 绝不把本地数据当完整库呈现（限定②）。
/// </para>
/// </summary>
public sealed partial class SteamService
{
    /// <summary>Steam 官方 Web API 基址。</summary>
    private const string SteamApiBase = "https://api.steampowered.com";

    /// <summary>
    /// 在线请求超时上界（设计 §3 限定③）。取 10s：官方接口返回体可达数百 KB（数百款游戏），
    /// 头像用的 5s 偏紧；同时**不做重试**——失败即降级，用户可手动刷新。
    /// </summary>
    private static readonly TimeSpan OnlineInventoryTimeout = TimeSpan.FromSeconds(10);

    private HttpClient? _apiHttp; // 仅在线库存使用，懒初始化（与头像用的 _http 分立：白名单与超时不同）

    /// <summary>
    /// 本地快照 + 可选在线增强 → 最终快照（**VM 唯一入口**）。
    /// <list type="number">
    /// <item>本地无数据（未装 Steam）→ 原样返回，绝不外呼。</item>
    /// <item>未配置 Key → 原样返回（中性：不是错误，由 UI 用"未设置"措辞提示）。</item>
    /// <item>拿不到当前账户 ID → 退回本地 + <c>Error</c> 说明。</item>
    /// <item>在线失败/可疑空 → 保留本地数据 + <c>Error</c> 如实说明原因。</item>
    /// <item>在线成功 → 合并，来源标 <see cref="SteamInventorySource.Online"/>。</item>
    /// </list>
    /// </summary>
    /// <param name="local">本地快照（<see cref="ScanInventoryLocal"/> 的结果）。</param>
    /// <param name="apiKey">API Key；<c>null</c>/空白 = 未配置。</param>
    /// <param name="steamId64">当前激活账户的 SteamID64。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<SteamInventorySnapshot> EnhanceInventoryAsync(
        SteamInventorySnapshot local,
        string? apiKey,
        string? steamId64,
        CancellationToken ct = default)
    {
        if (local.Source == SteamInventorySource.None || string.IsNullOrWhiteSpace(apiKey))
        {
            return local;
        }

        if (string.IsNullOrWhiteSpace(steamId64))
        {
            return local with { Error = "未能确定当前 Steam 账户 ID，已仅显示本机数据" };
        }

        SteamOnlineInventoryResult online =
            await FetchOwnedGamesAsync(apiKey, steamId64, ct).ConfigureAwait(false);
        SteamInventorySnapshot composed = ComposeInventory(local, online);

        if (online.Ok)
        {
            _logger.Info(
                $"在线库存拉取成功：{online.Games.Count} 条（本地 {local.Games.Count} 条 → 合并后 {composed.Games.Count} 条）");
        }
        else
        {
            _logger.Warn($"在线库存拉取失败，已退回本地数据：{online.Error}");
        }

        return composed;
    }

    /// <summary>
    /// 用自带 API Key 拉取账号**完整拥有清单**（含从未在本机安装的游戏）。
    /// <para>
    /// 接口：<c>IPlayerService/GetOwnedGames/v1</c>；<c>include_appinfo=1</c> 才带游戏名，
    /// <c>include_played_free_games=1</c> 才包含"玩过的免费游戏"（否则免费游戏会整批缺失）。
    /// </para>
    /// <para>
    /// 🔴 **不抛异常**：任何失败（超时/断网/401/解析异常）都返回带 <c>Error</c> 的失败结果；
    /// 唯一例外是调用方**主动取消**（<paramref name="ct"/> 已取消）——那时让取消按惯例上抛。
    /// </para>
    /// </summary>
    /// <param name="apiKey">Steam Web API Key。</param>
    /// <param name="steamId64">要查询的账号 SteamID64（来自本地 <c>loginusers.vdf</c> 的激活账户）。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task<SteamOnlineInventoryResult> FetchOwnedGamesAsync(
        string apiKey,
        string steamId64,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64))
        {
            return SteamOnlineInventoryResult.Failure("缺少 API Key 或 Steam 账户 ID");
        }

        string url = $"{SteamApiBase}/IPlayerService/GetOwnedGames/v1/"
            + $"?key={Uri.EscapeDataString(apiKey)}"
            + $"&steamid={Uri.EscapeDataString(steamId64)}"
            + "&include_appinfo=1&include_played_free_games=1&format=json";

        try
        {
            HttpClient client = EnsureApiHttpClient();
            using HttpResponseMessage resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                string hint = resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    ? "Key 无效、已被撤销，或不属于该账户"
                    : "官方接口返回异常状态";
                return SteamOnlineInventoryResult.Failure($"在线库存请求被拒（HTTP {code}）：{hint}");
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseOwnedGames(json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient 超时同样表现为 OCE（但调用方未取消）→ 这是"失败"，不是"用户取消"
            return SteamOnlineInventoryResult.Failure(
                $"在线库存请求超时（上界 {OnlineInventoryTimeout.TotalSeconds:0} 秒）");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方主动取消：不吞
        }
        catch (Exception e)
        {
            return SteamOnlineInventoryResult.Failure($"在线库存请求失败：{e.Message}");
        }
    }

    /// <summary>解析 <c>GetOwnedGames</c> 响应体（拆出来便于单测：本方法不发网络）。</summary>
    /// <param name="json">响应 JSON。</param>
    internal static SteamOnlineInventoryResult ParseOwnedGames(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out JsonElement response)
                || !response.TryGetProperty("games", out JsonElement games)
                || games.ValueKind != JsonValueKind.Array
                || games.GetArrayLength() == 0)
            {
                // 🔴 官方接口在「游戏详情」非公开时**正是这样返回**（HTTP 200 + 无 games）
                return SteamOnlineInventoryResult.Empty(
                    "在线返回空列表：请把 Steam「隐私设置 → 游戏详情」设为公开，并确认 Key 属于当前账户");
            }

            var list = new List<SteamInventoryGame>(games.GetArrayLength());
            foreach (JsonElement item in games.EnumerateArray())
            {
                // 🔴 必须先验 ValueKind：TryGetUInt32 对**类型不符**（如 "appid":"123"）是**抛** InvalidOperationException，
                // 只有越界才返回 false——不验就变成"一条脏数据丢掉整份库存"（本仓实证，测试已钉）
                if (!item.TryGetProperty("appid", out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetUInt32(out uint appId)
                    || appId == 0)
                {
                    continue;
                }

                string name = item.TryGetProperty("name", out JsonElement nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;
                ulong minutes = item.TryGetProperty("playtime_forever", out JsonElement ptElement)
                    && ptElement.ValueKind == JsonValueKind.Number
                    && ptElement.TryGetUInt64(out ulong pt)
                        ? pt
                        : 0;
                long lastPlayed = item.TryGetProperty("rtime_last_played", out JsonElement lpElement)
                    && lpElement.ValueKind == JsonValueKind.Number
                    && lpElement.TryGetInt64(out long lp)
                        ? lp
                        : 0;

                list.Add(new SteamInventoryGame
                {
                    AppId = appId,
                    Name = name.Length > 0 ? name : FallbackName(appId),
                    Installed = false, // 是否已安装由本地 .acf 说了算（合并时本地条目覆盖）
                    PlaytimeMinutes = minutes,
                    LastPlayed = lastPlayed,
                });
            }

            return list.Count == 0
                ? SteamOnlineInventoryResult.Failure("在线返回的记录均无有效 AppID，已忽略")
                : SteamOnlineInventoryResult.Success(list);
        }
        catch (JsonException e)
        {
            return SteamOnlineInventoryResult.Failure("在线响应解析失败：" + e.Message);
        }
    }

    /// <summary>
    /// 纯决策：把「本地快照 + 在线结果」折成最终快照与来源标注。**不含 IO**，便于逐态单测。
    /// </summary>
    /// <param name="local">本地快照（已假定非 <see cref="SteamInventorySource.None"/>）。</param>
    /// <param name="online">在线结果。</param>
    internal static SteamInventorySnapshot ComposeInventory(
        SteamInventorySnapshot local,
        SteamOnlineInventoryResult online)
    {
        if (online.Ok)
        {
            return MergeInventory(local, online.Games);
        }

        // 账号里一款游戏都没有（本地也无痕迹）时，"在线返回空"不算故障 → 不加错误噪声
        if (local.Games.Count == 0 && online.EmptyResult)
        {
            return local;
        }

        return local with { Error = online.Error ?? "在线数据不可用" };
    }

    /// <summary>
    /// 把在线条目并入本地快照（**纯函数**）。
    /// <para>
    /// 合并口径（本地为底）：<br/>
    /// ① AppID 命中本地 → <b>保留本地全部字段</b>（已安装态/磁盘占用/安装目录/库路径只有本地有），
    /// 仅两处补强：本地名为兜底名 <c>App {id}</c> 且在线有真名 → 用在线名；时长取 **max**（两边同为分钟）。<br/>
    /// ② 仅在线有 → 新增条目（<c>Installed=false</c>），并过一遍 <see cref="IsNonGame"/> 剔除 Valve 自带条目
    /// （在线条目拿不到 appinfo 类型，只能走 AppID 黑名单 + 名称兜底；类型未知按"是游戏"保留）。<br/>
    /// ③ 统计按合并后条目重算，来源标 <see cref="SteamInventorySource.Online"/>。
    /// </para>
    /// </summary>
    /// <param name="local">本地库存快照。</param>
    /// <param name="online">在线条目（成功结果）。</param>
    internal static SteamInventorySnapshot MergeInventory(
        SteamInventorySnapshot local,
        IReadOnlyList<SteamInventoryGame> online)
    {
        var merged = new Dictionary<uint, SteamInventoryGame>(local.Games.Count + online.Count);
        foreach (SteamInventoryGame g in local.Games)
        {
            merged[g.AppId] = g;
        }

        foreach (SteamInventoryGame o in online)
        {
            if (merged.TryGetValue(o.AppId, out SteamInventoryGame? localEntry))
            {
                bool localNameIsFallback = string.Equals(
                    localEntry.Name, FallbackName(o.AppId), StringComparison.Ordinal);
                merged[o.AppId] = localEntry with
                {
                    Name = localNameIsFallback && o.Name.Length > 0 ? o.Name : localEntry.Name,
                    PlaytimeMinutes = Math.Max(localEntry.PlaytimeMinutes, o.PlaytimeMinutes),
                    LastPlayed = Math.Max(localEntry.LastPlayed, o.LastPlayed),
                };
            }
            else if (!IsNonGame(o.AppId, o.Name))
            {
                merged[o.AppId] = o;
            }
        }

        var games = merged.Values
            .OrderByDescending(g => g.LastPlayed)
            .ThenBy(g => g.AppId)
            .ToList();

        int installedCount = 0;
        ulong totalPlaytime = 0;
        foreach (SteamInventoryGame g in games)
        {
            if (g.Installed)
            {
                installedCount++;
            }

            totalPlaytime += g.PlaytimeMinutes;
        }

        return local with
        {
            Source = SteamInventorySource.Online,
            Error = null,
            Games = games,
            Stats = new SteamInventoryStats
            {
                Total = games.Count,
                Installed = installedCount,
                NotInstalled = games.Count - installedCount,
                TotalPlaytimeMinutes = totalPlaytime,
            },
        };
    }

    /// <summary>名称缺失时的兜底名（与 <see cref="ScanInventoryLocal"/> 同口径，不猜）。</summary>
    private static string FallbackName(uint appId) =>
        "App " + appId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 懒初始化在线库存用的 <see cref="HttpClient"/>：关闭自动重定向（改 <see cref="RedirectFollowingHandler"/>
    /// 逐跳复验 HTTPS + 域白名单）、限定超时上界、显式 UserAgent（与头像在线兜底同一套纪律）。
    /// </summary>
    private HttpClient EnsureApiHttpClient()
    {
        if (_apiHttp is not null)
        {
            return _apiHttp;
        }

        var handler = new RedirectFollowingHandler(
            new HttpClientHandler { AllowAutoRedirect = false },
            ["steampowered.com"]);
        var client = new HttpClient(handler) { Timeout = OnlineInventoryTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SystemToolkit/1.0 (+SteamOwnedGames)");
        Volatile.Write(ref _apiHttp, client);
        return client;
    }
}
