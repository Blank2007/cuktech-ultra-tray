using System.Runtime.InteropServices;
using System.Text;
using CuktechMible;
using ProbeCs;

namespace CuktechTray;

/// <summary>
/// 后台设备客户端：连接 → 认证 → 轮询四口功率，掉线自动重连。
/// 所有 UI 事件都通过构造时捕获的 SynchronizationContext 回到界面线程。
/// </summary>
internal sealed class DeviceClient
{
    private readonly Settings _settings;
    private readonly SynchronizationContext? _ui;
    private readonly ManualResetEventSlim _linkLost = new(false);

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _sessionCts;
    private Task? _loop;

    public DeviceClient(Settings settings, SynchronizationContext? ui)
    {
        _settings = settings;
        _ui = ui;
        WinRtMibleLink.Diag = s => Log(s);
    }

    public event Action<Snapshot>? Updated;

    public Snapshot Last { get; private set; } = new();

    // ==================== 控制命令（新增，未改动既有方法签名） ====================

    /// <summary>控制命令结果：Ok=false 时 <see cref="Error"/> 是给人看的原因文本。</summary>
    public readonly record struct ControlResult(bool Ok, string Error)
    {
        public static ControlResult Success() => new(true, "");
        public static ControlResult Fail(string error) => new(false, error);
    }

    /// <summary>串行化命令通道：轮询与手动控制不能同时在跑，否则会读到彼此的响应。</summary>
    private readonly SemaphoreSlim _cmdLock = new(1, 1);

    private MibleSession? _session;
    private int _portSwitch = 0x0F;

    /// <summary>当前端口开关位掩码（bit0=C1 bit1=C2 bit2=C3 bit3=USB-A），1=该口通电。</summary>
    public int PortSwitch => Volatile.Read(ref _portSwitch);

    /// <summary>命令通道是否可用（已连接且认证完成）。</summary>
    public bool IsReady => _session is { Authenticated: true };

    /// <summary>
    /// 设置某一路端口的通断（读-改-写，不会影响其它口）。
    /// 实测：SET 的回帧只有 ACK 不带新值，所以写完必须再 GET 一次回读确认。
    /// </summary>
    /// <param name="portIndex">0=C1 1=C2 2=C3 3=USB-A</param>
    public async Task<ControlResult> SetPortEnabledAsync(int portIndex, bool enabled, CancellationToken ct = default)
    {
        if (portIndex is < 0 or > 3) return ControlResult.Fail("端口序号只能是 0~3（C1/C2/C3/A）");
        var session = _session;
        if (session is null || !session.Authenticated) return ControlResult.Fail("还没连上充电器，请等面板显示「已连接」");

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 先读基线：GET 失败绝不能按 0 兜底，否则会把其它口一起关掉
            var cur = await session.SendCommandAsync(2, 16, null, 4, ct).ConfigureAwait(false);
            if (cur?.UIntValue is not uint curMask)
                return ControlResult.Fail("读取端口开关失败（设备无响应），为了不误关其它口，本次没有写入");

            int bit = 1 << portIndex;
            int next = enabled ? (int)curMask | bit : (int)curMask & ~bit;

            var w = await session.SendCommandAsync(2, 16, (uint)next, 6, ct).ConfigureAwait(false);
            if (w is null) return ControlResult.Fail("写入端口开关没有收到确认（设备无响应）");

            var back = await session.SendCommandAsync(2, 16, null, 4, ct).ConfigureAwait(false);
            if (back?.UIntValue is not uint now)
                return ControlResult.Fail("已写入，但回读失败，实际状态未知（请点「刷新」查看）");

            ApplyPortSwitch((int)now);
            if ((int)now != next)
                return ControlResult.Fail($"设备只接受了 0x{now:x2}（请求 0x{next:x2}），可能是数值越界或该口不支持");
            return ControlResult.Success();
        }
        catch (OperationCanceledException) { return ControlResult.Fail("操作被取消"); }
        catch (Exception ex) { return ControlResult.Fail("写入失败：" + Humanize(ex)); }
        finally { _cmdLock.Release(); }
    }

    /// <summary>
    /// 直接写整个端口开关位掩码（0x00~0x0F）。写完整掩码时由调用方负责正确性，
    /// 逐口开关请用 <see cref="SetPortEnabledAsync"/>。
    /// </summary>
    public async Task<ControlResult> SetPortSwitchAsync(int mask, CancellationToken ct = default)
    {
        if (mask is < 0 or > 0x0F) return ControlResult.Fail($"端口开关只有低 4 位有效，0x{mask:x} 越界");
        var session = _session;
        if (session is null || !session.Authenticated) return ControlResult.Fail("还没连上充电器，请等面板显示「已连接」");

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var w = await session.SendCommandAsync(2, 16, (uint)mask, 6, ct).ConfigureAwait(false);
            if (w is null) return ControlResult.Fail("写入端口开关没有收到确认（设备无响应）");

            var back = await session.SendCommandAsync(2, 16, null, 4, ct).ConfigureAwait(false);
            if (back?.UIntValue is not uint now)
                return ControlResult.Fail("已写入，但回读失败，实际状态未知（请点「刷新」查看）");

            ApplyPortSwitch((int)now);
            if ((int)now != mask)
                return ControlResult.Fail($"设备只接受了 0x{now:x2}（请求 0x{mask:x2}）");
            return ControlResult.Success();
        }
        catch (OperationCanceledException) { return ControlResult.Fail("操作被取消"); }
        catch (Exception ex) { return ControlResult.Fail("写入失败：" + Humanize(ex)); }
        finally { _cmdLock.Release(); }
    }

    /// <summary>立刻读一次端口开关 + 四口数据并推一帧给界面（控制页「刷新」按钮用）。</summary>
    public async Task<ControlResult> RefreshAsync(CancellationToken ct = default)
    {
        var session = _session;
        if (session is null || !session.Authenticated) return ControlResult.Fail("还没连上充电器，请等面板显示「已连接」");

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snap = Last.Clone();
            snap.Connected = true;
            snap.Status = "已连接";
            snap.Error = null;

            int mask = Volatile.Read(ref _portSwitch);
            var sw = await session.SendCommandAsync(2, 16, null, 4, ct).ConfigureAwait(false);
            if (sw?.UIntValue is uint m16) mask = (int)m16;
            Volatile.Write(ref _portSwitch, mask);
            snap.PortSwitch = mask;

            int ok = 0;
            for (ushort piid = 1; piid <= 4; piid++)
            {
                var reading = snap.Ports[piid - 1];
                reading.Enabled = (mask & (1 << (piid - 1))) != 0;
                var msg = await session.SendCommandAsync(2, piid, null, 2.5, ct).ConfigureAwait(false);
                if (msg is not null && PortData.TryParse(msg.ValueBytes) is { } d)
                {
                    ok++;
                    reading.HasData = true;
                    reading.Idle = d.IsIdle;
                    reading.Volts = d.Voltage;
                    reading.Amps = d.Current;
                    reading.Watts = d.Power;
                    reading.State = d.State;
                    reading.Code = d.Code;
                }
            }
            snap.At = DateTime.Now;
            Publish(snap);
            return ok == 0 ? ControlResult.Fail("四个端口都没有应答") : ControlResult.Success();
        }
        catch (OperationCanceledException) { return ControlResult.Fail("操作被取消"); }
        catch (Exception ex) { return ControlResult.Fail(Humanize(ex)); }
        finally { _cmdLock.Release(); }
    }

    /// <summary>把新的端口开关值写进最近一帧并推给界面。</summary>
    private void ApplyPortSwitch(int mask)
    {
        Volatile.Write(ref _portSwitch, mask);
        var snap = Last.Clone();
        snap.PortSwitch = mask;
        for (int i = 0; i < snap.Ports.Length && i < 4; i++)
            snap.Ports[i].Enabled = (mask & (1 << i)) != 0;
        Publish(snap);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Restart()
    {
        try { _sessionCts?.Cancel(); } catch { }
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            _linkLost.Reset();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sessionCt = _sessionCts.Token;
            try
            {
                await RunSessionAsync(sessionCt, ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 手动重连：立刻进入下一轮
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Log($"[fail#{failures}] {ex.GetType().Name}: {ex.Message}");
                PublishDisconnected(Humanize(ex));
                // 连不上（设备没电 / 不在范围 / 蓝牙关）是常态：快速重试，别让用户等 30 秒；
                // 认证失败才需要长退避，避免把设备打挂。
                var delayMs = ex is MibleAuthException
                    ? Math.Min(120000, 5000 * (1 << Math.Min(failures - 1, 4)))
                    : Math.Min(8000, 2000 * (1 << Math.Min(failures - 1, 2)));
                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                _session = null; // 会话结束，控制命令不可再用
                _sessionCts?.Dispose();
                _sessionCts = null;
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken sessionCt, CancellationToken appCt)
    {
        var addr = ParseMac(_settings.Mac);
        PublishStatus("正在连接…", connected: false);

        await using var link = await WinRtMibleLink.ConnectAsync(addr, sessionCt).ConfigureAwait(false);
        link.Disconnected += () => _linkLost.Set();

        string firmware = "";
        try
        {
            var fw = await link.ReadShortAsync(0x0004, sessionCt).ConfigureAwait(false);
            firmware = Ascii(fw);
        }
        catch { }

        var session = new MibleSession(link, _settings.TokenBytes);
        _session = session;
        link.BytesReceived += (ch, data) => session.OnNotification(ch, data);
        session.PushReceived += OnPush;

        PublishStatus("正在认证…", connected: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.AuthenticateAsync(sessionCt).ConfigureAwait(false);
        Log($"[ok] 认证完成 {sw.ElapsedMilliseconds}ms 固件={firmware}");

        Volatile.Write(ref _portSwitch, 0x0F);
        try
        {
            var m = await session.SendCommandAsync(2, 16, null, 3, sessionCt).ConfigureAwait(false);
            if (m?.UIntValue is uint u) Volatile.Write(ref _portSwitch, (int)u);
            Log($"[cfg] port_switch=0x{PortSwitch:x2}");
        }
        catch { }

        while (!sessionCt.IsCancellationRequested)
        {
            if (_linkLost.IsSet) throw new IOException("设备断开了连接");

            int mask = Volatile.Read(ref _portSwitch);
            var snap = new Snapshot
            {
                Connected = true,
                Status = "已连接",
                Firmware = firmware,
                PortSwitch = mask,
            };
            int ok = 0;
            // 读四口也进命令锁：控制页可能同时在下发写入，同一个会话里命令不能交错
            await _cmdLock.WaitAsync(sessionCt).ConfigureAwait(false);
            try
            {
                mask = Volatile.Read(ref _portSwitch);
                snap.PortSwitch = mask;
                for (ushort piid = 1; piid <= 4; piid++)
                {
                    var reading = snap.Ports[piid - 1];
                    reading.Enabled = (mask & (1 << (piid - 1))) != 0;
                    reading.HasData = false;
                    var m = await session.SendCommandAsync(2, piid, null, 2.5, sessionCt).ConfigureAwait(false);
                    if (m is not null && PortData.TryParse(m.ValueBytes) is { } d)
                    {
                        ok++;
                        reading.HasData = true;
                        reading.Idle = d.IsIdle;
                        reading.Volts = d.Voltage;
                        reading.Amps = d.Current;
                        reading.Watts = d.Power;
                        reading.State = d.State;
                        reading.Code = d.Code;
                    }
                }
            }
            finally { _cmdLock.Release(); }
            if (ok == 0) throw new IOException("四个口都没有应答");

            snap.At = DateTime.Now;
            Publish(snap);

            // 设备推送过端口变化就在 300ms 后立刻补一轮，否则按配置间隔轮询
            int delay = _pushHint ? 300 : Math.Clamp(_settings.PollMs, 300, 10000);
            _pushHint = false;
            await Task.Delay(delay, sessionCt).ConfigureAwait(false);
        }
    }

    private bool _pushHint;

    private void OnPush(MiotMessage m)
    {
        // 设备推送的端口帧只有 1~2 字节（不完整），只能当"该口有变化"的提示：
        // 主动打断当前 1s 等待，让面板更快刷新。
        if (m.Siid == 2 && m.Piid is >= 1 and <= 4) _pushHint = true;
    }

    private void Publish(Snapshot snap)
    {
        Last = snap;
        Post(() => Updated?.Invoke(snap));
    }

    private void PublishStatus(string status, bool connected)
    {
        var snap = Last.Clone();
        snap.Status = status;
        snap.Connected = connected;
        snap.Error = null;
        Last = snap;
        Post(() => Updated?.Invoke(snap));
    }

    private void PublishDisconnected(string error)
    {
        var snap = Last.Clone();
        snap.Connected = false;
        snap.Status = "未连接";
        snap.Error = error;
        foreach (var p in snap.Ports) { p.HasData = false; p.Idle = true; }
        Last = snap;
        Post(() => Updated?.Invoke(snap));
    }

    private void Post(Action a)
    {
        if (_ui is null) a();
        else _ui.Post(_ => a(), null);
    }

    /// <summary>把底层异常翻成用户看得懂的一句话（完整堆栈在日志里）。</summary>
    private static string Humanize(Exception ex) => ex switch
    {
        MibleAuthException => "认证失败 · token 可能已失效，请右键「重新配置设备」",
        InvalidOperationException e when e.Message.Contains("返回 null") =>
            "没找到充电器 · 确认它已通电、在蓝牙范围内",
        COMException e when (uint)e.HResult is 0x800710DF or 0x80070005 =>
            "蓝牙没开 · 请在系统设置里打开蓝牙",
        IOException e when e.Message.Contains("断开") => "连接断开 · 正在重连",
        IOException e when e.Message.Contains("应答") => "充电器没响应 · 正在重试",
        _ => ex.Message,
    };

    public static byte[]? AsciiBytes(string s) => Encoding.ASCII.GetBytes(s);

    private static string Ascii(byte[]? b)
    {
        if (b is null || b.Length == 0) return "";
        var s = Encoding.ASCII.GetString(b);
        var i = s.IndexOf('\0');
        return (i >= 0 ? s[..i] : s).Trim();
    }

    public static ulong ParseMac(string mac)
    {
        var parts = mac.Split(':', '-', '.');
        if (parts.Length != 6) throw new ArgumentException("MAC 格式应为 AA:BB:CC:DD:EE:FF");
        ulong v = 0;
        foreach (var p in parts) v = (v << 8) | Convert.ToByte(p, 16);
        return v;
    }

    private static void Log(string s)
    {
        LogSink.Write(s);
        // 生产环境不落地到控制台，调试时可用调试器查看
    }
}
