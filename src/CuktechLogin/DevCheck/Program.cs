using System.Text;
using System.Text.Json;
using CuktechLogin;

namespace CuktechLogin.DevCheck;

/// <summary>
/// 类库连通性自检：申请二维码 -> 下载 PNG 落盘 -> 打印请求 URL 与响应字段（不打印敏感值）。
/// 用法: dotnet run --project src\CuktechLogin\DevCheck\DevCheck.csproj [-- &lt;输出目录&gt;]
/// 扫码之后的 PollUntilScanned / GetDevices 需要真人用米家 App 扫码，本程序不自动跑那一段。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "--crypto")
        {
            CryptoVectors();
            return 0;
        }

        if (args.Length > 0 && args[0] == "--poll-negative")
        {
            return await PollNegativeAsync();
        }

        if (args.Length > 0 && args[0] == "--scan")
        {
            return await ScanAndListAsync();
        }

        var outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : DefaultOutDir();
        Directory.CreateDirectory(outDir);
        var pngPath = Path.Combine(outDir, "qr.png");
        var jsonPath = Path.Combine(outDir, "qr-info.json");

        Console.WriteLine("=== CuktechLogin DevCheck ===");
        Console.WriteLine($"输出目录: {outDir}");
        Console.WriteLine();

        using var client = new XiaomiClient("cn");
        client.Log += line => Console.WriteLine("  " + line);

        QrTicket ticket;
        try
        {
            ticket = await client.RequestQrAsync();
        }
        catch (XiaomiLoginException ex)
        {
            Console.WriteLine();
            Console.WriteLine("[FAIL] 申请二维码失败: " + ex.Message);
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("--- 请求二维码的完整 URL ---");
        Console.WriteLine(client.BuildQrRequestUrl());
        Console.WriteLine();

        Console.WriteLine("--- 返回字段 (只显示非敏感项) ---");
        var fields = client.LastQrResponseFields ?? new Dictionary<string, string>();
        foreach (var kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            // 不打印 loginUrl / lp / qr 以外的长值，也不打印任何 token 类字段；这里只表意
            var value = kv.Key switch
            {
                "qr" => $"<{kv.Value.Length} 字符>",
                "lp" => $"<{kv.Value.Length} 字符>",
                "loginUrl" => $"<{kv.Value.Length} 字符>",
                _ => kv.Value,
            };
            Console.WriteLine($"  {kv.Key} = {value}");
        }

        Console.WriteLine();
        Console.WriteLine("--- 解析结果 ---");
        Console.WriteLine($"  QrImageUrl = {ticket.QrImageUrl}");
        Console.WriteLine($"  PollUrl    = {ticket.PollUrl}");
        Console.WriteLine($"  LoginUrl   = {ticket.LoginUrl}");
        Console.WriteLine($"  Ttl        = {ticket.Ttl.TotalSeconds:F0}s ({(int)ticket.Ttl.TotalSeconds} 秒)");
        Console.WriteLine();

        byte[] png;
        try
        {
            png = await client.DownloadQrAsync(ticket);
        }
        catch (XiaomiLoginException ex)
        {
            Console.WriteLine("[FAIL] 下载二维码失败: " + ex.Message);
            return 2;
        }

        await File.WriteAllBytesAsync(pngPath, png);

        var magic = png.Length >= 8
                    && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47;
        var onDisk = new FileInfo(pngPath);
        var (width, height) = PngSize(png);

        Console.WriteLine("--- 落盘结果 ---");
        Console.WriteLine($"  文件      = {pngPath}");
        Console.WriteLine($"  存在      = {File.Exists(pngPath)}");
        Console.WriteLine($"  字节      = {onDisk.Length}");
        Console.WriteLine($"  魔数 PNG  = {magic}");
        Console.WriteLine($"  尺寸      = {(width > 0 ? $"{width}x{height}" : "未知")}");

        var info = new
        {
            generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            requestUrl = client.BuildQrRequestUrl(),
            qrImageUrl = ticket.QrImageUrl,
            pollUrl = ticket.PollUrl,
            loginUrl = ticket.LoginUrl,
            ttlSeconds = (int)ticket.Ttl.TotalSeconds,
            responseFields = fields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            pngBytes = png.Length,
            pngWidth = width,
            pngHeight = height,
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine($"  附带信息  = {jsonPath}");

        var ok = magic && png.Length > 0 && File.Exists(pngPath);
        Console.WriteLine();
        Console.WriteLine(ok ? "[OK] 二维码已下载到磁盘 (端到端连通)" : "[FAIL] 二维码内容不像 PNG");
        return ok ? 0 : 3;
    }




    /// <summary>
    /// 半自动真机验证：申请二维码 -> 落盘并提示用米家 App 扫 -> 长轮询 -> 换 serviceToken ->
    /// 探测区域 -> 列出全部设备与 token 有无。需要真人扫码，所以不放进默认流程。
    /// 若 out\session.json 已存在且有效，则跳过扫码直接列设备（对应向导的"免扫码"路径）。
    /// </summary>
    private static async Task<int> ScanAndListAsync()
    {
        var outDir = DefaultOutDir();
        Directory.CreateDirectory(outDir);
        var sessionPath = Path.Combine(outDir, "session.json");

        using var client = new XiaomiClient("cn");
        client.Log += line => Console.WriteLine("  " + line);

        if (File.Exists(sessionPath))
        {
            Console.WriteLine($"--- 尝试复用会话: {sessionPath} ---");
            client.LoadSession(sessionPath);
        }

        if (!client.IsLoggedIn)
        {
            QrTicket ticket;
            try
            {
                ticket = await client.RequestQrAsync();
            }
            catch (XiaomiLoginException ex)
            {
                Console.WriteLine("[FAIL] 申请二维码失败: " + ex.Message);
                return 1;
            }

            var pngPath = Path.Combine(outDir, "qr.png");
            byte[] png;
            try
            {
                png = await client.DownloadQrAsync(ticket);
            }
            catch (XiaomiLoginException ex)
            {
                Console.WriteLine("[FAIL] 下载二维码失败: " + ex.Message);
                return 2;
            }

            await File.WriteAllBytesAsync(pngPath, png);
            Console.WriteLine();
            Console.WriteLine($"请用米家 App 扫描: {pngPath}  (有效期 {ticket.Ttl.TotalSeconds:F0}s)");
            Console.WriteLine($"二维码 URL: {ticket.QrImageUrl}");
            Console.WriteLine();

            bool scanned;
            try
            {
                scanned = await client.PollUntilScannedAsync(ticket);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[中断] 已取消");
                return 130;
            }

            if (!scanned)
            {
                Console.WriteLine($"[FAIL] 未完成扫码: {client.LastError}");
                return 3;
            }

            Console.WriteLine($"[OK] 扫码成功, userId={client.UserId}, server={client.Server}");
            client.SaveSession(sessionPath);
        }
        else
        {
            Console.WriteLine($"[OK] 复用会话成功, userId={client.UserId}, server={client.Server}");
        }

        var devices = await client.GetDevicesAsync();
        Console.WriteLine();
        Console.WriteLine($"--- 设备列表 ({devices.Count} 台, 区域 {client.Server}) ---");
        if (devices.Count == 0)
        {
            Console.WriteLine($"  (空) LastError={client.LastError}");
        }

        foreach (var d in devices)
        {
            Console.WriteLine($"  {(d.IsCuktech ? "*" : " ")} {d.Name} | {d.Model} | did={d.Did} | mac={d.Mac} "
                              + $"| token={(d.TokenHex.Length == 24 ? d.TokenHex : "<无>")} ({d.TokenSource}) | online={d.IsOnline}");
        }

        var target = devices.FirstOrDefault(d => d.IsCuktech && d.TokenHex.Length == 24);
        Console.WriteLine();
        if (target == null)
        {
            Console.WriteLine("[WARN] 没有找到带 12 字节 token 的酷态科设备");
            return 4;
        }

        Console.WriteLine($"[OK] 目标设备: {target.Name} ({target.Model}), token 长度 {target.TokenHex.Length / 2} 字节");
        return 0;
    }

    /// <summary>
    /// 轮询的失败路径自测（无需真人扫码）：HTTP 非 200 = 过期、连接异常 + 超时 = 基于 Ttl 的兜底。
    /// 目标是证明这些路径都不会抛未捕获异常（UI 不会被带崩）。
    /// </summary>
    private static async Task<int> PollNegativeAsync()
    {
        using var client = new XiaomiClient("cn");
        client.Log += line => Console.WriteLine("  " + line);

        var cases = new (string Name, QrTicket Ticket, bool ExpectSuccess, string ExpectErrorPart)[]
        {
            ("空 PollUrl", new QrTicket { Ttl = TimeSpan.FromSeconds(5) }, false, "lp"),
            ("HTTP 404 -> 过期", new QrTicket
            {
                PollUrl = "https://account.xiaomi.com/lp/definitely-not-a-ticket",
                Ttl = TimeSpan.FromSeconds(10),
            }, false, "过期"),
            ("连接失败 + Ttl 超时", new QrTicket
            {
                PollUrl = "https://127.0.0.1:1/lp",
                Ttl = TimeSpan.FromSeconds(3),
            }, false, "超时"),
        };

        var failed = 0;
        foreach (var c in cases)
        {
            Console.WriteLine();
            Console.WriteLine($"--- 用例: {c.Name} (Ttl={c.Ticket.Ttl.TotalSeconds:F0}s) ---");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var scanned = await client.PollUntilScannedAsync(c.Ticket);
                sw.Stop();
                var error = client.LastError ?? "";
                var ok = scanned == c.ExpectSuccess
                         && (c.ExpectErrorPart.Length == 0 || error.Contains(c.ExpectErrorPart, StringComparison.Ordinal));
                Console.WriteLine($"  结果: scanned={scanned}, LastError=\"{error}\", 用时 {sw.Elapsed.TotalSeconds:F1}s -> {(ok ? "PASS" : "FAIL")}");
                if (!ok)
                {
                    failed++;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                Console.WriteLine($"  [FAIL] 抛出未捕获异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "[OK] 轮询失败路径均按预期返回, 未抛异常" : $"[FAIL] {failed} 个用例不符合预期");
        return failed == 0 ? 0 : 4;
    }

    /// <summary>
    /// 加密原语等价性对拍：用固定输入复现 fetch_token.py 的 _rc4_drop1024 / signed_nonce /
    /// generate_enc_params / encrypt_rc4，输出与 Python 侧逐字段比对（见 README）。
    /// </summary>
    private static void CryptoVectors()
    {
        const string ssecurity = "8XLjK2bTIS9t3vFiA1lEKQ==";
        const string nonce = "AAAAAAAAAAAAAAAAAAAAAA==";
        const string url = "https://api.io.mi.com/app/v2/homeroom/gethome";
        const string data = "{\"fg\": true, \"fetch_share\": true, \"fetch_share_dev\": true, \"limit\": 300, \"app_ver\": 7}";
        const string payload = "响应密文测试 payload";

        var sn = MiCrypto.SignedNonce(ssecurity, nonce);
        var fields = MiCrypto.GenerateEncParams(
            url, "POST", sn, nonce, new[] { new KeyValuePair<string, string>("data", data) }, ssecurity);

        var result = new Dictionary<string, object>
        {
            ["rc4"] = Convert.ToHexString(MiCrypto.Rc4Drop1024(
                Encoding.UTF8.GetBytes("0123456789abcdef"), Encoding.UTF8.GetBytes("hello xiaomi cloud"))).ToLowerInvariant(),
            ["signed_nonce"] = sn,
            ["enc_params"] = fields.ToDictionary(kv => kv.Key, kv => kv.Value),
            ["enc_payload"] = MiCrypto.EncryptRc4(sn, payload),
            ["decrypted"] = MiCrypto.DecryptRc4(sn, "0o9rr3RpSBtz0tVpETO3tAIvpWPgsyd6o+E="),
            ["key_to_hex_hex24"] = MiCrypto.KeyToHex("00112233445566778899aabb") ?? "<null>",
            ["key_to_hex_b64"] = MiCrypto.KeyToHex(Convert.ToBase64String(Encoding.UTF8.GetBytes("123456789012"))) ?? "<null>",
        };

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>默认输出目录：从 bin 目录往上找 DevCheck.csproj 所在的源码目录，落到它的 out\ 下。</summary>
    private static string DefaultOutDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DevCheck.csproj")))
            {
                return Path.Combine(dir.FullName, "out");
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "out");
    }

    /// <summary>读 PNG 的 IHDR 宽高（仅诊断用，不解码像素）。</summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24)
        {
            return (0, 0);
        }

        static int Be32(byte[] b, int offset)
            => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

        return (Be32(png, 16), Be32(png, 20));
    }
}
