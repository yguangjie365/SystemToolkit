using System.Text.Json;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Infrastructure.Music.Online;

namespace SystemToolkit.Tests.Music.Online;

/// <summary>
/// OM-1：DPAPI 凭据存储往返 + 二维码状态便捷属性回归。
/// DPAPI CurrentUser 作用域在测试宿主（同用户）下可用——round-trip 真实走加密/解密。
/// </summary>
public class OnlineCredentialStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"om1-cred-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void SetThenGet_RoundTripsCookie_PlaintextNeverOnDisk()
    {
        var store = new DpapiOnlineCredentialStore(_path);
        const string cookie = "MUSIC_U=abc123def; __csrf=test-csrf; NMTID=xyz";

        store.SetCookie(OnlineProvider.NetEase, cookie);
        string? actual = store.GetCookie(OnlineProvider.NetEase);

        Assert.NotNull(actual);
        Assert.Equal(cookie, actual);

        // 🔴 红线验证：磁盘文件不得含明文 Cookie
        string raw = File.ReadAllText(_path);
        Assert.DoesNotContain("abc123def", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("test-csrf", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Providers_AreIsolated()
    {
        var store = new DpapiOnlineCredentialStore(_path);
        store.SetCookie(OnlineProvider.NetEase, "MUSIC_U=netease-cookie");
        store.SetCookie(OnlineProvider.QQMusic, "uin=12345; qqmusic_key=Q_H_L_abcdef");

        Assert.Equal("MUSIC_U=netease-cookie", store.GetCookie(OnlineProvider.NetEase));
        Assert.Equal("uin=12345; qqmusic_key=Q_H_L_abcdef", store.GetCookie(OnlineProvider.QQMusic));

        store.Clear(OnlineProvider.NetEase);
        Assert.Null(store.GetCookie(OnlineProvider.NetEase));
        Assert.Equal("uin=12345; qqmusic_key=Q_H_L_abcdef", store.GetCookie(OnlineProvider.QQMusic));
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var store = new DpapiOnlineCredentialStore(_path);
        Assert.Null(store.GetCookie(OnlineProvider.NetEase));
    }

    [Fact]
    public void CorruptedEntry_ReturnsNullAndSelfHeals()
    {
        var store = new DpapiOnlineCredentialStore(_path);
        store.SetCookie(OnlineProvider.NetEase, "MUSIC_U=good");

        // 模拟换机/损坏：直接把密文字段写坏
        Dictionary<string, Dictionary<string, string>>? payload = JsonSerializer.Deserialize<
            Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_path));
        Assert.NotNull(payload);
        payload!["ProtectedCookies"]!["netease"] = "!!!not-base64!!!";
        File.WriteAllText(_path, JsonSerializer.Serialize(payload));

        Assert.Null(store.GetCookie(OnlineProvider.NetEase));

        // 自愈：坏条目已被移除，重新写入正常
        store.SetCookie(OnlineProvider.NetEase, "MUSIC_U=fresh");
        Assert.Equal("MUSIC_U=fresh", store.GetCookie(OnlineProvider.NetEase));
    }

    [Theory]
    [InlineData(803, true, false, false)]
    [InlineData(802, false, true, false)]
    [InlineData(801, false, true, false)]
    [InlineData(800, false, false, true)]
    public void QrCheckResult_StateFlags_MapByCode(int code, bool success, bool waiting, bool expired)
    {
        var result = new OnlineQrCheckResult { Code = code };

        Assert.Equal(success, result.Success);
        Assert.Equal(waiting, result.Waiting);
        Assert.Equal(expired, result.Expired);
    }
}
