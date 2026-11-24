using System.Text;
using System.Text.Json;
using CuktechLogin;

namespace CuktechTray;

/// <summary>
/// AOT 专项自查：专盯「调试构建里好好的、发布成 NativeAOT 才炸」的位置。
///
/// 起因是 2026-11-25 的用户反馈：扫完码挑设备时向导上出现
/// <c>Reflection-based serialization has been disabled for this app</c>。
/// 根因是小米云的 /v2/home/home_device_list 请求体用 <c>JsonSerializer.Serialize</c> 拼参数，
/// 而 PublishAot 会把反射式序列化关掉（runtimeconfig 里
/// System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false），于是真机才炸。
/// 修法是改成手写转义（MiCrypto.JsonQuote），这里把登录链路所有自己拼 JSON 的地方跑一遍，
/// 顺手确认设置文件的 source-gen 序列化仍然可用。
/// </summary>
internal static class AotChecks
{
    public static bool RunAll()
    {
        var sb = new StringBuilder();
        bool ok = true;

        // JsonQuote：引号、反斜杠、换行、制表符、控制字符各来一个
        ok &= Expect(sb, "json-quote-escape", MiCrypto.JsonQuote("A\"B\\C\n\t\u0001"),
            "\"A\\\"B\\\\C\\n\\t\\u0001\"");
        ok &= Expect(sb, "json-quote-null", MiCrypto.JsonQuote(null), "null");
        ok &= Expect(sb, "json-quote-empty", MiCrypto.JsonQuote(""), "\"\"");
        ok &= Expect(sb, "json-quote-cjk", MiCrypto.JsonQuote("酷态科"), "\"酷态科\"");

        // JsonToken：纯数字原样、其它加引号（对齐 Python json.dumps）
        ok &= Expect(sb, "json-token-number", MiCrypto.JsonToken("12345"), "12345");
        ok &= Expect(sb, "json-token-string", MiCrypto.JsonToken("phone_owner"), "\"phone_owner\"");
        ok &= Expect(sb, "json-token-null", MiCrypto.JsonToken(null), "null");

        ok &= SettingsRoundTrip(sb);

        // 环境信息：确认反射式序列化确实被关掉了（关掉才对，说明上面那些手写路径是必须的）
        bool reflectionEnabled = true;
        if (AppContext.TryGetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", out bool flag))
        {
            reflectionEnabled = flag;
        }

        sb.AppendLine("[aot] json-reflection-enabled=" + reflectionEnabled);
        Console.Write(sb.ToString());
        return ok;
    }

    private static bool SettingsRoundTrip(StringBuilder sb)
    {
        try
        {
            var s = new Settings
            {
                Mac = "AA:BB:CC:DD:EE:FF",
                TokenHex = "00112233445566778899aabb",
                IconStyle = "Dark",
            };
            string json = JsonSerializer.Serialize(s, SettingsJsonContext.Default.Settings);
            var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings);
            bool ok = back != null
                      && back.Mac == s.Mac
                      && back.TokenHex == s.TokenHex
                      && back.IconStyle == s.IconStyle;
            sb.AppendLine(ok ? "[aot] settings-roundtrip ok" : "[aot] settings-roundtrip FAILED");
            return ok;
        }
        catch (Exception ex)
        {
            sb.AppendLine("[aot] settings-roundtrip FAILED " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static bool Expect(StringBuilder sb, string name, string actual, string expected)
    {
        bool ok = actual == expected;
        sb.AppendLine(ok
            ? "[aot] " + name + " ok"
            : "[aot] " + name + " FAILED got=[" + actual + "] want=[" + expected + "]");
        return ok;
    }
}
