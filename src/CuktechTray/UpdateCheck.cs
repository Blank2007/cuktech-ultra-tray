using System.Net;
using System.Text.Json;
using Microsoft.Win32;

namespace CuktechTray;

/// <summary>
/// 问一次 GitHub Releases 看有没有新版。只在用户点菜单「检查更新」时联网，
/// 平时（包括刚启动、正在连充电器时）不发任何网络请求。
/// </summary>
internal static class UpdateCheck
{
    public const string RepoUrl = "https://github.com/Blank2007/cuktech-ultra-tray";
    // GitHub 的 API 域名跟网页域名不同，这里要 api.github.com，路径还要带 /repos/
    private static readonly Uri LatestApi = new("https://api.github.com/repos/Blank2007/cuktech-ultra-tray/releases/latest");

    /// <summary>Ok=false 表示这次没问成（没网、被墙、GitHub 限流），Error 里是原因。</summary>
    internal readonly record struct Result(bool Ok, Version? Latest, string ReleaseUrl, string Error, string Via);

    /// <summary>当前 exe 的版本号，去掉第四段（0.3.1.0 → 0.3.1）。</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(UpdateCheck).Assembly.GetName().Version;
            if (v is null) return new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static async Task<Result> RunAsync(CancellationToken ct = default)
    {
        // 国内直连 api.github.com 多半连不上（或者被塞回一张 HTML 拦截页），
        // 而 .NET 8 的 HttpClient 又不会去读系统的代理设置。所以：
        // 先按注册表里的系统代理试一次，不行再直连一次。
        var proxy = SystemProxy();
        var errors = new List<string>();

        if (proxy is not null)
        {
            var viaProxy = await TryAsync(proxy, "代理", ct).ConfigureAwait(false);
            if (viaProxy.Ok) return viaProxy;
            errors.Add(viaProxy.Error);
        }

        var direct = await TryAsync(null, "直连", ct).ConfigureAwait(false);
        if (direct.Ok) return direct;
        errors.Add(direct.Error);

        return new Result(false, null, "", string.Join("；", errors), "");
    }

    private static async Task<Result> TryAsync(IWebProxy? proxy, string via, CancellationToken ct)
    {
        try
        {
            var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) };
            if (proxy is not null) { handler.Proxy = proxy; handler.UseProxy = true; }
            else { handler.UseProxy = false; }

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CuktechTray/" + Current);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Result(false, null, "", $"{via}：GitHub 返回 HTTP {(int)resp.StatusCode}", via);

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return new Result(false, null, "",
                    $"{via}：返回的不是 JSON（Content-Type={resp.Content.Headers.ContentType}，开头：{Snippet(body)}）", via);
            }

            using var _ = doc;
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : RepoUrl + "/releases";
            return new Result(true, ParseVersion(tag), url, "", via);
        }
        catch (Exception ex)
        {
            return new Result(false, null, "", $"{via}：{ex.Message}", via);
        }
    }

    /// <summary>读 IE/系统代理设置（Clash、v2rayN 这类工具都写这儿）。没开代理返回 null。</summary>
    private static IWebProxy? SystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return null;
            if (key.GetValue("ProxyEnable") is not int enable || enable == 0) return null;
            if (key.GetValue("ProxyServer") is not string server || string.IsNullOrWhiteSpace(server)) return null;

            // 常见写法是 "127.0.0.1:7897"；也可能是 "http=host:port;https=host:port"，取 https 那段
            string address = server;
            int eq = server.LastIndexOf('=');
            if (eq >= 0) address = server[(eq + 1)..];
            address = address.Trim();
            if (address.Length == 0) return null;

            if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
            return new WebProxy(address);
        }
        catch
        {
            return null;
        }
    }

    private static string Snippet(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    private static Version? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        return Version.TryParse(s, out var v) ? v : null;
    }
}
