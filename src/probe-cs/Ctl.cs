using System.Globalization;
using System.Text;
using System.Threading.Channels;
using CuktechMible;

namespace ProbeCs;

/// <summary>
/// 控制命令实测台：连接 → 认证 → 按顺序执行「实验步骤」，把每一步的请求与回读都打印出来。
/// 用法：
///   probe-cs ctl --mac AA:BB:CC:DD:EE:FF --token &lt;24位hex&gt; -- get:16 set:16:7 get:16 ports:3 sleep:500
/// 步骤语法（每个步骤是一个独立参数，用冒号分隔）：
///   get:piid                 读 SIID=2
///   get:siid:piid            读任意 SIID
///   set:piid:value           写 SIID=2（value 可写 0x0f / 15 / 7 三种形式）
///   set:siid:piid:value      写任意 SIID
///   ports:轮数               每轮读 C1/C2/C3/A 四个口，间隔 1s
///   sleep:毫秒               等待
/// </summary>
internal static class Ctl
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? mac = null, tokenHex = null;
        var steps = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                for (int j = i + 1; j < args.Length; j++) steps.Add(args[j]);
                break;
            }
            if (args[i].Equals("--mac", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { mac = args[++i]; continue; }
            if (args[i].Equals("--token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { tokenHex = args[++i]; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) steps.Add(args[i]);
        }
        if (mac is null) throw new ArgumentException("缺少 --mac");
        if (tokenHex is null) throw new ArgumentException("缺少 --token");

        var token = Convert.FromHexString(tokenHex.Replace(" ", "").Replace("-", ""));
        if (token.Length != 12) throw new ArgumentException($"token 必须 12 字节，实际 {token.Length}");

        Console.WriteLine($"[ctl] 连接 {mac} ...");
        await using var link = await WinRtMibleLink.ConnectAsync(ParseMac(mac));
        var devInfoQ = Channel.CreateUnbounded<byte[]>();
        MibleSession? holder = null;
        link.BytesReceived += (ch, data) =>
        {
            if (ch == MibleChannel.DeviceInfo) devInfoQ.Writer.TryWrite(data);
            else holder?.OnNotification(ch, data);
        };

        await link.SubscribeAsync(MibleChannel.DeviceInfo);
        var session = new MibleSession(link, token);
        holder = session;
        session.Trace = s => Console.WriteLine("[auth] " + s);
        session.PushReceived += m =>
            Console.WriteLine($"[push] {DateTime.Now:HH:mm:ss.fff} siid={m.Siid} piid={m.Piid} op=0x{m.Opcode:x2} val={Hex(m.ValueBytes)}");

        var fw = await link.ReadShortAsync(0x0004);
        Console.WriteLine($"[ctl] 已连接，固件={Ascii(fw)}");
        Console.WriteLine("[ctl] 认证中 ...");
        await session.AuthenticateAsync();
        Console.WriteLine("[ctl] 认证成功");
        await Task.Delay(200);

        foreach (var step in steps)
        {
            try
            {
                await RunStepAsync(session, step);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ctl] !! 步骤 {step} 异常 {ex.GetType().Name}: {ex.Message}");
            }
            await Task.Delay(150);
        }

        Console.WriteLine("[ctl] 收尾：退订 ...");
        foreach (var ch in new[] { MibleChannel.CmdRecv, MibleChannel.CmdSend, MibleChannel.DeviceInfo, MibleChannel.AuthData, MibleChannel.AuthCtrl })
        {
            try { await link.UnsubscribeAsync(ch); } catch { }
        }
        await Task.Delay(300);
        return 0;
    }

    private static async Task RunStepAsync(MibleSession session, string step)
    {
        var p = step.Split(':');
        switch (p[0].ToLowerInvariant())
        {
            case "get":
            {
                byte siid = 2;
                ushort piid;
                if (p.Length == 2) piid = ushort.Parse(p[1], CultureInfo.InvariantCulture);
                else if (p.Length == 3) { siid = byte.Parse(p[1], CultureInfo.InvariantCulture); piid = ushort.Parse(p[2], CultureInfo.InvariantCulture); }
                else throw new ArgumentException("get 需要 get:piid 或 get:siid:piid");
                var m = await session.SendCommandAsync(siid, piid, null, 6);
                Console.WriteLine($"[get] {DateTime.Now:HH:mm:ss.fff} siid={siid} piid={piid} -> {Describe(m)}");
                break;
            }
            case "set":
            {
                byte siid = 2;
                ushort piid;
                uint value;
                if (p.Length == 3) { piid = ushort.Parse(p[1], CultureInfo.InvariantCulture); value = ParseValue(p[2]); }
                else if (p.Length == 4) { siid = byte.Parse(p[1], CultureInfo.InvariantCulture); piid = ushort.Parse(p[2], CultureInfo.InvariantCulture); value = ParseValue(p[3]); }
                else throw new ArgumentException("set 需要 set:piid:value 或 set:siid:piid:value");
                var m = await session.SendCommandAsync(siid, piid, value, 8);
                Console.WriteLine($"[set] {DateTime.Now:HH:mm:ss.fff} siid={siid} piid={piid} value={value} (0x{value:x}) -> {Describe(m)}");
                break;
            }
            case "ports":
            {
                int rounds = p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : 1;
                for (int i = 0; i < rounds; i++)
                {
                    var line = new StringBuilder($"[ports] {DateTime.Now:HH:mm:ss.fff} ");
                    for (ushort piid = 1; piid <= 4; piid++)
                    {
                        var m = await session.SendCommandAsync(2, piid, null, 6);
                        var name = piid switch { 1 => "C1", 2 => "C2", 3 => "C3", 4 => "A", _ => "??" };
                        var pd = m is null ? null : PortData.TryParse(m.ValueBytes);
                        line.Append(pd is PortData d
                            ? $"{name}[{Hex(m!.ValueBytes)}]{(d.IsIdle ? "idle" : $"{d.Voltage:F1}V/{d.Current:F2}A/{d.Power:F1}W")} "
                            : $"{name}[无响应] ");
                    }
                    Console.WriteLine(line.ToString());
                    if (i + 1 < rounds) await Task.Delay(1000);
                }
                break;
            }
            case "sleep":
                await Task.Delay(int.Parse(p[1], CultureInfo.InvariantCulture));
                break;
            default:
                throw new ArgumentException($"未知步骤 {step}");
        }
    }

    private static string Describe(MiotMessage? m)
    {
        if (m is null) return "<无响应/超时>";
        return $"op=0x{m.Opcode:x2} err=0x{m.ErrorCode:x2} type={m.TypeId} raw={Hex(m.ValueBytes)} u32={(m.UIntValue?.ToString(CultureInfo.InvariantCulture) ?? "-")}";
    }

    private static uint ParseValue(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : uint.Parse(s, CultureInfo.InvariantCulture);

    private static string Hex(byte[]? b) => b is null || b.Length == 0 ? "<空>" : Convert.ToHexString(b).ToLowerInvariant();

    private static string? Ascii(byte[]? b)
    {
        if (b is null) return null;
        var s = Encoding.ASCII.GetString(b);
        var i = s.IndexOf('\0');
        return i >= 0 ? s[..i] : s;
    }

    private static ulong ParseMac(string mac)
    {
        var parts = mac.Split(':', '-', '.');
        if (parts.Length != 6) throw new ArgumentException("MAC 格式应为 AA:BB:CC:DD:EE:FF");
        ulong v = 0;
        foreach (var p in parts) v = (v << 8) | Convert.ToByte(p, 16);
        return v;
    }
}
