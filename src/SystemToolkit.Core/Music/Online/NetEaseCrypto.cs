using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SystemToolkit.Core.Music.Online;

/// <summary>
/// 网易云音乐 EAPI 加密（A 方案：打包常量密钥）。
/// 移植自 NexBox src-tauri/src/music_api/crypto.rs。
///
/// 合规声明：本模块仅用于个人学习与研究，不存储用户凭据，
/// 所有 API 请求均由用户本人发起，密钥为公开的客户端固定常量，
/// 不涉及任何逆向工程或绕过版权保护。使用者自行承担合规风险。
/// </summary>
public static class NetEaseCrypto
{
    /// <summary>
    /// <para>NexBox 用 serde_json 序列化 JSON 的形态 1:1（BMP 内字符原样写出、只转义引号/反斜杠/控制字符）。
    /// —— System.Text.Json 默认 JavaScriptEncoder 会把引号/单引号/与字符转义成 \uXXXX 形式，
    ///    生成的 header 内嵌字符串完全不同，MD5 会立刻不对。</para>
    ///    服务器端校验 EAPI payload 签名是字符级的，这里必须字节一致。
    /// </summary>
    private static readonly JsonSerializerOptions RustAlignedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>EAPI AES-128-ECB 密钥（公开常量，来自安卓客户端）。</summary>
    private static readonly byte[] EapiKey = "e82ckenh8dichen8"u8.ToArray();

    /// <summary>
    /// EAPI 加密：
    /// 1. digest = MD5("nobody{api_path}use{payload}md5forencrypt")
    /// 2. data = "{api_path}-36cd479b6b5-{payload}-36cd479b6b5-{digest}"
    /// 3. AES-128-ECB 加密 data
    /// 4. 十六进制大写编码
    /// </summary>
    public static string EncryptEapiPayload(string apiPath, string payloadText)
    {
        string digestSource = $"nobody{apiPath}use{payloadText}md5forencrypt";
        string digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(digestSource))).ToLowerInvariant();
        string data = $"{apiPath}-36cd479b6b5-{payloadText}-36cd479b6b5-{digest}";
        byte[] encrypted = AesEcbEncryptPkcs7(Encoding.UTF8.GetBytes(data), EapiKey);
        return Convert.ToHexString(encrypted);
    }

    /// <summary>
    /// 构建 EAPI 请求头 (作为 header 字段注入到加密 payload，同时也按 k=v 字符串化发送 Cookie)。
    /// ⚠️ NexBox 对照 (crypto.rs `build_eapi_header()`):
    ///   buildver = 纯数字 (now_ms/1000), versioncode = 字符串 "140"
    /// 两边类型不相同——如果 Cookie 发送时把 buildver 强转成字符串可以，
    /// 但 payload 里必须与 NexBox 的 JSON 序列化形态 1:1，否则 EAPI MD5 就不对
    /// （"nobody{path}use{payload}md5forencrypt" 会被服务器校验）。
    /// 所以这里返回 JsonElement Map，让调用侧写 JSON 时直接保留原始类型。
    /// </summary>
    public static Dictionary<string, JsonElement> BuildEapiHeaderMap()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string requestId = $"{nowMs}_{Random.Shared.Next(0, 1000):D4}";
        using var doc = JsonDocument.Parse($"{{" +
            $"\"__csrf\":\"\"," +
            $"\"appver\":\"8.0.0\"," +
            $"\"buildver\":{nowMs / 1000}," +
            $"\"channel\":\"\"," +
            $"\"deviceId\":\"\"," +
            $"\"mobilename\":\"\"," +
            $"\"resolution\":\"1920x1080\"," +
            $"\"os\":\"android\"," +
            $"\"osver\":\"\"," +
            $"\"requestId\":\"{requestId}\"," +
            $"\"versioncode\":\"140\"," +
            $"\"MUSIC_U\":\"\"" +
            $"}}");
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    /// <summary>
    /// 把 BuildEapiHeaderMap 的 Map 序列化成 header JSON 字符串
    /// （NexBox: full_payload 的 "header" 字段 = serde_json 序列化的 header 字典）。
    /// </summary>
    public static string SerializeHeaderMapToJsonString(Dictionary<string, JsonElement> header)
    {
        var buffer = new Dictionary<string, object?>(header.Count);
        foreach (KeyValuePair<string, System.Text.Json.JsonElement> kv in header)
        {
            buffer[kv.Key] = kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString(),
                JsonValueKind.Number when kv.Value.TryGetInt64(out long iv) => iv,
                JsonValueKind.Number when kv.Value.TryGetDouble(out double dv) => dv,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        // ⚠️ 一定要 UnsafeRelaxedJsonEscaping；默认编码器会把 " → \u0022，
        // 而 serde_json 只输出 \"，差一个字符 MD5 就判失败，服务器直接 400 参数错误。
        return JsonSerializer.Serialize(buffer, RustAlignedJsonOptions);
    }

    /// <summary>把 BuildEapiHeaderMap 的 Map 转成 k=v 字符串（Set-Cookie 请求头用）。</summary>
    public static string SerializeHeaderMapToCookieString(Dictionary<string, JsonElement> header)
    {
        // NexBox netease.rs L92-L96 对齐：
        //   header.iter().map(|(k,v)| format!("{k}={}", v.as_str().unwrap_or(""))).join("; ")
        // —— 只有真正 String 类型才写值；其他类型（如 buildver 是数字）v.as_str() 返回 None，unwrap 到空串。
        // 之前我们把 buildver 写成 "1756861234" 数字字面量，属于越界差异；
        // 虽然 EAPI MD5 只看 payloadText，但这层 Cookie 也必须字节一致。
        static string AsString(JsonElement v)
            => v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        return string.Join("; ", header.Select(kv => $"{kv.Key}={AsString(kv.Value)}"));
    }

    /// <summary>
    /// 诊断：返回 EncryptEapiPayload 的所有中间产物，方便与 NexBox Rust 版逐字节对比。
    /// 一旦服务器 400 就可以用同 requestId/now_ms 两端跑一遍，立刻找出差异处。
    /// </summary>
    public static (string payloadText, string digestSource, string digest, string data, string encryptedHex)
        ProbeEncryptEapiPayload(string apiPath, string payloadText)
    {
        string digestSource = $"nobody{apiPath}use{payloadText}md5forencrypt";
        string digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(digestSource))).ToLowerInvariant();
        string data = $"{apiPath}-36cd479b6b5-{payloadText}-36cd479b6b5-{digest}";
        byte[] encrypted = AesEcbEncryptPkcs7(Encoding.UTF8.GetBytes(data), EapiKey);
        return (payloadText, digestSource, digest, data, Convert.ToHexString(encrypted));
    }

    /// <summary>AES-128-ECB 加密 (PKCS7 填充)。</summary>
    private static byte[] AesEcbEncryptPkcs7(byte[] input, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }

    // ════════════ WEAPI（对照 NexBox crypto.rs:67-130，NeteaseCloudMusicApi 标准算法）════════════

    private static readonly byte[] WeapiPresetKey = "0CoJUm6Qyw8W8jud"u8.ToArray();
    private static readonly byte[] WeapiIv = "0102030405060708"u8.ToArray();
    /// <summary>weapi 公钥模数（1024 位，官方 128 字节，无前导零）。</summary>
    private const string WeapiModulusHex =
        "e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";
    private const long WeapiE = 65537;

    /// <summary>AES-128-CBC 加密 (PKCS7 填充)。</summary>
    private static byte[] AesCbcEncryptPkcs7(byte[] input, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }

    /// <summary>裸 RSA 加密（无填充，node-forge 'NONE' 行为）：入参为<strong>大端</strong>字节（即逆序 secretKey），c = m^e mod n，输出 128 字节大写 hex。</summary>
    private static string WeapiRsaEncrypt(byte[] bigEndianBytes)
    {
        var n = System.Numerics.BigInteger.Parse(WeapiModulusHex,
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var e = new System.Numerics.BigInteger(WeapiE);
        // BigInteger(byte[]) 按小端解析：翻转为小端，并在最高位 ≥0x80 时补 0x00 防止被解释成负数。
        byte[] le = bigEndianBytes.Reverse().ToArray();
        if (le.Length > 0 && le[^1] >= 0x80)
        {
            byte[] padded = new byte[le.Length + 1];
            le.CopyTo(padded, 0);
            le = padded;
        }
        var m = new System.Numerics.BigInteger(le);
        var c = System.Numerics.BigInteger.ModPow(m, e, n);

        byte[] cLe = c.ToByteArray();
        Array.Reverse(cLe); // 转大端
        if (cLe.Length > 0 && cLe[0] == 0)
        {
            cLe = cLe[1..]; // 去掉符号补位
        }
        byte[] result = new byte[128];
        cLe.CopyTo(result, 128 - cLe.Length);
        return Convert.ToHexString(result);
    }

    /// <summary>weapi 随机 16 位 secretKey（base62）。fixedSecret 仅供测试注入。</summary>
    private static string WeapiRandomSecretKey(string? fixedSecret = null)
    {
        if (fixedSecret is not null)
        {
            return fixedSecret;
        }
        const string Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 16; i++)
        {
            sb.Append(Chars[Random.Shared.Next(Chars.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 网易云 weapi 加密：① AES-CBC(presetKey) 加密 payload → base64；
    /// ② AES-CBC(随机 secretKey) 加密第一层 base64 → base64 = params；
    /// ③ encSecKey = 裸 RSA 加密 secretKey 逆序。返回 (params, encSecKey)。
    /// </summary>
    public static (string Params, string EncSecKey) EncryptWeapiPayload(string payloadText, string? fixedSecret = null)
    {
        string secret = WeapiRandomSecretKey(fixedSecret);
        byte[] inner = AesCbcEncryptPkcs7(Encoding.UTF8.GetBytes(payloadText), WeapiPresetKey, WeapiIv);
        string innerB64 = Convert.ToBase64String(inner);
        byte[] paramsBytes = AesCbcEncryptPkcs7(Encoding.UTF8.GetBytes(innerB64), Encoding.ASCII.GetBytes(secret), WeapiIv);
        string @params = Convert.ToBase64String(paramsBytes);
        string encSecKey = WeapiRsaEncrypt(Encoding.ASCII.GetBytes(secret).Reverse().ToArray());
        return (@params, encSecKey);
    }
}
