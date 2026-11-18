using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Channels;
using CuktechMible;
using ProbeCs;
using Windows.Devices.Bluetooth.Advertisement;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help" || args[0] == "help"))
{
    PrintHelp();
    return 0;
}

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";
try
{
    switch (cmd)
    {
        case "scan":
            await ScanAsync(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 12);
            return 0;
        case "diag":
            return await DiagAsync();
        case "info":
            return await InfoAsync(Require(args, "--mac"));
        case "ctl":
            return await Ctl.RunAsync(args);
        case "probe":
            return await ProbeAsync(
                Require(args, "--mac"),
                Require(args, "--token"),
                args.Length > 2 && int.TryParse(LastArg(args), out var n) ? n : 10);
        default:
            PrintHelp();
            return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"!! 失败: {ex.GetType().Name}: {ex.Message} (HResult=0x{ex.HResult:x8})");
    Console.WriteLine(ex.StackTrace);
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
cuktech probe — 酷态科 10 号 Ultra BLE 探针 (M0)
  scan  [秒数]                    扫描周边 BLE 设备，标出小米 0xFE95 / productId 0x660E
  info  --mac AA:BB:CC:DD:EE:FF    只连不认证，读设备信息/固件版本
  probe --mac .. --token <24位hex> [采样次数]
                                   认证后轮询 C1/C2/C3/A 四个口的电压电流功率
  ctl   --mac .. --token <24位hex> -- <步骤...>
                                   控制命令实验台，步骤：get:piid / set:piid:value /
                                   ports:轮数 / sleep:毫秒（详见 Ctl.cs）
""");
}

static string Require(string[] a, string name)
{
    for (int i = 0; i < a.Length - 1; i++)
        if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
    throw new ArgumentException("缺少参数 " + name);
}

static string LastArg(string[] a) => a[^1];

// ---------------------------------------------------------------- scan

static async Task ScanAsync(int seconds)
{
    Console.WriteLine($"[scan] 开始扫描 {seconds}s ...");
    var seen = new Dictionary<ulong, Adv>();
    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

    watcher.Received += (_, e) =>
    {
        var a = e.Advertisement;
        byte[]? mi = null;
        foreach (var s in a.DataSections)
        {
            if (s.DataType != 0x16) continue;
            var p = s.Data.ToArray();
            if (p.Length >= 2 && p[0] == 0x95 && p[1] == 0xFE) { mi = p; break; }
        }
        ushort? product = null;
        if (mi is { Length: >= 4 } && mi[0] == 0x95 && mi[1] == 0xFE)
            product = (ushort)(mi[2] | (mi[3] << 8));

        var key = e.BluetoothAddress;
        if (!seen.TryGetValue(key, out var rec))
        {
            rec = new Adv { Address = key, Name = a.LocalName };
            seen[key] = rec;
        }
        rec.Rssi = e.RawSignalStrengthInDBm;
        if (!string.IsNullOrWhiteSpace(a.LocalName)) rec.Name = a.LocalName;
        if (product is not null) { rec.ProductId = product; rec.ServiceData = mi!; }
        foreach (var u in a.ServiceUuids)
        {
            var sid = WinRtMibleLink.ShortUuid(u);
            if (sid is not null) rec.Uuids.Add(sid.Value);
        }
        if (mi is not null) rec.RawMi = mi;
    };

    watcher.Start();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    watcher.Stop();

    Console.WriteLine();
    Console.WriteLine($"{"MAC",-20} {"RSSI",5}  {"productId",-10} {"0xFE95",-7} 名称");
    foreach (var r in seen.Values.OrderByDescending(x => x.Rssi))
    {
        var isCuktech = r.ProductId == 0x660E || r.Uuids.Contains(0xFE95);
        var mac = MacString(r.Address);
        Console.WriteLine(
            $"{(isCuktech ? "*" : " ")}{mac,-19} {r.Rssi,5}  " +
            $"{(r.ProductId is null ? "-" : "0x" + r.ProductId.Value.ToString("x4"))}      " +
            $"{(r.Uuids.Contains(0xFE95) ? "yes" : "-"),-7} {r.Name}");
        if (isCuktech && r.RawMi is not null)
            Console.WriteLine($"     mi-data: {Hex(r.RawMi)}");
    }
    Console.WriteLine();
    Console.WriteLine("提示：productId=0x660E 且带 0xFE95 的*行就是充电器。");
}

// ---------------------------------------------------------------- diag

static async Task<int> DiagAsync()
{
    Console.WriteLine("== 1. BluetoothAdapter.GetDefaultAsync ==");
    try
    {
        var adapter = await Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync();
        if (adapter is null) Console.WriteLine("   -> null（本进程看不到蓝牙适配器！）");
        else Console.WriteLine($"   -> {adapter.DeviceId}  status={adapter.IsLowEnergySupported}/{adapter.IsCentralRoleSupported}");
    }
    catch (Exception ex) { Console.WriteLine($"   !! {ex.GetType().Name} 0x{ex.HResult:x8} {ex.Message}"); }

    Console.WriteLine("== 2. Radio.GetRadiosAsync ==");
    try
    {
        var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
        foreach (var r in radios) Console.WriteLine($"   -> {r.Kind} {r.Name} state={r.State}");
        if (radios.Count == 0) Console.WriteLine("   -> 空列表（无可用无线电）");
    }
    catch (Exception ex) { Console.WriteLine($"   !! {ex.GetType().Name} 0x{ex.HResult:x8} {ex.Message}"); }

    Console.WriteLine("== 2b. 尝试打开蓝牙无线电 ==");
    try
    {
        var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
        Console.WriteLine($"   RequestAccessAsync -> {access}");
        var radios2 = await Windows.Devices.Radios.Radio.GetRadiosAsync();
        foreach (var r in radios2.Where(x => x.Kind == Windows.Devices.Radios.RadioKind.Bluetooth))
        {
            var res = await r.SetStateAsync(Windows.Devices.Radios.RadioState.On);
            Console.WriteLine($"   SetStateAsync(On) -> {res}; now state={r.State}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"   !! {ex.GetType().Name} 0x{ex.HResult:x8} {ex.Message}"); }

    Console.WriteLine("== 3. DeviceInformation 枚举 BLE 设备 ==");
    try
    {
        var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelector();
        var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
        Console.WriteLine($"   -> {infos.Count} 个已知设备");
        foreach (var i in infos.Take(25)) Console.WriteLine($"      {i.Name}  {i.Id}");
    }
    catch (Exception ex) { Console.WriteLine($"   !! {ex.GetType().Name} 0x{ex.HResult:x8} {ex.Message}"); }

    Console.WriteLine("== 4. 观察器(带 handler) ==");
    try
    {
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += (_, _) => { };
        w.Start();
        await Task.Delay(4000);
        Console.WriteLine($"   -> started, status={w.Status}");
        w.Stop();
    }
    catch (Exception ex) { Console.WriteLine($"   !! {ex.GetType().Name} 0x{ex.HResult:x8} {ex.Message}"); }
    return 0;
}

// ---------------------------------------------------------------- info

static async Task<int> InfoAsync(string mac)
{
    var addr = ParseMac(mac);
    Console.WriteLine($"[info] 连接 {mac} ...");
    await using var link = await WinRtMibleLink.ConnectAsync(addr);
    Console.WriteLine($"[info] 已连接，名称={link.Device.Name}");

    var q = Channel.CreateUnbounded<byte[]>();
    link.BytesReceived += (ch, data) =>
    {
        if (ch == MibleChannel.DeviceInfo) q.Writer.TryWrite(data);
    };

    await link.SubscribeAsync(MibleChannel.DeviceInfo);

    await link.WriteAsync(MibleChannel.DeviceInfo, new byte[] { 0x00 });
    var ver = await ReadQAsync(q, 3000);
    Console.WriteLine($"[info] 协议版本帧: {Hex(ver)}");

    await link.WriteAsync(MibleChannel.DeviceInfo, new byte[] { 0x03 });
    var chip = await ReadQAsync(q, 3000);
    Console.WriteLine($"[info] 芯片名帧:   {Hex(chip)}" +
                      (chip is null ? "" : $"  ascii=\"{Ascii(chip)}\""));
    if (ver is { Length: >= 3 })
        Console.WriteLine($"[info] => model = njcuk.fitting.ad1204_{Ascii(chip)?.Trim('\0')}  proto v{ver[1]}.{ver[2]}");

    var fw = await link.ReadShortAsync(0x0004);
    Console.WriteLine($"[info] 固件版本 = {Ascii(fw)}");

    Console.WriteLine("[info] 特征清单:");
    foreach (var kv in link.AllCharacteristics.OrderBy(k => k.Key))
        Console.WriteLine($"   0x{kv.Key:x4}  {kv.Value.CharacteristicProperties}");

    return 0;
}

// ---------------------------------------------------------------- probe

static async Task<int> ProbeAsync(string mac, string tokenHex, int rounds)
{
    var token = Convert.FromHexString(tokenHex.Replace(" ", "").Replace("-", ""));
    if (token.Length != 12) throw new ArgumentException($"token 必须 12 字节，实际 {token.Length}");

    var addr = ParseMac(mac);
    Console.WriteLine($"[probe] 连接 {mac} ...");
    await using var link = await WinRtMibleLink.ConnectAsync(addr);
    Console.WriteLine($"[probe] 已连接，名称={link.Device.Name}");

    var devInfoQ = Channel.CreateUnbounded<byte[]>();
    MibleSession? holder = null;
    link.BytesReceived += (ch, data) =>
    {
        if (ch == MibleChannel.DeviceInfo) devInfoQ.Writer.TryWrite(data);
        else holder?.OnNotification(ch, data);
    };

    // 先在明文通道上读设备信息（参考实现也是这个顺序）
    await link.SubscribeAsync(MibleChannel.DeviceInfo);
    var session = new MibleSession(link, token);
    holder = session;
    session.Trace = s => Console.WriteLine("[auth] " + s);
    session.PushReceived += m =>
        Console.WriteLine($"[push] piid={m.Piid} op={m.Opcode:x2} len={m.ValueBytes.Length} val={Hex(m.ValueBytes)}");

    await link.WriteAsync(MibleChannel.DeviceInfo, new byte[] { 0x00 });
    var v = await ReadQAsync(devInfoQ, 3000);
    await link.WriteAsync(MibleChannel.DeviceInfo, new byte[] { 0x03 });
    var c = await ReadQAsync(devInfoQ, 3000);
    var fw = await link.ReadShortAsync(0x0004);
    Console.WriteLine($"[probe] 协议={Hex(v)} 芯片={Ascii(c)} 固件={Ascii(fw)}");

    Console.WriteLine("[probe] 开始认证 ...");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await session.AuthenticateAsync();
    sw.Stop();
    Console.WriteLine($"[probe] 认证成功，用时 {sw.ElapsedMilliseconds} ms，app_key={Hex(session.AppKey.ToArray())}");

    // 读一轮静态配置
    foreach (var (piid, label) in new (ushort, string)[]
             {
                 (17, "c1c2_protocol"), (18, "c3a_protocol"), (16, "port_switch"),
                 (6, "screen_timeout"), (5, "scene_mode"), (13, "language"),
                 (20, "screen_orientation_lock"), (19, "idle_screen_off"), (21, "protocol_switch"),
             })
    {
        var m = await session.SendCommandAsync(2, piid, null, 6);
        Console.WriteLine($"[cfg] PIID{piid,-2} {label,-24} = " +
                          (m is null ? "<无响应>" : $"0x{(m.UIntValue ?? 0):x8} ({m.UIntValue}) raw={Hex(m.ValueBytes)}"));
        await Task.Delay(150);
    }

    Console.WriteLine();
    Console.WriteLine("[probe] 轮询端口功率（Ctrl+C 结束）");
    for (int i = 0; i < rounds; i++)
    {
        var line = new StringBuilder($"{DateTime.Now:HH:mm:ss.fff} ");
        for (ushort piid = 1; piid <= 4; piid++)
        {
            var m = await session.SendCommandAsync(2, piid, null, 6);
            var name = piid switch { 1 => "C1", 2 => "C2", 3 => "C3", 4 => "A", _ => "??" };
            var pd = m is null ? null : PortData.TryParse(m.ValueBytes);
            if (pd is PortData d)
                line.Append($"{name}:{(d.IsIdle ? "idle" : $"{d.Voltage,5:F1}V {d.Current,5:F2}A {d.Power,6:F1}W")}({(m is null ? "" : Hex(m.ValueBytes))}) ");
            else
                line.Append($"{name}:  ---  ");
        }
        Console.WriteLine(line.ToString());
    }

    Console.WriteLine("[probe] 收尾：退订 ...");
    foreach (var ch in new[] { MibleChannel.CmdRecv, MibleChannel.CmdSend, MibleChannel.DeviceInfo, MibleChannel.AuthData, MibleChannel.AuthCtrl })
    {
        try { await link.UnsubscribeAsync(ch); } catch { }
    }
    await Task.Delay(500);
    return 0;
}

// ---------------------------------------------------------------- helpers

static async Task<byte[]?> ReadQAsync(Channel<byte[]> q, int timeoutMs)
{
    using var cts = new CancellationTokenSource(timeoutMs);
    try { return await q.Reader.ReadAsync(cts.Token); }
    catch (OperationCanceledException) { return null; }
}

static string Hex(byte[]? b) => b is null ? "<null>" : Convert.ToHexString(b).ToLowerInvariant();

static string? Ascii(byte[]? b)
{
    if (b is null) return null;
    var s = Encoding.ASCII.GetString(b);
    var i = s.IndexOf('\0');
    return i >= 0 ? s[..i] : s;
}

static ulong ParseMac(string mac)
{
    var parts = mac.Split(':', '-', '.');
    if (parts.Length != 6) throw new ArgumentException("MAC 格式应为 AA:BB:CC:DD:EE:FF");
    ulong v = 0;
    foreach (var p in parts) v = (v << 8) | Convert.ToByte(p, 16);
    return v;
}

static string MacString(ulong a) => string.Join(":",
    Enumerable.Range(0, 6).Reverse().Select(i => ((a >> (i * 8)) & 0xFF).ToString("X2")));

internal sealed class Adv
{
    public ulong Address { get; init; }
    public string? Name { get; set; }
    public short Rssi { get; set; }
    public ushort? ProductId { get; set; }
    public byte[]? ServiceData { get; set; }
    public byte[]? RawMi { get; set; }
    public HashSet<ushort> Uuids { get; } = new();
}
