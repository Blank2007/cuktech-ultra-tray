using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CuktechLogin;

/// <summary>
/// 小米云扫码登录客户端 —— C# 移植自 <c>tools/token-fetch/fetch_token.py</c>（741 行，已在本机跑通）。
///
/// 典型流程（首次使用向导）：
/// <code>
/// using var client = new XiaomiClient("cn");
/// client.Log += Console.WriteLine;
/// var qr = await client.RequestQrAsync(ct);              // 申请二维码
/// var png = await client.DownloadQrAsync(qr, ct);        // 显示给用户扫
/// if (await client.PollUntilScannedAsync(qr, ct))        // 长轮询 + 换 serviceToken
/// {
///     client.SaveSession(sessionPath);                   // 可选: 下次免扫码
///     var devices = await client.GetDevicesAsync(ct);    // 探测区域 + 拉全部家庭设备
///     var cuktech = devices.FirstOrDefault(d => d.IsCuktech &amp;&amp; d.TokenHex.Length == 24);
/// }
/// </code>
///
/// 线程安全：单实例只应由一个 UI 线程顺序调用（内部有可变登录状态）。
/// 网络异常：除 <see cref="RequestQrAsync"/> / <see cref="DownloadQrAsync"/> 会抛
/// <see cref="XiaomiLoginException"/> 外，其余方法都不抛网络异常，失败信息写进 <see cref="LastError"/> 与 <see cref="Log"/>。
/// </summary>
public sealed class XiaomiClient : IDisposable
{
    /// <summary>小米扫码登录入口（对应 LOGIN_URL）。</summary>
    private const string LoginEndpoint = "https://account.xiaomi.com/longPolling/loginUrl";

    /// <summary>0x660E CUKTECH 10 号 Ultra 的 productId。</summary>
    private const int TargetProductId = 26126;

    private static readonly string[] CuktechHints = { "cuktech", "酷态科" };
    private static readonly string[] BleHints = { "blt", "ble", "mesh", "bluetooth" };

    /// <summary>pick_region 的探测顺序：cn 优先，其余按参考实现的 SERVERS 顺序。</summary>
    private static readonly string[] RegionOrder = { "cn", "de", "us", "ru", "tw", "sg", "in", "i2" };

    private static readonly Dictionary<string, string> Servers = new(StringComparer.Ordinal)
    {
        ["cn"] = "https://api.io.mi.com/app",
        ["de"] = "https://de.api.io.mi.com/app",
        ["us"] = "https://us.api.io.mi.com/app",
        ["ru"] = "https://ru.api.io.mi.com/app",
        ["tw"] = "https://tw.api.io.mi.com/app",
        ["sg"] = "https://sg.api.io.mi.com/app",
        ["in"] = "https://in.api.io.mi.com/app",
        ["i2"] = "https://i2.api.io.mi.com/app",
    };

    /// <summary>
    /// gethome 的 data 参数，逐字节对齐 Python：
    /// <c>json.dumps({"fg": True, "fetch_share": True, "fetch_share_dev": True, "limit": 300, "app_ver": 7})</c>
    /// 的默认分隔符是 ", " 与 ": "，这里原样保留（服务端解析 JSON，空白无关，但保持等价便于核对）。
    /// </summary>
    private const string HomeRoomData =
        "{\"fg\": true, \"fetch_share\": true, \"fetch_share_dev\": true, \"limit\": 300, \"app_ver\": 7}";

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _loginHttp;    // 登录流程：UseCookies + AllowAutoRedirect（照抄参考实现的 requests.Session）
    private readonly HttpClient _locationHttp; // location 跟随：手工逐跳，才能拿到每一跳的 Set-Cookie
    private readonly HttpClient _apiHttp;      // 云 API：手工拼 Cookie 头（对应 requests 的 cookies= 参数）
    private readonly string _agent;
    private string _server;
    private string? _ssecurity;
    private string? _serviceToken;
    private string? _userId;
    private string? _location;
    private bool _disposed;

    /// <summary>创建客户端。<paramref name="server"/> 默认 cn，取值见 <see cref="Server"/>。</summary>
    /// <exception cref="ArgumentException">区域名未知。</exception>
    public XiaomiClient(string server = "cn")
    {
        if (string.IsNullOrEmpty(server) || !Servers.ContainsKey(server))
        {
            throw new ArgumentException(
                $"未知区域: {server ?? "<null>"}（可用: {string.Join(", ", RegionOrder)}）", nameof(server));
        }

        _server = server;
        _agent = MiCrypto.GenerateAgent();
        DeviceId = MiCrypto.GenerateDeviceId();

        var loginHandler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AllowAutoRedirect = true,
        };
        _loginHttp = new HttpClient(loginHandler) { Timeout = Timeout.InfiniteTimeSpan };

        // AllowAutoRedirect=false：location 那一跳要逐跳抓 Set-Cookie，否则中间跳转的
        // serviceToken 会被 .NET 吞掉（requests 是 session 级累积，语义不同）。
        var locationHandler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AllowAutoRedirect = false,
        };
        _locationHttp = new HttpClient(locationHandler) { Timeout = Timeout.InfiniteTimeSpan };

        // 云 API 的 Cookie 全集由参考实现显式给出，走独立 handler 避免容器里的陈旧 cookie 混进来。
        var apiHandler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        _apiHttp = new HttpClient(apiHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>进度日志（向导界面直接显示）。内容不含 token / ssecurity / serviceToken。</summary>
    public event Action<string>? Log;

    /// <summary>是否已拿到 userId + ssecurity + serviceToken（可直接调 <see cref="GetDevicesAsync"/>）。</summary>
    public bool IsLoggedIn
        => !string.IsNullOrEmpty(_ssecurity) && !string.IsNullOrEmpty(_serviceToken) && !string.IsNullOrEmpty(_userId);

    /// <summary>小米账号 userId（登录或 <see cref="LoadSession"/> 后可用）。</summary>
    public string? UserId => _userId;

    /// <summary>当前/已探测到的区域名（cn/de/us/ru/tw/sg/in/i2）。</summary>
    public string Server => _server;

    /// <summary>随机设备标识（参考实现同样生成，本流程未使用，保留以便以后上报/风控对齐）。</summary>
    public string DeviceId { get; }

    /// <summary>最近一次失败的简要原因（成功时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次申请二维码时服务端返回的字段（原始值，诊断用；由调用方决定打印哪些）。</summary>
    public IReadOnlyDictionary<string, string>? LastQrResponseFields { get; private set; }

    /// <summary>
    /// 构造申请二维码的完整 URL（参数照抄参考实现 request_qr，第 246-276 行）。
    ///
    /// 注意 <c>qs</c> 的值本身就带 %3F/%3D 转义，而 requests 会对参数值再编码一次，
    /// 实测线上就是双编码 <c>qs=%253Fsid%253Dxiaomiio%2526_json%253Dtrue</c>，
    /// 这里保持一致（否则服务端拿不到 sid）。公开出来是为了让 DevCheck 能打印完整请求 URL。
    /// </summary>
    public string BuildQrRequestUrl()
    {
        var sb = new StringBuilder(LoginEndpoint);
        AppendQuery(sb, "_qrsize", "480", first: true);
        AppendQuery(sb, "qs", "%3Fsid%3Dxiaomiio%26_json%3Dtrue");
        AppendQuery(sb, "callback", "https://sts.api.io.mi.com/sts");
        AppendQuery(sb, "_hasLogo", "false");
        AppendQuery(sb, "sid", "xiaomiio");
        AppendQuery(sb, "serviceParam", "");
        AppendQuery(sb, "_locale", "en_GB");
        AppendQuery(sb, "_dc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void AppendQuery(StringBuilder sb, string name, string value, bool first = false)
    {
        sb.Append(first ? '?' : '&');
        sb.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
    }

    // ---------------------------------------------------------------- 第一步: 申请二维码

    /// <summary>
    /// 申请扫码登录二维码（GET https://account.xiaomi.com/longPolling/loginUrl，sid=xiaomiio）。
    /// </summary>
    /// <exception cref="XiaomiLoginException">HTTP 非 200 / 响应缺 qr、lp / 超时 / 网络错误。</exception>
    public async Task<QrTicket> RequestQrAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        LastError = null;

        var url = BuildQrRequestUrl();
        Emit($"申请二维码: GET {url}");

        using var cts = CreateTimeout(TimeSpan.FromSeconds(30), ct);
        HttpStatusCode status;
        string body;
        try
        {
            using var resp = await _loginHttp.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            status = resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new XiaomiLoginException("申请二维码失败: 请求超时 (30s)");
        }
        catch (HttpRequestException ex)
        {
            throw new XiaomiLoginException($"申请二维码失败: {ex.Message}", ex);
        }

        if (status != HttpStatusCode.OK)
        {
            throw new XiaomiLoginException($"申请二维码失败: HTTP {(int)status}");
        }

        var data = ParseFlatJson(body);
        LastQrResponseFields = data;

        if (!data.ContainsKey("qr") || !data.ContainsKey("lp"))
        {
            throw new XiaomiLoginException($"申请二维码失败: 响应缺少 qr/lp: {Truncate(body, 300)}");
        }

        var ttl = ParseTtl(data.GetValueOrDefault("timeout"));
        var ticket = new QrTicket
        {
            QrImageUrl = data["qr"],
            PollUrl = data["lp"],
            LoginUrl = data.GetValueOrDefault("loginUrl") ?? "",
            Ttl = ttl,
        };

        Emit($"二维码已申请: 有效期 {ttl.TotalSeconds:F0}s, 图片 URL {ticket.QrImageUrl.Length} 字符, "
             + $"lp {ticket.PollUrl.Length} 字符, 响应字段 [{string.Join(", ", data.Keys)}]");
        return ticket;
    }

    /// <summary>timeout 字段归一化（毫秒 -&gt; 秒，夹取到 30..300 秒），对应 request_qr 尾部几行。</summary>
    private static TimeSpan ParseTtl(string? raw)
    {
        var seconds = 300.0;
        if (!string.IsNullOrEmpty(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            seconds = parsed;
        }

        if (seconds > 1000) // 毫秒 -> 秒
        {
            seconds /= 1000.0;
        }

        seconds = Math.Max(30.0, Math.Min(seconds, 300.0));
        return TimeSpan.FromSeconds(seconds);
    }

    // ---------------------------------------------------------------- 二维码图片

    /// <summary>下载二维码 PNG（<paramref name="t"/> 的 QrImageUrl）。返回原始字节，由调用方落盘/显示。</summary>
    /// <exception cref="XiaomiLoginException">HTTP 非 200 / 空图片 / 超时 / 网络错误。</exception>
    public async Task<byte[]> DownloadQrAsync(QrTicket t, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(t);
        if (string.IsNullOrEmpty(t.QrImageUrl))
        {
            throw new XiaomiLoginException("下载二维码失败: QrImageUrl 为空");
        }

        using var cts = CreateTimeout(TimeSpan.FromSeconds(30), ct);
        HttpResponseMessage resp;
        try
        {
            resp = await _loginHttp.GetAsync(t.QrImageUrl, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new XiaomiLoginException("下载二维码失败: 请求超时 (30s)");
        }
        catch (HttpRequestException ex)
        {
            throw new XiaomiLoginException($"下载二维码失败: {ex.Message}", ex);
        }

        using (resp)
        {
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                throw new XiaomiLoginException($"下载二维码失败: HTTP {(int)resp.StatusCode}");
            }

            var blob = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (blob.Length == 0)
            {
                throw new XiaomiLoginException("下载二维码失败: 二维码图片为空");
            }

            Emit($"二维码已下载: {blob.Length} bytes ({DetectImageKind(blob)})");
            return blob;
        }
    }

    private static string DetectImageKind(byte[] blob)
    {
        if (blob.Length >= 8
            && blob[0] == 0x89 && blob[1] == 0x50 && blob[2] == 0x4E && blob[3] == 0x47
            && blob[4] == 0x0D && blob[5] == 0x0A && blob[6] == 0x1A && blob[7] == 0x0A)
        {
            return "png";
        }

        return blob.Length >= 2 && blob[0] == 0xFF && blob[1] == 0xD8 ? "jpeg" : "unknown";
    }

    // ---------------------------------------------------------------- 第二步: 长轮询 + 换 serviceToken

    private enum PollState
    {
        Scanned,
        Pending,
        Expired,
    }

    /// <summary>
    /// 长轮询 <paramref name="t"/> 的 lp URL，直到扫码成功（返回 true，并且内部已换好 serviceToken，
    /// <see cref="IsLoggedIn"/> 变 true）或二维码过期（返回 false，需要重新申请）。
    ///
    /// 超时策略照抄参考实现：单次请求 40s（连接 10s 内含在 40s 内），pending 间隔 1s，
    /// 网络错误间隔 2s；HTTP 非 200 视为二维码过期。
    /// </summary>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public async Task<bool> PollUntilScannedAsync(QrTicket t, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(t);
        LastError = null;

        if (string.IsNullOrEmpty(t.PollUrl))
        {
            LastError = "二维码缺少 lp 轮询地址";
            Emit($"轮询失败: {LastError}");
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + t.Ttl;
        Emit($"二维码生效中 (有效期 {t.Ttl.TotalSeconds:F0}s), 等待扫码...");

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var (state, data) = await PollOnceAsync(t.PollUrl, ct).ConfigureAwait(false);
            switch (state)
            {
                case PollState.Scanned:
                    Emit("扫码成功!");
                    return await FinishLoginAsync(data!, ct).ConfigureAwait(false);
                case PollState.Expired:
                    Emit("二维码已过期, 需要重新申请");
                    LastError = "二维码已过期";
                    return false;
                default:
                    break;
            }
        }

        Emit("等待扫码超时 (二维码已过期)");
        LastError = "等待扫码超时 (二维码已过期)";
        return false;
    }

    /// <summary>单次轮询，对应 poll_once（第 292-313 行）。</summary>
    private async Task<(PollState State, Dictionary<string, string>? Data)> PollOnceAsync(
        string lpUrl, CancellationToken ct)
    {
        using var cts = CreateTimeout(TimeSpan.FromSeconds(40), ct);
        HttpResponseMessage resp;
        try
        {
            resp = await _loginHttp.GetAsync(lpUrl, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Python: requests Timeout -> ("pending", None)，不 sleep，直接下一轮
            Emit("轮询等待超时 (40s), 继续等待");
            return (PollState.Pending, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Emit($"轮询网络错误: {ex.Message}");
            await DelaySafe(2000, ct).ConfigureAwait(false);
            return (PollState.Pending, null);
        }

        using (resp)
        {
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Emit($"轮询返回 HTTP {(int)resp.StatusCode}, 视为二维码过期");
                return (PollState.Expired, null);
            }

            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Emit($"轮询响应读取失败: {ex.Message}");
                await DelaySafe(2000, ct).ConfigureAwait(false);
                return (PollState.Pending, null);
            }

            var data = ParseFlatJson(body);
            if (HasValue(data.GetValueOrDefault("ssecurity")) || HasValue(data.GetValueOrDefault("userId")))
            {
                return (PollState.Scanned, data);
            }

            if (HasValue(data.GetValueOrDefault("code")))
            {
                Emit($"轮询响应 code={data["code"]} (继续等待)");
            }

            await DelaySafe(1000, ct).ConfigureAwait(false);
            return (PollState.Pending, null);
        }
    }

    /// <summary>
    /// 扫码成功后收尾：跟随 location 换 serviceToken（第 316-346 行）。
    /// serviceToken 取不到时按 最终响应 cookie -> CookieContainer serviceToken -> yetAnotherServiceToken 顺序回退。
    /// </summary>
    private async Task<bool> FinishLoginAsync(Dictionary<string, string> data, CancellationToken ct)
    {
        _userId = data.GetValueOrDefault("userId");
        _ssecurity = data.GetValueOrDefault("ssecurity");
        _location = data.GetValueOrDefault("location");

        if (string.IsNullOrEmpty(_ssecurity))
        {
            Emit("扫码结果缺少 ssecurity");
            LastError = "扫码结果缺少 ssecurity";
            return false;
        }

        _serviceToken = null;

        if (!string.IsNullOrEmpty(_location))
        {
            try
            {
                var (status, serviceToken) = await FollowLocationAsync(_location!, ct).ConfigureAwait(false);
                Emit($"location 跟随完成: HTTP {(int)status}");
                _serviceToken = string.IsNullOrEmpty(serviceToken) ? null : serviceToken;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Emit("location 跟随超时 (30s)");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException)
            {
                Emit($"location 跟随失败: {ex.Message}");
            }
        }
        else
        {
            Emit("扫码结果无 location, 直接使用已有 cookie");
        }

        _serviceToken ??= FindCookie("serviceToken");
        if (string.IsNullOrEmpty(_serviceToken))
        {
            // 少数情况下 serviceToken 落在 yetAnotherServiceToken
            _serviceToken = FindCookie("yetAnotherServiceToken");
        }

        if (string.IsNullOrEmpty(_serviceToken))
        {
            Emit("无法获取 serviceToken (cookie 中不存在)");
            LastError = "无法获取 serviceToken (cookie 中不存在)";
            return false;
        }

        Emit($"已拿到 serviceToken (userId={_userId})");
        return true;
    }

    /// <summary>
    /// 手工跟随 location 的 302（最多 10 跳），返回最终状态码与最终响应的 serviceToken。
    /// 每一跳的 Set-Cookie 由 CookieContainer 自动累积（等价 requests 的 session 行为）。
    /// </summary>
    private async Task<(HttpStatusCode Status, string? ServiceToken)> FollowLocationAsync(
        string location, CancellationToken ct)
    {
        var current = new Uri(location);
        using var cts = CreateTimeout(TimeSpan.FromSeconds(30), ct);

        for (var hop = 0; hop < 10; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("User-Agent", _agent);
            req.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");

            using var resp = await _locationHttp
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var setCookies = ReadSetCookies(resp);
            var isRedirect = (int)resp.StatusCode is >= 300 and < 400;
            if (isRedirect && resp.Headers.Location is { } next)
            {
                var target = next.IsAbsoluteUri ? next : new Uri(current, next);
                Emit($"location 重定向 -> {target.GetLeftPart(UriPartial.Path)}");
                current = target;
                continue;
            }

            setCookies.TryGetValue("serviceToken", out var serviceToken);
            return (resp.StatusCode, serviceToken);
        }

        Emit("location 重定向次数过多 (>10)");
        return (HttpStatusCode.TooManyRequests, null);
    }

    /// <summary>解析一次响应的 Set-Cookie（每个头一个 cookie，取 name=value 部分）。</summary>
    private static Dictionary<string, string> ReadSetCookies(HttpResponseMessage resp)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return result;
        }

        foreach (var raw in values)
        {
            var semi = raw.IndexOf(';');
            var pair = semi < 0 ? raw : raw[..semi];
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (name.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private string? FindCookie(string name)
    {
        foreach (Cookie cookie in _cookies.GetAllCookies())
        {
            if (string.Equals(cookie.Name, name, StringComparison.Ordinal) && !string.IsNullOrEmpty(cookie.Value))
            {
                return cookie.Value;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- 第三步: 云 API

    /// <summary>
    /// 调一次云 API（第 349-395 行）。返回解密后的 JSON；任何失败都返回 null 并记日志，不抛异常。
    /// </summary>
    private async Task<JsonElement?> CallApiAsync(string region, string path, string dataJson, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_ssecurity))
        {
            Emit("尚未登录, 无法调用云 API");
            return null;
        }

        var apiName = path[(path.LastIndexOf('/') + 1)..];
        var url = Servers[region] + path;
        var nonce = MiCrypto.GenerateNonce(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var sn = MiCrypto.SignedNonce(_ssecurity, nonce);
        var fields = MiCrypto.GenerateEncParams(
            url, "POST", sn, nonce, new[] { new KeyValuePair<string, string>("data", dataJson) }, _ssecurity);

        var query = string.Join("&", fields.Select(kv =>
            Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        using var req = new HttpRequestMessage(HttpMethod.Post, url + "?" + query);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        req.Headers.TryAddWithoutValidation("User-Agent", _agent);
        // 注意：头部名就是小米的拼写错误 protocal（不是 protocol），照抄参考实现，别"修正"
        req.Headers.TryAddWithoutValidation("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        req.Headers.TryAddWithoutValidation("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4");
        req.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader());

        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        req.Content = content;

        using var cts = CreateTimeout(TimeSpan.FromSeconds(40), ct);
        HttpResponseMessage resp;
        try
        {
            resp = await _apiHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Emit($"API {apiName} 请求超时 (40s)");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Emit($"API 请求异常 {apiName}: {ex.Message}");
            return null;
        }

        using (resp)
        {
            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Emit($"API 响应读取失败 {apiName}: {ex.Message}");
                return null;
            }

            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Emit($"API {apiName} -> HTTP {(int)resp.StatusCode}");
                return null;
            }

            string decoded;
            try
            {
                decoded = MiCrypto.DecryptRc4(MiCrypto.SignedNonce(_ssecurity, nonce), body);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                Emit($"API 响应解密失败 {apiName}: {ex.Message}");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(decoded);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                Emit($"API {apiName} 返回非 JSON: {Truncate(decoded, 200)}");
                return null;
            }
        }
    }

    /// <summary>参考实现里 requests 侧 cookie 全集（第 359-368 行），顺序也照抄。</summary>
    private string BuildCookieHeader()
        => $"userId={_userId}; yetAnotherServiceToken={_serviceToken}; serviceToken={_serviceToken}; "
           + "locale=en_GB; timezone=GMT+08:00; is_daylight=0; dst_offset=0; channel=MI_APP_STORE";

    // ---------------------------------------------------------------- 第四步: 区域探测 + 设备列表

    /// <summary>
    /// 探测可用区域并拉取账号下全部家庭的设备（第 415-463 行）。
    /// 返回的列表按「酷态科且取到 token → 酷态科 → 有序 token → 名字」排序，第一台通常就是目标设备。
    /// 任何失败返回空列表并写 <see cref="LastError"/>，不抛网络异常。
    /// </summary>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public async Task<IReadOnlyList<MiDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        LastError = null;

        if (string.IsNullOrEmpty(_ssecurity))
        {
            LastError = "未登录 (ssecurity 缺失)";
            Emit($"拉取设备失败: {LastError}");
            return Array.Empty<MiDevice>();
        }

        if (string.IsNullOrEmpty(_serviceToken) && !await EnsureServiceTokenAsync(ct).ConfigureAwait(false))
        {
            return Array.Empty<MiDevice>();
        }

        var (region, homes) = await PickRegionAsync(ct).ConfigureAwait(false);
        if (region == null || homes == null)
        {
            LastError = "所有区域 gethome 均失败, 无法列出设备";
            Emit(LastError);
            return Array.Empty<MiDevice>();
        }

        var raws = await CollectDevicesAsync(region, homes.Value, ct).ConfigureAwait(false);
        Emit($"总计拉取到 {raws.Count} 台设备");

        // 与 Python 版一致：先处理疑似酷态科/BLE 设备，尽快拿到目标 token
        var priority = raws.Where(r => r.IsCuktech || r.LooksBle).ToList();
        var rest = raws.Where(r => !r.IsCuktech && !r.LooksBle).ToList();
        Emit($"优先探测 {priority.Count} 台 (酷态科/BLE 候选), 其余 {rest.Count} 台随后补全");

        var devices = new List<MiDevice>(raws.Count);
        foreach (var raw in priority.Concat(rest))
        {
            devices.Add(await BuildDeviceAsync(raw, region, ct).ConfigureAwait(false));
        }

        return devices
            .OrderBy(d => d.IsCuktech ? 0 : 1)
            .ThenBy(d => string.IsNullOrEmpty(d.TokenHex) ? 1 : 0)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>ssecurity 在但 serviceToken 丢了（例如手工 LoadSession 了半截）时，用 location 再换一次。</summary>
    private async Task<bool> EnsureServiceTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_serviceToken))
        {
            return true;
        }

        if (string.IsNullOrEmpty(_location))
        {
            LastError = "serviceToken 缺失且无 location, 无法补取 (请重新扫码)";
            Emit($"拉取设备失败: {LastError}");
            return false;
        }

        Emit("serviceToken 缺失, 重新跟随 location 获取");
        try
        {
            var (status, serviceToken) = await FollowLocationAsync(_location!, ct).ConfigureAwait(false);
            Emit($"location 跟随完成: HTTP {(int)status}");
            _serviceToken = string.IsNullOrEmpty(serviceToken)
                ? FindCookie("serviceToken") ?? FindCookie("yetAnotherServiceToken")
                : serviceToken;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Emit("location 跟随超时 (30s)");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException)
        {
            Emit($"location 跟随失败: {ex.Message}");
        }

        if (string.IsNullOrEmpty(_serviceToken))
        {
            LastError = "无法获取 serviceToken";
            Emit($"拉取设备失败: {LastError}");
            return false;
        }

        return true;
    }

    /// <summary>pick_region：先 cn，失败依次回落到其它区域，判定条件是 gethome 的 code == 0。</summary>
    private async Task<(string? Region, JsonElement? Homes)> PickRegionAsync(CancellationToken ct)
    {
        foreach (var region in RegionOrder)
        {
            var res = await CallApiAsync(region, "/v2/homeroom/gethome", HomeRoomData, ct).ConfigureAwait(false);
            if (res is { } homes && IsCodeZero(homes))
            {
                Emit($"使用区域: {region}");
                _server = region;
                return (region, res);
            }

            Emit($"区域 {region} 不可用: {Truncate(res?.GetRawText() ?? "无响应", 160)}");
        }

        return (null, null);
    }

    /// <summary>collect_devices：拿 homelist，逐个 home 调 home_device_list，按 did 去重。</summary>
    private async Task<List<DeviceRaw>> CollectDevicesAsync(string region, JsonElement homes, CancellationToken ct)
    {
        var list = new List<DeviceRaw>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var homeList = new List<JsonElement>();

        if (homes.ValueKind == JsonValueKind.Object
            && homes.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("homelist", out var homelist)
            && homelist.ValueKind == JsonValueKind.Array)
        {
            foreach (var home in homelist.EnumerateArray())
            {
                homeList.Add(home.Clone());
            }
        }

        Emit($"家庭数量: {homeList.Count}");

        foreach (var home in homeList)
        {
            var homeId = GetString(home, "id");
            var homeName = GetString(home, "name");
            var owner = GetString(home, "uid");

            // json.dumps({"home_owner": ..., "home_id": ..., "limit": 200,
            //             "get_split_device": True, "support_smart_home": True})
            var payload = "{\"home_owner\": " + MiCrypto.JsonToken(HasValue(owner) ? owner : _userId)
                          + ", \"home_id\": " + GetRawOrNull(home, "id")
                          + ", \"limit\": 200, \"get_split_device\": true, \"support_smart_home\": true}";

            var res = await CallApiAsync(region, "/v2/home/home_device_list", payload, ct).ConfigureAwait(false);
            if (res == null || !IsCodeZero(res.Value))
            {
                Emit($"home_device_list 失败 home={homeId}: {Truncate(res?.GetRawText() ?? "无响应", 200)}");
                continue;
            }

            var devList = ExtractDeviceList(res.Value);
            Emit($"家庭 {homeId} ({homeName}): {devList.Count} 台设备");

            foreach (var dev in devList)
            {
                var did = GetString(dev, "did");
                if (did.Length > 0 && !seen.Add(did))
                {
                    continue;
                }

                list.Add(new DeviceRaw
                {
                    Dev = dev,
                    Did = did,
                    HomeId = homeId,
                    IsCuktech = IsCuktech(dev),
                    LooksBle = LooksBle(dev),
                });
            }
        }

        return list;
    }

    /// <summary>result.device_info 或 result.list。</summary>
    private static List<JsonElement> ExtractDeviceList(JsonElement res)
    {
        var devices = new List<JsonElement>();
        if (!res.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return devices;
        }

        foreach (var key in new[] { "device_info", "list" })
        {
            if (result.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var dev in arr.EnumerateArray())
                {
                    devices.Add(dev.Clone());
                }

                break;
            }
        }

        return devices;
    }

    /// <summary>组装一台设备：token 优先取设备列表的 token 字段，缺失时（酷态科/BLE 候选）再问 blt_get_beaconkey。</summary>
    private async Task<MiDevice> BuildDeviceAsync(DeviceRaw raw, string region, CancellationToken ct)
    {
        var dev = raw.Dev;
        var name = GetString(dev, "name");
        var model = GetString(dev, "model");

        var tokenHex = "";
        var tokenSource = "";
        var listed = MiCrypto.KeyToHex(GetString(dev, "token"));
        if (listed is { Length: 24 })
        {
            tokenHex = listed;
            tokenSource = "device_list_token";
        }
        else if (raw.IsCuktech || raw.LooksBle)
        {
            var best = await FetchBeaconKeyTokenAsync(region, raw.Did, ct).ConfigureAwait(false);
            if (best is { Length: 24 })
            {
                tokenHex = best;
                tokenSource = "blt_get_beaconkey";
            }
        }

        var device = new MiDevice
        {
            Did = raw.Did,
            Name = name,
            Model = model,
            Mac = GetString(dev, "mac"),
            TokenHex = tokenHex,
            TokenSource = tokenSource,
            IsCuktech = raw.IsCuktech,
            IsOnline = dev.ValueKind == JsonValueKind.Object
                       && dev.TryGetProperty("isOnline", out var online)
                       && ToBool(online),
        };

        if (tokenHex.Length == 24)
        {
            // 参考实现会把 token 明文打出来；向导日志不打印密钥本身，只报来源与字节数
            Emit($"token OK: {name} | {model} | {raw.Did} -> 12 字节 (来源={tokenSource})");
        }
        else if (raw.IsCuktech || raw.LooksBle)
        {
            Emit($"token 无: {name} | {model} | {raw.Did} (blt_get_beaconkey 无 12 字节字段)");
        }
        else
        {
            Emit($"token 无: {name} | {model} | {raw.Did} (非酷态科/BLE 候选, 未探测)");
        }

        return device;
    }

    /// <summary>
    /// blt_get_beaconkey（第 466-479 行）。返回 12 字节候选的 hex；注意该接口的 beaconkey 本身是
    /// 24 字节（仅 MiBeacon 广播解密），绝不能当认证 token 用 —— 这里只认 12 字节字段。
    /// </summary>
    private async Task<string?> FetchBeaconKeyTokenAsync(string region, string did, CancellationToken ct)
    {
        // json.dumps({"did": did, "pdid": 1})
        var payload = "{\"did\": " + MiCrypto.JsonQuote(did) + ", \"pdid\": 1}";
        var res = await CallApiAsync(region, "/v2/device/blt_get_beaconkey", payload, ct).ConfigureAwait(false);
        if (res == null)
        {
            return null;
        }

        if (!IsCodeZero(res.Value))
        {
            Emit($"blt_get_beaconkey code={GetString(res.Value, "code")} {GetString(res.Value, "message")} [{did}]");
            return null;
        }

        if (!res.Value.TryGetProperty("result", out var inner) || inner.ValueKind != JsonValueKind.Object)
        {
            Emit($"blt_get_beaconkey 返回无 result [{did}]");
            return null;
        }

        var (candidates, best) = MiCrypto.ParseFields(inner);
        var summary = string.Join(", ", candidates.Select(kv => $"{kv.Key}:{kv.Value.Bytes}B"));
        // 参考实现把整个 result 明文打出来；这里只打字段名与字节数，避免日志泄露密钥
        Emit($"blt_get_beaconkey [{did}] 字段: {(summary.Length == 0 ? "无" : summary)}");
        return best?.Hex;
    }

    // ---------------------------------------------------------------- 会话持久化

    /// <summary>
    /// 保存登录会话（含 serviceToken，属敏感信息）。JSON 字段名与 Python 版 session.json 完全一致：
    /// <c>user_id, ssecurity, service_token, server, saved_at</c>。目录不存在会自动创建。
    /// </summary>
    public void SaveSession(string path)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空", nameof(path));
        }

        var full = Path.GetFullPath(path);
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                WriteSessionValue(writer, "user_id", _userId);
                writer.WriteString("ssecurity", _ssecurity ?? "");
                writer.WriteString("service_token", _serviceToken ?? "");
                writer.WriteString("server", _server);
                writer.WriteString("saved_at", IsoNow());
                writer.WriteEndObject();
            }

            // 原子替换，避免向导下一次启动读到半截文件
            var tmp = full + ".tmp";
            File.WriteAllBytes(tmp, buffer.ToArray());
            File.Move(tmp, full, overwrite: true);
            Emit($"登录会话已保存到 {full} (下次可直接复用, 免扫码)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Emit($"保存会话失败: {ex.Message}");
        }
    }

    /// <summary>userId 是数字时写成 JSON 数字（与 Python 版一致），否则写成字符串。</summary>
    private static void WriteSessionValue(Utf8JsonWriter writer, string name, string? value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteString(name, value ?? "");
        }
    }

    private static string IsoNow()
    {
        var utc = DateTimeOffset.UtcNow;
        var fraction = utc.Microsecond > 0
            ? "." + utc.ToString("ffffff", CultureInfo.InvariantCulture)
            : "";
        return $"{utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}{fraction}+00:00";
    }

    /// <summary>
    /// 读回会话（对应 load_session）。兼容 Python 版的下划线字段名，也接受 userId/serviceToken 驼峰写法。
    /// 文件不存在或无效只记日志，不抛异常（由 <see cref="IsLoggedIn"/> 让调用方判断）。
    /// </summary>
    public void LoadSession(string path)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            Emit($"会话文件不存在: {full}");
            return;
        }

        Dictionary<string, string> data;
        try
        {
            var text = File.ReadAllText(full, Encoding.UTF8);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                Emit($"读取会话失败: 顶层不是 JSON 对象 ({full})");
                return;
            }

            data = FlattenObject(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Emit($"读取会话失败: {ex.Message}");
            return;
        }

        _userId = FirstNonEmpty(data, "user_id", "userId");
        _ssecurity = FirstNonEmpty(data, "ssecurity", "sessionId");
        _serviceToken = FirstNonEmpty(data, "service_token", "serviceToken");
        var server = FirstNonEmpty(data, "server", "region");
        if (server != null && Servers.ContainsKey(server))
        {
            _server = server;
        }

        _location = null; // location 是扫码当次的下发地址，不落盘

        var ok = IsLoggedIn;
        Emit($"复用已保存会话: {(ok ? "是" : "否")} (userId={_userId}, server={_server})");
        if (!ok)
        {
            LastError = "已保存会话无效 (user_id / ssecurity / service_token 不全)";
        }
    }

    private static string? FirstNonEmpty(Dictionary<string, string> data, params string[] names)
    {
        foreach (var name in names)
        {
            if (data.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- JSON / 小工具

    /// <summary>去掉 &amp;&amp;&amp;START&amp;&amp;&amp; 前缀后解析成 字段名 -&gt; 字符串（失败返回空字典，对应 to_json）。</summary>
    private static Dictionary<string, string> ParseFlatJson(string text)
    {
        var clean = (text ?? "").Replace("&&&START&&&", "").Replace("&&&END&&&", "").Trim();
        if (clean.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var doc = JsonDocument.Parse(clean);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? FlattenObject(doc.RootElement)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> FlattenObject(JsonElement obj)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = MiCrypto.ToFlatString(prop.Value);
        }

        return dict;
    }

    private static bool HasValue(string? value)
        => !string.IsNullOrEmpty(value) && value is not ("0" or "false");

    private static string GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
        {
            return "";
        }

        return MiCrypto.ToFlatString(value);
    }

    private static string GetRawOrNull(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
            ? value.GetRawText()
            : "null";

    private static bool IsCodeZero(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty("code", out var code)
           && ToInt(code) == 0;

    private static int? ToInt(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt32(out var number) ? number : null,
        JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null,
        _ => null,
    };

    private static bool ToBool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => element.TryGetInt32(out var number) && number != 0,
        JsonValueKind.String => element.GetString() is "true" or "True" or "1",
        _ => false,
    };

    private static bool IsCuktech(JsonElement dev)
    {
        var hay = string.Join(" ",
            new[] { "model", "name", "did", "extra" }.Select(k => GetString(dev, k))).ToLowerInvariant();
        if (CuktechHints.Any(hint => hay.Contains(hint, StringComparison.Ordinal)))
        {
            return true;
        }

        foreach (var key in new[] { "pid", "productId", "product_id" })
        {
            if (GetString(dev, key) == TargetProductId.ToString(CultureInfo.InvariantCulture))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksBle(JsonElement dev)
    {
        var hay = string.Join(" ",
            new[] { "model", "name", "extra" }.Select(k => GetString(dev, k))).ToLowerInvariant();
        return BleHints.Any(hint => hay.Contains(hint, StringComparison.Ordinal));
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";

    private static CancellationTokenSource CreateTimeout(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static async Task DelaySafe(int milliseconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(milliseconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消由外层的 ct.ThrowIfCancellationRequested() 处理
        }
    }

    private void Emit(string message)
    {
        try
        {
            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
        catch
        {
            // 日志回调是 UI 代码，绝不能让它的异常打断登录流程
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>释放三个 HttpClient（含 CookieContainer）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loginHttp.Dispose();
        _locationHttp.Dispose();
        _apiHttp.Dispose();
    }

    /// <summary>collect_devices 中间态：原始 JSON + 预判的候选标记。</summary>
    private sealed class DeviceRaw
    {
        public required JsonElement Dev { get; init; }

        public required string Did { get; init; }

        public string? HomeId { get; init; }

        public bool IsCuktech { get; init; }

        public bool LooksBle { get; init; }
    }
}
