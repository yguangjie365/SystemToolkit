using System.IO;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Services;
using SystemToolkit.Infrastructure.Steam;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// 批次 4（二）在线库存：解析 / 合并 / 降级四态 + API Key 存储（DPAPI）。
/// <para>
/// 全部用例**零网络**：网络层只覆盖"能不发就不发"的短路分支（无 Steam / 无 Key / 无账户 ID），
/// 真实外呼（URL 形状、401、隐私返回空）留给真机验证与手动刷新——不把测试套件绑到外网上。
/// </para>
/// </summary>
public class SteamOnlineInventoryTests
{
    /// <summary>官方 <c>GetOwnedGames</c> 的真实响应形状（字段名照官方文档，含一条无 name 的条目）。</summary>
    private const string SampleJson = """
    {"response":{"game_count":3,"games":[
      {"appid":570,"name":"Dota 2","playtime_forever":12345,"rtime_last_played":1700000000},
      {"appid":2358720,"name":"Black Myth: Wukong","playtime_forever":600,"rtime_last_played":1690000000},
      {"appid":480}
    ]}}
    """;

    // ==================== 解析 ====================

    [Fact]
    public void ParseOwnedGames_Sample_ParsesAllFields()
    {
        SteamOnlineInventoryResult r = SteamService.ParseOwnedGames(SampleJson);

        Assert.True(r.Ok);
        Assert.Equal(3, r.Games.Count);
        SteamInventoryGame dota = r.Games.Single(g => g.AppId == 570);
        Assert.Equal("Dota 2", dota.Name);
        Assert.Equal(12345UL, dota.PlaytimeMinutes);
        Assert.Equal(1700000000L, dota.LastPlayed);
        Assert.False(dota.Installed); // 在线结果一律先按"未安装"，由本地 .acf 覆盖
    }

    [Fact]
    public void ParseOwnedGames_MissingName_FallsBackToAppId()
    {
        SteamOnlineInventoryResult r = SteamService.ParseOwnedGames(SampleJson);

        Assert.Equal("App 480", r.Games.Single(g => g.AppId == 480).Name);
    }

    /// <summary>
    /// 🔴 隐私「游戏详情」非公开时的真实形状：**HTTP 200 但 <c>games</c> 缺失**。
    /// 必须判为失败（带可操作提示），不得当作"该账号没有游戏"。
    /// </summary>
    [Fact]
    public void ParseOwnedGames_PrivateProfile_IsFailureWithActionableHint()
    {
        SteamOnlineInventoryResult r = SteamService.ParseOwnedGames("""{"response":{}}""");

        Assert.False(r.Ok);
        Assert.True(r.EmptyResult);
        Assert.Contains("隐私", r.Error!);
        Assert.Contains("公开", r.Error!);
    }

    [Fact]
    public void ParseOwnedGames_EmptyGamesArray_IsEmptyResult()
    {
        SteamOnlineInventoryResult r = SteamService.ParseOwnedGames("""{"response":{"game_count":0}}""");

        Assert.False(r.Ok);
        Assert.True(r.EmptyResult);
    }

    [Fact]
    public void ParseOwnedGames_InvalidJson_FailsWithoutThrowing()
    {
        SteamOnlineInventoryResult r = SteamService.ParseOwnedGames("{not json");

        Assert.False(r.Ok);
        Assert.False(r.EmptyResult);
        Assert.Contains("解析失败", r.Error!);
    }

    /// <summary>单条结构异常只跳这一条——不因一条脏数据丢掉整份库存。</summary>
    [Fact]
    public void ParseOwnedGames_MalformedItem_SkippedOthersKept()
    {
        const string json = """
        {"response":{"games":[
          {"appid":"not-a-number","name":"Bad"},
          {"name":"No AppId"},
          {"appid":0,"name":"Zero"},
          {"appid":620,"name":"Portal 2"}
        ]}}
        """;

        SteamOnlineInventoryResult r = SteamService.ParseOwnedGames(json);

        Assert.True(r.Ok);
        Assert.Single(r.Games);
        Assert.Equal(620u, r.Games[0].AppId);
    }

    // ==================== 合并（本地优先） ====================

    private static SteamInventorySnapshot LocalWith(params SteamInventoryGame[] games) => new()
    {
        Source = SteamInventorySource.LocalCache,
        Games = games,
        Stats = new SteamInventoryStats { Total = games.Length, Installed = games.Count(g => g.Installed) },
    };

    [Fact]
    public void MergeInventory_LocalInstalledEntry_KeepsLocalFields_AndTakesMaxPlaytime()
    {
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame
        {
            AppId = 570,
            Name = "Dota 2",
            Installed = true,
            PlaytimeMinutes = 100,
            SizeOnDisk = 40_000,
            InstallDir = "dota 2 beta",
            LibraryPath = @"F:\SteamLibrary",
        });
        SteamInventoryGame[] online = new[]
        {
            new SteamInventoryGame { AppId = 570, Name = "Dota 2", PlaytimeMinutes = 12345, LastPlayed = 1700000000 },
        };

        SteamInventorySnapshot merged = SteamService.MergeInventory(local, online);
        SteamInventoryGame g = merged.Games.Single();

        Assert.Equal(SteamInventorySource.Online, merged.Source);
        Assert.True(g.Installed);
        Assert.Equal(40_000UL, g.SizeOnDisk);
        Assert.Equal("dota 2 beta", g.InstallDir);
        Assert.Equal(@"F:\SteamLibrary", g.LibraryPath);
        Assert.Equal(12345UL, g.PlaytimeMinutes); // max(100, 12345)
        Assert.Equal(1700000000L, g.LastPlayed);
    }

    [Fact]
    public void MergeInventory_OnlineOnlyEntry_IsAddedAsNotInstalled()
    {
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame { AppId = 570, Name = "Dota 2", Installed = true });
        SteamInventoryGame[] online = new[] { new SteamInventoryGame { AppId = 620, Name = "Portal 2", PlaytimeMinutes = 30 } };

        SteamInventorySnapshot merged = SteamService.MergeInventory(local, online);

        Assert.Equal(2, merged.Games.Count);
        Assert.Equal(2, merged.Stats.Total);
        Assert.Equal(1, merged.Stats.Installed);
        Assert.Equal(1, merged.Stats.NotInstalled);
        SteamInventoryGame portal = merged.Games.Single(g => g.AppId == 620);
        Assert.False(portal.Installed);
        Assert.Equal(30UL, portal.PlaytimeMinutes);
    }

    /// <summary>本地只有兜底名（appinfo 也没读到名）时，用在线真名补——反之不动本地名。</summary>
    [Fact]
    public void MergeInventory_FallbackNameReplaced_RealNameKept()
    {
        SteamInventorySnapshot local = LocalWith(
            new SteamInventoryGame { AppId = 111, Name = "App 111", Installed = true },
            new SteamInventoryGame { AppId = 222, Name = "黑神话：悟空", Installed = true });
        SteamInventoryGame[] online = new[]
        {
            new SteamInventoryGame { AppId = 111, Name = "Half-Life" },
            new SteamInventoryGame { AppId = 222, Name = "Black Myth: Wukong" },
        };

        SteamInventorySnapshot merged = SteamService.MergeInventory(local, online);

        Assert.Equal("Half-Life", merged.Games.Single(g => g.AppId == 111).Name);
        Assert.Equal("黑神话：悟空", merged.Games.Single(g => g.AppId == 222).Name);
    }

    /// <summary>Valve 自带条目即使出现在在线清单里也要剔掉（在线条目拿不到 appinfo 类型 → 只能靠 AppID 黑名单）。</summary>
    [Fact]
    public void MergeInventory_OnlineOnlyValveTestApp_IsFiltered()
    {
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame { AppId = 570, Name = "Dota 2", Installed = true });
        SteamInventoryGame[] online = new[] { new SteamInventoryGame { AppId = 480, Name = "Spacewar" } };

        SteamInventorySnapshot merged = SteamService.MergeInventory(local, online);

        Assert.Single(merged.Games);
        Assert.DoesNotContain(merged.Games, g => g.AppId == 480);
    }

    // ==================== 降级四态 ====================

    [Fact]
    public void ComposeInventory_OnlineOk_MarksOnline()
    {
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame { AppId = 570, Name = "Dota 2", Installed = true });
        SteamInventorySnapshot composed = SteamService.ComposeInventory(
            local, SteamOnlineInventoryResult.Success([new SteamInventoryGame { AppId = 620, Name = "Portal 2" }]));

        Assert.Equal(SteamInventorySource.Online, composed.Source);
        Assert.Null(composed.Error);
        Assert.Equal(2, composed.Games.Count);
    }

    [Fact]
    public void ComposeInventory_OnlineFailure_KeepsLocalAndFillsError()
    {
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame { AppId = 570, Name = "Dota 2", Installed = true });
        SteamInventorySnapshot composed = SteamService.ComposeInventory(
            local, SteamOnlineInventoryResult.Failure("在线库存请求超时（上界 10 秒）"));

        Assert.Equal(SteamInventorySource.LocalCache, composed.Source);
        Assert.Equal(local.Games, composed.Games); // 本地数据一字不动
        Assert.Contains("超时", composed.Error!);
    }

    [Fact]
    public void ComposeInventory_EmptyOnlineWithLocalGames_ReportsActionableError()
    {
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame { AppId = 570, Name = "Dota 2", Installed = true });
        SteamInventorySnapshot composed = SteamService.ComposeInventory(
            local, SteamOnlineInventoryResult.Empty("在线返回空列表：请把…设为公开"));

        Assert.Equal(SteamInventorySource.LocalCache, composed.Source);
        Assert.Contains("公开", composed.Error!);
    }

    /// <summary>本地也是空的 → "在线返回空"不是故障，不该给用户加错误噪声。</summary>
    [Fact]
    public void ComposeInventory_EmptyOnlineAndEmptyLocal_NoErrorNoise()
    {
        SteamInventorySnapshot local = new() { Source = SteamInventorySource.LocalCache };
        SteamInventorySnapshot composed = SteamService.ComposeInventory(
            local, SteamOnlineInventoryResult.Empty("在线返回空列表"));

        Assert.Null(composed.Error);
    }

    // ==================== 编排：能不发就不发 ====================

    [Fact]
    public async Task EnhanceInventory_NoSteam_ReturnsLocalWithoutNetwork()
    {
        var svc = new SteamService();
        SteamInventorySnapshot local = new(); // Source = None

        SteamInventorySnapshot result = await svc.EnhanceInventoryAsync(local, "FAKE-KEY", "76561190000000000");

        Assert.Equal(SteamInventorySource.None, result.Source);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task EnhanceInventory_NoApiKey_ReturnsLocalWithoutNetwork()
    {
        var svc = new SteamService();
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame { AppId = 570, Name = "Dota 2", Installed = true });

        SteamInventorySnapshot result = await svc.EnhanceInventoryAsync(local, null, "76561190000000000");

        Assert.Equal(SteamInventorySource.LocalCache, result.Source);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task EnhanceInventory_MissingSteamId_ReportsErrorWithoutNetwork()
    {
        var svc = new SteamService();
        SteamInventorySnapshot local = LocalWith(new SteamInventoryGame { AppId = 570, Name = "Dota 2", Installed = true });

        SteamInventorySnapshot result = await svc.EnhanceInventoryAsync(local, "FAKE-KEY", "  ");

        Assert.Equal(SteamInventorySource.LocalCache, result.Source);
        Assert.Contains("账户 ID", result.Error!);
    }

    // ==================== API Key 存储（DPAPI） ====================

    private static string NewTempKeyPath() =>
        Path.Combine(Path.GetTempPath(), $"stk_apikey_{Guid.NewGuid():N}", "webapi-key.json");

    private static void Cleanup(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApiKeyStore_RoundTrip_AndCiphertextIsNotPlaintext()
    {
        string path = NewTempKeyPath();
        try
        {
            var store = new DpapiSteamApiKeyStore(path);
            Assert.Null(store.Get()); // 未存储 = null（不是空串）

            store.Set("ABCDEF0123456789ABCDEF0123456789");
            Assert.Equal("ABCDEF0123456789ABCDEF0123456789", store.Get());

            // 🔴 红线：落盘内容不得含明文 Key
            string raw = File.ReadAllText(path);
            Assert.DoesNotContain("ABCDEF0123456789", raw, StringComparison.Ordinal);

            store.Clear();
            Assert.Null(store.Get());
            Assert.False(File.Exists(path)); // 清除 = 删文件（不是写空串）
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ApiKeyStore_SetWhitespace_ActsAsClear()
    {
        string path = NewTempKeyPath();
        try
        {
            var store = new DpapiSteamApiKeyStore(path);
            store.Set("SOME-KEY");
            Assert.NotNull(store.Get());

            store.Set("   "); // 契约：空白 = 清除（避免"存了空串"这种自相矛盾状态）
            Assert.Null(store.Get());
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ApiKeyStore_TrimsInput()
    {
        string path = NewTempKeyPath();
        try
        {
            var store = new DpapiSteamApiKeyStore(path);
            store.Set("  KEY-WITH-SPACES  ");

            Assert.Equal("KEY-WITH-SPACES", store.Get());
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>坏文件（改机 / profile 变更 / 手改）按"未配置"处理，且删掉坏条目避免每次都撞。</summary>
    [Fact]
    public void ApiKeyStore_CorruptFile_TreatedAsUnsetAndRemoved()
    {
        string path = NewTempKeyPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not valid json");
            var store = new DpapiSteamApiKeyStore(path);

            Assert.Null(store.Get());
            Assert.False(File.Exists(path));

            store.Set("RECOVERED-KEY"); // 坏文件不该让后续写入失败
            Assert.Equal("RECOVERED-KEY", store.Get());
        }
        finally
        {
            Cleanup(path);
        }
    }
}
