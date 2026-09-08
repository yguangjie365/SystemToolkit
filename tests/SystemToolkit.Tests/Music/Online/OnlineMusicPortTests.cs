using System.Text.Json;
using SystemToolkit.Core.Music.Online;

namespace SystemToolkit.Tests.Music.Online;

/// <summary>OM-0 移植回归：在线模型序列化往返 + 网易云 EAPI 加密的确定性。</summary>
public class OnlineMusicPortTests
{
    [Fact]
    public void OnlineTrack_JsonRoundTrip_PreservesProviderAndFields()
    {
        var track = new OnlineTrack
        {
            Provider = OnlineProvider.QQMusic,
            Id = "0039MnYb0qxYhV",
            Mid = "0039MnYb0qxYhV",
            MediaMid = "0039MnYb0qxYhV",
            Name = "晴天",
            Artist = "周杰伦",
            Album = "叶惠美",
            Cover = "https://example.com/cover.jpg",
            DurationMs = 269_000,
            Fee = 1,
            Playable = true,
            QqSongId = 97773,
        };

        string json = JsonSerializer.Serialize(track);
        OnlineTrack? parsed = JsonSerializer.Deserialize<OnlineTrack>(json);

        Assert.NotNull(parsed);
        Assert.Equal(track, parsed); // record 值相等——序列化往返无损
    }

    [Fact]
    public void NetEaseCrypto_EncryptEapiPayload_IsDeterministicAndHexShaped()
    {
        string payload = "{\"id\":\"33894312\"}";

        string first = NetEaseCrypto.EncryptEapiPayload("/api/song/enhance/player/url", payload);
        string second = NetEaseCrypto.EncryptEapiPayload("/api/song/enhance/player/url", payload);

        // 确定性：同输入两次加密输出一致（签名链的根基）
        Assert.Equal(first, second);

        // 形态：AES 密文的十六进制大写文本（EAPI data 字段的本体）
        Assert.Matches("^[0-9A-F]+$", first);
    }

    [Fact]
    public void NetEaseCrypto_SerializeHeaderMapToJsonString_AvoidsUnicodeEscaping()
    {
        Dictionary<string, JsonElement> header = NetEaseCrypto.BuildEapiHeaderMap();

        string json = NetEaseCrypto.SerializeHeaderMapToJsonString(header);

        // 与 NexBox serde_json 对齐的关键：中文/特殊字符不做 \uXXXX 转义（否则 MD5 签名错）
        Assert.DoesNotContain("\\u", json, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("{", json, StringComparison.Ordinal);
    }
}
