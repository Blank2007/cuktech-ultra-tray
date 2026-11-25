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

    private int _sceneMode = -1;

    /// <summary>当前充电模式（1=AI 2=数码生态 3=单口 4=均衡），-1 = 还没读到。</summary>
    public int SceneMode => Volatile.Read(ref _sceneMode);

    /// <summary>命令通道是否可用（已连接且认证完成）。</summary>
    public bool IsReady => _session is { Authenticated: true };

    // ==================== 设备设置（0.5.0 起，对齐 macOS 0.1.3） ====================

    /// <summary>最近一次读到的整份设备设置（读不到就是空的一份）。</summary>
    public DeviceSettingsState DeviceSettings { get; private set; } = new();

    // ==================== 单口采集（0.5.0 起） ====================

    /// <summary>正在采集的端口（0=C1 … 3=A），-1 = 没在采集。</summary>
    private int _capturePort = -1;

    private CaptureAccumulator? _captureStats;
    private DateTime _captureStartedAt;
    private DateTime _captureNextTick;
    private DateTime _captureLastGood;
    private DateTime _captureLastActive;
    private volatile bool _captureStop;
    private string _captureFirmware = "";

    /// <summary>是否正在单口采集。</summary>
    public bool Capturing => Volatile.Read(ref _capturePort) >= 0;

    /// <summary>正在采集的端口序号（0~3），没在采集是 -1。</summary>
    public int CapturePort => Volatile.Read(ref _capturePort);

    /// <summary>采集进度的快照（界面读这个，不要读正在累加的对象）。</summary>
    public CaptureAccumulator? CaptureStats => _captureStats?.Clone();

    /// <summary>采集进度：每秒一次，参数是累计器的快照。</summary>
    public event Action<CaptureAccumulator>? CaptureUpdated;

    /// <summary>采集结束：结果是内存里的一份，不落盘。</summary>
    public event Action<CaptureResult>? CaptureFinished;

    /// <summary>最近一次采集的结果（没采过是 null）。窗口不用订阅事件也能取到，
    /// 自己跑事件循环的自绘窗口靠它轮询刷新。</summary>
    public CaptureResult? LastCapture { get; private set; }

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

    /// <summary>充电模式的四个取值（顺序即下拉/卡片顺序）。</summary>
    public static readonly (int Value, string Name, string Hint)[] SceneModes =
    {
        (1, "AI", "插上什么就按什么给，自动分配功率"),
        (2, "数码生态", "优先照顾自家设备，出厂默认"),
        (3, "单口", "只给一路快充，其余让路"),
        (4, "均衡", "四路平分，谁都不落下"),
    };

    /// <summary>
    /// 切换充电模式（SIID=2 PIID=5）。
    /// 实测记录见 docs/control-protocol.md 3.1：SET 只回 ACK 不带新值，所以写完必须再 GET 回读确认。
    /// </summary>
    public async Task<ControlResult> SetSceneModeAsync(int mode, CancellationToken ct = default)
    {
        if (mode is < 1 or > 4) return ControlResult.Fail("充电模式只有 1~4（AI / 数码生态 / 单口 / 均衡）");
        var session = _session;
        if (session is null || !session.Authenticated) return ControlResult.Fail("还没连上充电器，请等面板显示「已连接」");

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var w = await session.SendCommandAsync(2, 5, (uint)mode, 6, ct).ConfigureAwait(false);
            if (w is null) return ControlResult.Fail("写入充电模式没有收到确认（设备无响应）");

            var back = await session.SendCommandAsync(2, 5, null, 4, ct).ConfigureAwait(false);
            int now = back?.ValueBytes is { Length: > 0 } b ? b[0] : -1;
            if (now < 0) return ControlResult.Fail("已写入，但回读失败，实际模式未知（请重新打开面板看看）");

            ApplySceneMode(now);
            if (now != mode) return ControlResult.Fail($"设备只接受了模式 {now}（请求 {mode}）");
            return ControlResult.Success();
        }
        catch (OperationCanceledException) { return ControlResult.Fail("操作被取消"); }
        catch (Exception ex) { return ControlResult.Fail("写入失败：" + Humanize(ex)); }
        finally { _cmdLock.Release(); }
    }

    /// <summary>读一次当前充电模式并推给界面（打开「模式」页时调用，能看到米家 App 改过的值）。</summary>
    public async Task<ControlResult> RefreshSceneModeAsync(CancellationToken ct = default)
    {
        var session = _session;
        if (session is null || !session.Authenticated) return ControlResult.Fail("还没连上充电器");

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var m = await session.SendCommandAsync(2, 5, null, 4, ct).ConfigureAwait(false);
            if (m?.ValueBytes is not { Length: > 0 } vb) return ControlResult.Fail("读取充电模式失败（设备无响应）");
            ApplySceneMode(vb[0]);
            return ControlResult.Success();
        }
        catch (OperationCanceledException) { return ControlResult.Fail("操作被取消"); }
        catch (Exception ex) { return ControlResult.Fail(Humanize(ex)); }
        finally { _cmdLock.Release(); }
    }

    /// <summary>把新的充电模式写进最近一帧并推给界面。</summary>
    private void ApplySceneMode(int mode)
    {
        Volatile.Write(ref _sceneMode, mode);
        var snap = Last.Clone();
        snap.SceneMode = mode;
        Publish(snap);
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

            var sm = await session.SendCommandAsync(2, 5, null, 4, ct).ConfigureAwait(false);
            if (sm?.ValueBytes is { Length: > 0 } vb)
            {
                Volatile.Write(ref _sceneMode, vb[0]);
                snap.SceneMode = vb[0];
            }

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

    /// <summary>
    /// 读一整份设备设置。逐条 GET，**读不到的留 null**（不同固件答得上来的项不一样，
    /// 倒计时在有的固件上直接超时）。全程握着命令通道，避免和轮询交错。
    /// </summary>
    public async Task<DeviceSettingsState> ReadDeviceSettingsAsync(CancellationToken ct = default)
    {
        var session = _session;
        if (session is null || !session.Authenticated) return DeviceSettings;

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var st = new DeviceSettingsState();

            async Task<uint?> GetAsync(ushort piid)
            {
                var m = await session.SendCommandAsync(2, piid, null, 3, ct).ConfigureAwait(false);
                return m?.UIntValue is uint v ? v : null;
            }

            if (await GetAsync(6).ConfigureAwait(false) is uint v6) st.ScreenTimeout = (int)v6;
            if (await GetAsync(19).ConfigureAwait(false) is uint v19) st.IdleScreenOff = v19 != 0;
            if (await GetAsync(20).ConfigureAwait(false) is uint v20) st.ScreenLock = v20 != 0;
            if (await GetAsync(15).ConfigureAwait(false) is uint v15) st.UsbLowCurrent = v15 != 0;
            if (await GetAsync(13).ConfigureAwait(false) is uint v13) st.Language = (int)v13;
            if (await GetAsync(21).ConfigureAwait(false) is uint v21) st.ProtocolFlags = v21;
            for (int i = 0; i < 4; i++)
            {
                if (await GetAsync((ushort)(9 + i)).ConfigureAwait(false) is uint vc) st.Countdowns[i] = (int)vc;
            }
            if (await GetAsync(17).ConfigureAwait(false) is uint v17)
            {
                var p = Negotiation.Split(v17);
                st.Negotiations[0] = p.High;
                st.Negotiations[1] = p.Low;
            }
            if (await GetAsync(18).ConfigureAwait(false) is uint v18)
            {
                var p = Negotiation.Split(v18);
                st.Negotiations[2] = p.High;
                st.Negotiations[3] = p.Low;
            }
            st.At = DateTime.Now;

            Log($"[settings] 读到 {st.LoadedCount} 项：息屏={st.ScreenTimeoutLabel} "
                + $"空闲息屏={Opt(st.IdleScreenOff)} 方向锁={Opt(st.ScreenLock)} A小电流={Opt(st.UsbLowCurrent)} "
                + $"语言={st.LanguageLabel} 协议位={(st.ProtocolFlags is uint pf ? "0x" + pf.ToString("X8") : "?")} "
                + $"倒计时={string.Join("/", st.Countdowns.Select(Opt))}");
            DeviceSettings = st;
            return st;
        }
        catch (OperationCanceledException) { return DeviceSettings; }
        catch (Exception ex)
        {
            Log("[settings] 读取失败：" + Humanize(ex));
            return DeviceSettings;
        }
        finally { _cmdLock.Release(); }
    }

    private static string Opt<T>(T? v) => v is null ? "?" : v.ToString() ?? "?";

    /// <summary>写单个属性的通用流程：SET → 回读 → 比对。</summary>
    /// <param name="strict">回读必须等于写入值。倒计时回读的可能是「剩余时间」，这种传 false。</param>
    private async Task<ControlResult> WriteSettingAsync(ushort piid, uint value, string name, bool strict, CancellationToken ct)
    {
        var session = _session;
        if (session is null || !session.Authenticated) return ControlResult.Fail("还没连上充电器，请等面板显示「已连接」");

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var w = await session.SendCommandAsync(2, piid, value, 6, ct).ConfigureAwait(false);
            if (w is null) return ControlResult.Fail("写入「" + name + "」没有收到确认（设备无响应）");

            var back = await session.SendCommandAsync(2, piid, null, 4, ct).ConfigureAwait(false);
            if (back?.UIntValue is not uint now)
                return ControlResult.Fail("「" + name + "」已写入，但回读失败，实际状态未知");

            Log($"[settings] {name} 写 {value}，回读 {now}");
            if (strict && now != value)
                return ControlResult.Fail($"「{name}」设备只接受了 {now}（请求 {value}），可能这一项不支持");
            return ControlResult.Success();
        }
        catch (OperationCanceledException) { return ControlResult.Fail("操作被取消"); }
        catch (Exception ex) { return ControlResult.Fail(Humanize(ex)); }
        finally { _cmdLock.Release(); }
    }

    /// <summary>息屏时间（PIID 6，取值见 <see cref="ScreenTimeouts"/>，1 和 5 是反的）。</summary>
    public async Task<ControlResult> SetScreenTimeoutAsync(int value, CancellationToken ct = default)
    {
        if (Array.IndexOf(ScreenTimeouts.Values, value) < 0)
            return ControlResult.Fail("息屏时间只有这五个取值（1 分 / 5 分 / 10 分 / 30 分 / 常亮）");
        var r = await WriteSettingAsync(6, (uint)value, "息屏时间", strict: true, ct).ConfigureAwait(false);
        if (r.Ok) { DeviceSettings.ScreenTimeout = value; PublishSettings(); }
        return r;
    }

    /// <summary>空闲息屏（PIID 19）：没插设备也关屏。</summary>
    public async Task<ControlResult> SetIdleScreenOffAsync(bool on, CancellationToken ct = default)
    {
        var r = await WriteSettingAsync(19, on ? 1u : 0u, "空闲息屏", strict: true, ct).ConfigureAwait(false);
        if (r.Ok) { DeviceSettings.IdleScreenOff = on; PublishSettings(); }
        return r;
    }

    /// <summary>屏幕方向锁（PIID 20）。</summary>
    public async Task<ControlResult> SetScreenLockAsync(bool on, CancellationToken ct = default)
    {
        var r = await WriteSettingAsync(20, on ? 1u : 0u, "方向锁", strict: true, ct).ConfigureAwait(false);
        if (r.Ok) { DeviceSettings.ScreenLock = on; PublishSettings(); }
        return r;
    }

    /// <summary>USB-A 小电流模式（PIID 15）。</summary>
    public async Task<ControlResult> SetUsbLowCurrentAsync(bool on, CancellationToken ct = default)
    {
        var r = await WriteSettingAsync(15, on ? 1u : 0u, "USB-A 小电流", strict: true, ct).ConfigureAwait(false);
        if (r.Ok) { DeviceSettings.UsbLowCurrent = on; PublishSettings(); }
        return r;
    }

    /// <summary>屏幕语言（PIID 13）：0=English 1=中文。</summary>
    public async Task<ControlResult> SetLanguageAsync(int value, CancellationToken ct = default)
    {
        if (value is not (0 or 1)) return ControlResult.Fail("语言只有 0=English / 1=中文");
        var r = await WriteSettingAsync(13, (uint)value, "语言", strict: true, ct).ConfigureAwait(false);
        if (r.Ok) { DeviceSettings.Language = value; PublishSettings(); }
        return r;
    }

    /// <summary>
    /// 开/关某口的某种快充协议（PIID 21 的 32 位掩码）。
    /// 必须读-改-写：读不到基线就拒绝写入 —— 按 0 兜底等于把别的口别的协议全关掉。
    /// </summary>
    public async Task<ControlResult> SetProtocolAsync(int portIndex, ProtocolCap cap, bool on, CancellationToken ct = default)
    {
        if (portIndex is < 0 or > 3) return ControlResult.Fail("端口序号只能是 0~3（C1/C2/C3/A）");
        if (cap.Bit(portIndex) is not uint bit)
            return ControlResult.Fail(Snapshot.PortNames[portIndex] + " 没有 " + cap.Label() + " 这一项");

        var session = _session;
        if (session is null || !session.Authenticated) return ControlResult.Fail("还没连上充电器，请等面板显示「已连接」");

        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var curMsg = await session.SendCommandAsync(2, 21, null, 4, ct).ConfigureAwait(false);
            if (curMsg?.UIntValue is not uint cur)
                return ControlResult.Fail("读取协议开关失败（设备无响应），为了不误改其它协议，本次没有写入");

            uint mask = 1u << (int)bit;
            uint next = on ? (cur | mask) : (cur & ~mask);

            if (await session.SendCommandAsync(2, 21, next, 6, ct).ConfigureAwait(false) is null)
                return ControlResult.Fail("写入协议开关没有收到确认（设备无响应）");

            var backMsg = await session.SendCommandAsync(2, 21, null, 4, ct).ConfigureAwait(false);
            if (backMsg?.UIntValue is not uint back)
                return ControlResult.Fail("已写入，但回读失败，实际状态未知");

            DeviceSettings.ProtocolFlags = back;
            Log($"[settings] 协议 {Snapshot.PortNames[portIndex]} {cap.Label()} -> 请求 0x{next:X8}，回读 0x{back:X8}");
            PublishSettings();
            if (back != next)
                return ControlResult.Fail($"设备只接受了 0x{back:X8}（请求 0x{next:X8}），可能这一项不支持");
            return ControlResult.Success();
        }
        catch (OperationCanceledException) { return ControlResult.Fail("操作被取消"); }
        catch (Exception ex) { return ControlResult.Fail(Humanize(ex)); }
        finally { _cmdLock.Release(); }
    }

    /// <summary>某口定时断电（PIID 9~12，分钟，0 = 取消）。回读可能是剩余时间，所以不严格要求相等。</summary>
    public async Task<ControlResult> SetCountdownAsync(int portIndex, int minutes, CancellationToken ct = default)
    {
        if (portIndex is < 0 or > 3) return ControlResult.Fail("端口序号只能是 0~3（C1/C2/C3/A）");
        if (minutes is < 0 or > 1440) return ControlResult.Fail("定时断电只能是 0~1440 分钟");
        var r = await WriteSettingAsync((ushort)(9 + portIndex), (uint)minutes,
            Snapshot.PortNames[portIndex] + " 定时断电", strict: false, ct).ConfigureAwait(false);
        if (r.Ok) { DeviceSettings.Countdowns[portIndex] = minutes; PublishSettings(); }
        return r;
    }

    /// <summary>设置改完推一帧给界面（面板/设置窗口据此立刻显示新值）。</summary>
    public event Action<DeviceSettingsState>? SettingsUpdated;

    private void PublishSettings()
    {
        var st = DeviceSettings.Clone();
        Post(() => SettingsUpdated?.Invoke(st));
    }

    /// <summary>
    /// 开始单口采集：每秒读这一个口，按分段积分算充入电量。
    /// 采集期间常规轮询让路（其它三口不刷新），避免两条命令挤一个通道被设备吞掉。
    /// </summary>
    public ControlResult StartCapture(int portIndex)
    {
        if (portIndex is < 0 or > 3) return ControlResult.Fail("端口序号只能是 0~3（C1/C2/C3/A）");
        if (Capturing) return ControlResult.Fail("已经在采集 " + Snapshot.PortNames[CapturePort] + " 了，先停止再换口");
        if (!IsReady) return ControlResult.Fail("还没连上充电器，请等面板显示「已连接」");

        var acc = new CaptureAccumulator();
        _captureStats = acc;
        _captureStop = false;
        _captureStartedAt = DateTime.Now;
        _captureNextTick = _captureStartedAt;
        _captureLastGood = _captureStartedAt;
        _captureLastActive = _captureStartedAt;
        LastCapture = null;
        Volatile.Write(ref _capturePort, portIndex);
        Log("[capture] 开始读 " + Snapshot.PortNames[portIndex] + "，每秒一次");
        return ControlResult.Success();
    }

    /// <summary>手动停止采集（结果照常送出，结束原因是「手动停止」）。</summary>
    public void StopCapture()
    {
        if (!Capturing) return;
        Log("[capture] 手动停止");
        _captureStop = true;
    }

    /// <summary>采集一拍：读指定口、累加、安排下一拍。返回 true 表示采集已经结束。</summary>
    private async Task<bool> CaptureTickAsync(MibleSession session, CancellationToken ct)
    {
        int port = CapturePort;
        if (port < 0) return true;

        var now = DateTime.Now;
        // 结束判断放在采样**前**：空载恰好 30 秒时这一拍不再多采一次，30 秒才算 30 次
        if (_captureStop) { FinishCapture(CaptureEndReason.Stopped); return true; }
        if (!session.Authenticated) { FinishCapture(CaptureEndReason.Disconnected); return true; }
        if ((now - _captureLastGood).TotalSeconds >= 30) { FinishCapture(CaptureEndReason.NoData); return true; }
        if ((now - _captureLastActive).TotalSeconds >= 30) { FinishCapture(CaptureEndReason.NoLoad); return true; }

        PortData? d = null;
        await _cmdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var m = await session.SendCommandAsync(2, (ushort)(port + 1), null, 2.0, ct).ConfigureAwait(false);
            if (m is not null) d = PortData.TryParse(m.ValueBytes);
        }
        finally { _cmdLock.Release(); }

        if (d is { } dd && _captureStats is { } stats)
        {
            var t = DateTime.Now;
            stats.Add(dd, t);
            _captureLastGood = t;
            if (dd.State != 0x00 && dd.Power > 0.05) _captureLastActive = t;

            // 同步这一口到快照，面板上的读数跟着秒变
            var snap = Last.Clone();
            var r = snap.Ports[port];
            r.HasData = true;
            r.Idle = dd.IsIdle;
            r.Volts = dd.Voltage;
            r.Amps = dd.Current;
            r.Watts = dd.Power;
            r.State = dd.State;
            r.Code = dd.Code;
            snap.At = t;
            Publish(snap);

            var progress = stats.Clone();
            Post(() => CaptureUpdated?.Invoke(progress));
        }

        // 下一拍按绝对时刻推进：一次 GET 往返 0.1~0.3 秒，算在这 1 秒里才是真的每秒一次
        _captureNextTick = _captureNextTick.AddSeconds(1);
        if (_captureNextTick < DateTime.Now) _captureNextTick = DateTime.Now; // 严重超时不补采，免得连发把设备挤爆

        while (!_captureStop)
        {
            double remain = (_captureNextTick - DateTime.Now).TotalMilliseconds;
            if (remain <= 0) break;
            await Task.Delay((int)Math.Min(200, remain), ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>结束采集并送出结果（只走内存，不落盘）。</summary>
    private void FinishCapture(CaptureEndReason reason)
    {
        int port = CapturePort;
        var stats = _captureStats;
        if (port < 0 || stats is null) return;

        Volatile.Write(ref _capturePort, -1);
        _captureStats = null;

        var result = new CaptureResult
        {
            PortIndex = port,
            PortName = Snapshot.PortNames[port],
            StartedAt = _captureStartedAt,
            EndedAt = DateTime.Now,
            Stats = stats.Clone(),
            Reason = reason,
            Firmware = _captureFirmware,
        };
        LastCapture = result;
        Log($"[capture] 结束 {result.PortName}：{result.Stats.SampleCount} 次采样，"
            + $"{result.EnergyText}Wh，平均 {result.AverageText}W，{result.ReasonText}");
        Post(() => CaptureFinished?.Invoke(result));
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
                if (Capturing) FinishCapture(CaptureEndReason.Disconnected);
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

        Volatile.Write(ref _sceneMode, -1);
        try
        {
            var sm = await session.SendCommandAsync(2, 5, null, 3, sessionCt).ConfigureAwait(false);
            if (sm?.ValueBytes is { Length: > 0 } sb) Volatile.Write(ref _sceneMode, sb[0]);
            Log($"[cfg] scene_mode={SceneMode}");
        }
        catch { }

        _captureFirmware = firmware;

        while (!sessionCt.IsCancellationRequested)
        {
            if (_linkLost.IsSet) throw new IOException("设备断开了连接");

            // 采集期间只读被采的那一口：两条命令挤一个通道会被设备吞掉
            if (Capturing)
            {
                await CaptureTickAsync(session, sessionCt).ConfigureAwait(false);
                continue;
            }

            var tickStart = DateTime.Now;
            int mask = Volatile.Read(ref _portSwitch);
            var snap = new Snapshot
            {
                Connected = true,
                Status = "已连接",
                Firmware = firmware,
                PortSwitch = mask,
                SceneMode = Volatile.Read(ref _sceneMode),
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

            // 0.5.0：按绝对时刻定拍 —— 以前是「采完再睡 PollMs」，而一轮四口要 0.4~1.2 秒，
            // 实际间隔变成 PollMs + 采样耗时，设 1 秒其实在 1.5 秒左右。
            // 现在把采样耗时从等待里扣掉，设备慢的时候最多少睡，不会越拖越远。
            int period = _pushHint ? 300 : Math.Clamp(_settings.PollMs, 300, 10000);
            _pushHint = false;
            int elapsed = (int)(DateTime.Now - tickStart).TotalMilliseconds;
            await Task.Delay(Math.Max(150, period - elapsed), sessionCt).ConfigureAwait(false);
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
