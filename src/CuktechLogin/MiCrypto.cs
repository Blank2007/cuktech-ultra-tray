using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CuktechLogin;

/// <summary>
/// 小米云 API 的加密原语，逐行对照 tools/token-fetch/fetch_token.py 第 82-226 行。
/// 纯托管实现，零第三方依赖（对应 Python 版里的纯 Python RC4-drop1024）。
/// </summary>
internal static class MiCrypto
{
    private const string AgentChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string DeviceIdChars = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// RC4 流密码 + 丢弃前 1024 字节密钥流
    /// （等价于 pycryptodome `ARC4.new(key)` 之后先 `encrypt(1024 * b"\0")`）。
    /// </summary>
    internal static byte[] Rc4Drop1024(byte[] key, byte[] data)
    {
        if (key.Length == 0)
        {
            throw new ArgumentException("RC4 key 不能为空", nameof(key));
        }

        var s = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        // KSA
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        // 丢弃前 1024 字节密钥流
        int a = 0, b = 0;
        for (var n = 0; n < 1024; n++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
        }

        var output = new byte[data.Length];
        for (var n = 0; n < data.Length; n++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            output[n] = (byte)(data[n] ^ s[(s[a] + s[b]) & 0xFF]);
        }

        return output;
    }

    /// <summary>标准 base64 编码（小米接口不吃 url-safe，因此不能用 Base64Url）。</summary>
    internal static string B64Encode(byte[] raw) => Convert.ToBase64String(raw);

    /// <summary>
    /// 标准 base64 解码，自动补 '='（对应 Python 的 `text + "=" * (-len(text) % 4)`）。
    /// 正常路径只走标准字母表；万一遇到 url-safe 字符才回退替换，避免无谓失败。
    /// </summary>
    internal static byte[] B64Decode(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var pad = -s.Length % 4;
        if (pad > 0)
        {
            s += new string('=', pad);
        }

        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            var urlSafe = s.Replace('-', '+').Replace('_', '/');
            if (urlSafe != s)
            {
                return Convert.FromBase64String(urlSafe);
            }

            throw;
        }
    }

    /// <summary>参考实现的 UA：Android-7.1.1-1.0.0-ONEPLUS A3010-136-&lt;11 位随机&gt; MIIO/&lt;6 位随机&gt;。</summary>
    internal static string GenerateAgent()
        => "Android-7.1.1-1.0.0-ONEPLUS A3010-136-" + RandomString(AgentChars, 11) + " MIIO/" + RandomString(AgentChars, 6);

    internal static string GenerateDeviceId() => RandomString(DeviceIdChars, 16);

    private static string RandomString(string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>base64(8 字节随机 + (millis / 60000) 的 4 字节大端)。</summary>
    internal static string GenerateNonce(long millis)
    {
        var buffer = new byte[12];
        RandomNumberGenerator.Fill(buffer.AsSpan(0, 8));
        var minutes = (uint)(millis / 60000);
        buffer[8] = (byte)(minutes >> 24);
        buffer[9] = (byte)(minutes >> 16);
        buffer[10] = (byte)(minutes >> 8);
        buffer[11] = (byte)minutes;
        return B64Encode(buffer);
    }

    /// <summary>base64(sha256(base64解码(ssecurity) + base64解码(nonce)))。</summary>
    internal static string SignedNonce(string ssecurity, string nonce)
    {
        var key = B64Decode(ssecurity);
        var salt = B64Decode(nonce);
        var buffer = new byte[key.Length + salt.Length];
        Buffer.BlockCopy(key, 0, buffer, 0, key.Length);
        Buffer.BlockCopy(salt, 0, buffer, key.Length, salt.Length);
        return B64Encode(SHA256.HashData(buffer));
    }

    /// <summary>base64(RC4-drop1024(base64解码(signed_nonce), utf8(payload)))。</summary>
    internal static string EncryptRc4(string signedNonceB64, string payload)
        => B64Encode(Rc4Drop1024(B64Decode(signedNonceB64), Encoding.UTF8.GetBytes(payload)));

    /// <summary>utf8(RC4-drop1024(base64解码(signed_nonce), base64解码(payload)))，非法字节按替换符处理。</summary>
    internal static string DecryptRc4(string signedNonceB64, string payloadB64)
        => Encoding.UTF8.GetString(Rc4Drop1024(B64Decode(signedNonceB64), B64Decode(payloadB64)));

    /// <summary>
    /// SHA1 签名：`METHOD&amp;path&amp;k=v...&amp;signed_nonce`。
    /// path 取 `url.split("com")[1]` 再去掉 `/app/` 前缀（等价实现见 <see cref="ApiPathPart"/>）。
    /// </summary>
    internal static string GenerateEncSignature(
        string url,
        string method,
        string sn,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var parts = new List<string> { method.ToUpperInvariant(), ApiPathPart(url) };
        foreach (var kv in parameters)
        {
            parts.Add(kv.Key + "=" + kv.Value);
        }

        parts.Add(sn);
        return B64Encode(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("&", parts))));
    }

    /// <summary>
    /// Python `url.split("com")[1]` 的等价实现：取第一个 "com" 之后、第二个 "com" 之前的子串，
    /// 再把 `/app/` 换成 `/`。参数顺序必须保持插入顺序，签名对顺序敏感。
    /// </summary>
    internal static string ApiPathPart(string url)
    {
        var first = url.IndexOf("com", StringComparison.Ordinal);
        if (first < 0)
        {
            return url;
        }

        var start = first + 3;
        var second = url.IndexOf("com", start, StringComparison.Ordinal);
        var slice = second < 0 ? url[start..] : url[start..second];
        return slice.Replace("/app/", "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 两次签名 + 整体加密：
    /// 1) 先给明文参数集（data）算 rc4_hash__；
    /// 2) 把每个值 RC4 加密；
    /// 3) 对加密后的参数集再算一次 signature；
    /// 4) 附上 ssecurity / _nonce（这两个不加密）。
    /// 返回顺序即最终 query string 顺序：data, rc4_hash__, signature, ssecurity, _nonce。
    /// </summary>
    internal static List<KeyValuePair<string, string>> GenerateEncParams(
        string url,
        string method,
        string sn,
        string nonce,
        IEnumerable<KeyValuePair<string, string>> parameters,
        string ssecurity)
    {
        var fields = new List<KeyValuePair<string, string>>(parameters);
        fields.Add(new KeyValuePair<string, string>("rc4_hash__", GenerateEncSignature(url, method, sn, fields)));

        for (var i = 0; i < fields.Count; i++)
        {
            fields[i] = new KeyValuePair<string, string>(fields[i].Key, EncryptRc4(sn, fields[i].Value));
        }

        fields.Add(new KeyValuePair<string, string>("signature", GenerateEncSignature(url, method, sn, fields)));
        fields.Add(new KeyValuePair<string, string>("ssecurity", ssecurity));
        fields.Add(new KeyValuePair<string, string>("_nonce", nonce));
        return fields;
    }

    /// <summary>
    /// 把 24 位 hex 或 base64 统一规范成小写 hex（对应 key_to_hex）。
    /// 24 位 hex 原样返回（= 12 字节）；否则按 base64 解码再转 hex；都失败返回 null。
    /// </summary>
    internal static string? KeyToHex(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var s = key.Trim();
        if (s.Length == 24 && s.All(IsHexDigit))
        {
            return s.ToLowerInvariant();
        }

        byte[] raw;
        try
        {
            raw = B64Decode(s);
        }
        catch (FormatException)
        {
            return null;
        }

        return raw.Length == 0 ? null : Convert.ToHexString(raw).ToLowerInvariant();
    }

    private static bool IsHexDigit(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>
    /// 把 blt_get_beaconkey 的 result 里的字符串字段全部规范化成 hex（对应 parse_fields）。
    /// Best 优先选 12 字节（24 位 hex）的字段，字段名偏好 key &gt; token &gt; ble_key &gt; beaconkey。
    /// </summary>
    internal static (
        Dictionary<string, (string Hex, int Bytes)> Candidates,
        (string Field, string Hex)? Best) ParseFields(JsonElement inner)
    {
        var candidates = new Dictionary<string, (string Hex, int Bytes)>(StringComparer.Ordinal);
        if (inner.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in inner.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var hex = KeyToHex(prop.Value.GetString());
                if (hex == null)
                {
                    continue;
                }

                candidates[prop.Name] = (hex, hex.Length / 2);
            }
        }

        foreach (var prefer in new[] { "key", "token", "ble_key", "beaconkey" })
        {
            if (candidates.TryGetValue(prefer, out var candidate) && candidate.Bytes == 12)
            {
                return (candidates, (prefer, candidate.Hex));
            }
        }

        foreach (var kv in candidates)
        {
            if (kv.Value.Bytes == 12)
            {
                return (candidates, (kv.Key, kv.Value.Hex));
            }
        }

        if (candidates.Count > 0)
        {
            var first = candidates.First();
            return (candidates, (first.Key, first.Value.Hex));
        }

        return (candidates, null);
    }

    /// <summary>把 JSON 里可能出现的数字/字符串统一成字符串（用于 log 与参数拼接）。</summary>
    internal static string ToFlatString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => element.GetRawText(),
    };

    /// <summary>把字符串当成 JSON token 写回（纯数字写数字，其它加引号）——对齐 Python json.dumps。</summary>
    internal static string JsonToken(string? raw)
    {
        if (raw == null)
        {
            return "null";
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return raw;
        }

        return JsonSerializer.Serialize(raw);
    }
}
