using System.Text;

namespace CuktechTraySetup;

/// <summary>--report 输出的 JSON。手写而不是引 System.Text.Json，保证单文件发布时行为可控。</summary>
internal sealed class SetupReport
{
    public string Action = "";
    public string Result = "ok";       // ok | failed | cancelled
    public int ExitCode;
    public string? Error;
    public List<string> Steps = new();
    public List<string> Warnings = new();
    private readonly List<KeyValuePair<string, string?>> _fields = new();

    public void Add(string key, string? value)
    {
        _fields.RemoveAll(kv => kv.Key == key);
        _fields.Add(new KeyValuePair<string, string?>(key, value));
    }

    public void Step(string text)
    {
        Log.Write("[step] " + text);
        Steps.Add(text);
    }

    public void Warn(string text)
    {
        Log.Write("[warn] " + text);
        Warnings.Add(text);
    }

    public void Fail(string message, int exitCode = ExitCodes.Failed)
    {
        Result = "failed";
        Error = message;
        ExitCode = exitCode;
    }

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\r\n");
        sb.Append("  \"action\": ").Append(Quote(Action)).Append(",\r\n");
        sb.Append("  \"result\": ").Append(Quote(Result)).Append(",\r\n");
        sb.Append("  \"exitCode\": ").Append(ExitCode.ToString()).Append(",\r\n");
        sb.Append("  \"error\": ").Append(Quote(Error)).Append(",\r\n");

        sb.Append("  \"fields\": {\r\n");
        for (var i = 0; i < _fields.Count; i++)
        {
            sb.Append("    ").Append(Quote(_fields[i].Key)).Append(": ").Append(Quote(_fields[i].Value));
            if (i < _fields.Count - 1) sb.Append(',');
            sb.Append("\r\n");
        }
        sb.Append("  },\r\n");

        sb.Append("  \"steps\": ").Append(QuoteList(Steps)).Append(",\r\n");
        sb.Append("  \"warnings\": ").Append(QuoteList(Warnings)).Append("\r\n");
        sb.Append("}\r\n");
        return sb.ToString();
    }

    public void Write(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(full, ToJson(), new UTF8Encoding(false));
            Log.Write("[report] 已写入 " + full);
        }
        catch (Exception ex)
        {
            Log.Write("[report] 写报告失败：" + ex.Message);
        }
    }

    private static string QuoteList(List<string> items)
    {
        if (items.Count == 0) return "[]";
        var sb = new StringBuilder("[");
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Quote(items[i]));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string Quote(string? value)
    {
        if (value is null) return "null";
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
