using System.Drawing;
using System.Drawing.Drawing2D;

namespace CuktechTray;

/// <summary>
/// 设备设置（0.5.0）：改的是充电器自己的设置（息屏、四口定时断电、快充协议开关），
/// 不是这个程序的配置。照 macOS 0.1.3 的 SettingsWindow 搬过来，判据也同一套：
/// 读不到的项一律显示「—」并把控件灰掉 —— 把「没读到」当成「关着」，用户一点就会写进一个他不知道的值。
/// <para>
/// 写流程固定是「写 → 回读 → 比对」（在 DeviceClient 里做），所以这里写完一定要重读一遍整份设置，
/// 界面永远显示设备的真实状态，而不是我们以为写进去的那个值。
/// </para>
/// </summary>
internal sealed class DeviceSettingsWindow : WinBase
{
    // ---- 设计尺寸（96 DPI 逻辑像素，按 DPI 缩放）----
    private const int DesignW = 400;
    private const int DesignH = 676;
    private const int PadX = 24;
    private const int RowH = 32;
    private const int BtnH = 40;
    private const int BtnY = DesignH - PadX - BtnH;   // 612

    private const int TitleY = 20;
    private const int SubY = 48;
    private const int Sec1Y = 76;
    private const int Rows1Y = 100;                    // 屏幕 4 行：100..228
    private const int Sec2Y = 234;
    private const int Rows2Y = 258;                    // 端口 5 行（四口定时 + USB-A 小电流）：258..418
    private const int Sec3Y = 424;
    private const int Rows3Y = 448;                    // 协议 4 行：448..576
    private const int StatusY = 588;

    private const int ChipH = 24;
    private const int ChipGap = 6;
    private const int SwitchW = 40;
    private const int SwitchH = 22;
    // 各种胶囊宽度（0.5.3 提取成常量：绘制和命中现在共用同一批矩形函数）
    private const int ScreenChipW = 50;
    private const int CountdownChipX = 84;
    private const int CountdownChipW = 80;
    private const int ProtocolChipW = 54;
    private const int LangCnW = 56;
    private const int LangEnW = 74;
    private const int BtnGap = 8;
    private const int BtnW = 88;

    // ---- 配色（跟主面板同一套）----
    private static readonly Color BgTop = Color.FromArgb(34, 38, 45);
    private static readonly Color BgBottom = Color.FromArgb(20, 23, 28);
    private static readonly Color BorderColor = Color.FromArgb(58, 64, 74);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = WattStyle.Dim;
    private static readonly Color Faint = Color.FromArgb(110, 117, 126);
    private static readonly Color Ok = WattStyle.Ok;
    private static readonly Color Warn = WattStyle.Warn;
    private static readonly Color Bad = WattStyle.Bad;
    private static readonly Color SepColor = Color.FromArgb(44, 49, 57);
    private static readonly Color ChipBg = Color.FromArgb(34, 39, 47);
    private static readonly Color ChipOnBg = Color.FromArgb(30, 60, 50);
    private static readonly Color ChipOffBg = Color.FromArgb(26, 30, 36);
    private static readonly Color TrackOff = Color.FromArgb(58, 63, 72);

    private enum Kind
    {
        None, ScreenChip, IdleSwitch, LockSwitch, LangChip,
        Countdown, UsbSwitch, ProtocolChip, Refresh, Close,
    }

    private readonly record struct Hot(Kind Kind, int A, int B, Rectangle Rect);

    private static readonly Hot NoHot = new(Kind.None, -1, -1, Rectangle.Empty);

    private readonly DeviceClient _client;

    private DeviceSettingsState _state = new();
    private Task<DeviceSettingsState>? _read;
    private Task<DeviceClient.ControlResult>? _write;
    private string _writeWhat = "";
    private DateTime _lastAutoTry = DateTime.MinValue;

    private string _status = "";
    private Color _statusColor = Faint;

    private Hot _hover = NoHot;
    private Hot _press = NoHot;
    private List<Hot> _hots = new();

    private GdiFont _fTitle = null!, _fSub = null!, _fHead = null!, _fLabel = null!,
                    _fVal = null!, _fChip = null!, _fBtn = null!, _fNote = null!;
    private float _fontBase;

    public DeviceSettingsWindow(DeviceClient client)
    {
        _client = client;
        CreateWin(DesignW, DesignH, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, IntPtr.Zero, "settings");
        if (Hwnd == IntPtr.Zero)
        {
            LogSink.Write("[settings-ui] CreateWin 失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return;
        }
        ApplyScale();
        SetRoundedRegion(S(14));
        LogSink.Write("[settings-ui] 建立 ok hwnd=" + Hwnd + " scale=" + Scale.ToString("F2"));
    }

    // 截断而不是四舍五入：跟面板一致，1.25 倍下累加横坐标不会一格一格错开
    private int S(float v) => (int)(v * Scale);

    private int RadiusPx => S(14);

    /// <summary>渲染自查模式：把「连着设备」当作成立，好让截图里控件是亮着的。</summary>
    private bool _demo;

    /// <summary>现在能不能下发命令：连着、认过证、并且没有正在跑的读写。</summary>
    private bool CanWrite => _demo || (_client.IsReady && _write is null);

    private void ApplyScale()
    {
        DisposeFonts();
        float s = Scale;
        float fb = _fontBase > 0f ? _fontBase : s;
        _fTitle = GdiFont.Create("Microsoft YaHei UI", 14, s, bold: true, fontBase: fb);
        _fSub = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fHead = GdiFont.Create("Microsoft YaHei UI", 11, s, bold: true, fontBase: fb);
        _fLabel = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fVal = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fChip = GdiFont.Create("Microsoft YaHei UI", 10, s, fontBase: fb);
        _fBtn = GdiFont.Create("Microsoft YaHei UI", 11, s, bold: true, fontBase: fb);
        _fNote = GdiFont.Create("Microsoft YaHei UI", 9.5f, s, fontBase: fb);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, S(DesignW), S(DesignH),
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Invalidate();
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fSub?.Dispose(); _fHead?.Dispose(); _fLabel?.Dispose();
        _fVal?.Dispose(); _fChip?.Dispose(); _fBtn?.Dispose(); _fNote?.Dispose();
    }

    protected override void OnDpiChanged(float scale, Native.RECT suggested)
    {
        ApplyScale();
        SetRoundedRegion(RadiusPx);
    }

    public override void Dispose()
    {
        DisposeFonts();
        base.Dispose();
    }

    // ==================== 模态流程 ====================

    private bool _closed;

    /// <summary>居中显示并跑自己的消息循环，直到窗口被关掉。</summary>
    public void ShowModal()
    {
        if (!IsValid) return;
        ShowCentered();
        StartRead(false);
        Native.SetTimer(Hwnd, (UIntPtr)1, 200, IntPtr.Zero);
        LogSink.Write("[settings-ui] 显示设备设置");

        while (!_closed && IsValid)
        {
            int r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        if (IsValid) Native.KillTimer(Hwnd, (UIntPtr)1);
        LogSink.Write("[settings-ui] 关闭");
    }

    private void ShowCentered()
    {
        int w = S(DesignW), h = S(DesignH);
        int x = 120, y = 60;
        if (Native.GetCursorPos(out var pt))
        {
            IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfoW(mon, ref mi))
            {
                var wa = mi.rcWork;
                x = wa.Left + Math.Max(0, (wa.Right - wa.Left - w) / 2);
                y = wa.Top + Math.Max(0, (wa.Bottom - wa.Top - h) / 2);
            }
        }
        Native.SetWindowPos(Hwnd, new IntPtr(Native.HWND_TOPMOST), x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.ShowWindow(Hwnd, Native.SW_SHOW);
        Native.SetForegroundWindow(Hwnd);
        Native.SetFocus(Hwnd);
    }

    private void Close()
    {
        _closed = true;
        if (IsValid) Native.DestroyWindow(Hwnd);
    }

    protected override bool OnClose()
    {
        _closed = true;
        Native.KillTimer(Hwnd, (UIntPtr)1);
        return true;   // 交给 DefWindowProc 销毁，ShowModal 里的循环随即退出
    }

    protected override void OnDestroy()
    {
        _closed = true;
        if (Hwnd != IntPtr.Zero) Native.KillTimer(Hwnd, (UIntPtr)1);
    }

    protected override void OnKeyDown(int vk)
    {
        if (vk == 27) Close();          // Esc
    }

    // ==================== 读 / 写 ====================

    private void StartRead(bool userInitiated)
    {
        if (_read is not null)
        {
            if (userInitiated) SetStatus("正在读…", Warn);
            return;
        }
        if (!_client.IsReady)
        {
            SetStatus("充电器还没连上，先等面板显示「已连接」", Bad);
            return;
        }
        SetStatus("正在从充电器读设置…", Warn);
        _read = _client.ReadDeviceSettingsAsync();
        if (userInitiated) LogSink.Write("[settings-ui] 用户点了刷新");
    }

    private void DoWrite(string what, Task<DeviceClient.ControlResult> task)
    {
        if (_write is not null)
        {
            SetStatus("上一条还在写，等它回来", Warn);
            return;
        }
        if (!_client.IsReady)
        {
            SetStatus("充电器还没连上，改了也没人收", Bad);
            return;
        }
        _write = task;
        _writeWhat = what;
        SetStatus("正在写入「" + what + "」…", Warn);
        LogSink.Write("[settings-ui] 写 " + what);
        Invalidate();
    }

    private void Pump()
    {
        if (_read is { IsCompleted: true } r)
        {
            _read = null;
            if (r.IsFaulted)
            {
                SetStatus("读设置失败：" + Short(r.Exception), Bad);
            }
            else
            {
                _state = r.Result ?? new DeviceSettingsState();
                SetStatus(LoadedText(), Faint);
                LogSink.Write("[settings-ui] 读到 " + _state.LoadedCount + " 项");
            }
            Invalidate();
        }

        if (_write is { IsCompleted: true } w)
        {
            _write = null;
            if (w.IsFaulted) SetStatus(_writeWhat + " 写失败：" + Short(w.Exception), Bad);
            else if (w.Result.Ok) SetStatus(_writeWhat + " 已写入", Ok);
            else SetStatus(_writeWhat + " 没成功：" + w.Result.Error, Bad);
            StartRead(false);   // 写完必回读：界面显示的是设备真实状态
            Invalidate();
        }

        // 打开窗口时还没连上：一旦连上就自己读一次，省得用户以为坏了
        if (_state.LoadedCount == 0 && _read is null && _client.IsReady
            && (DateTime.Now - _lastAutoTry).TotalSeconds >= 2)
        {
            _lastAutoTry = DateTime.Now;
            StartRead(false);
        }
    }

    private static string Short(Exception? ex)
    {
        string m = ex?.GetBaseException().Message ?? "";
        return m.Length > 70 ? m[..70] + "…" : m;
    }

    private string LoadedText()
    {
        if (!_demo && !_client.IsReady) return "充电器还没连上，先等面板显示「已连接」";
        int n = _state.LoadedCount;
        if (n == 0) return "设备一项都没答上来（固件可能不支持这些设置）";
        int secs = _state.At == DateTime.MinValue ? 0 : (int)Math.Max(0, (DateTime.Now - _state.At).TotalSeconds);
        return "读到 " + n + " 项 · " + secs + " 秒前";
    }

    private void SetStatus(string text, Color color)
    {
        _status = text;
        _statusColor = color;
    }

    protected override void OnTimer(int id)
    {
        if (id != 1) return;
        Pump();
    }

    // ==================== 命中区域 ====================

    private List<Hot> BuildHots()
    {
        var list = new List<Hot>(26);
        bool ready = CanWrite;

        // 息屏时间：5 个胶囊右对齐
        var timeouts = ScreenTimeouts.Values;
        bool screenKnown = _state.ScreenTimeout is not null;
        for (int i = 0; i < timeouts.Length; i++)
        {
            if (!ready || !screenKnown) continue;
            list.Add(new Hot(Kind.ScreenChip, i, 0, R(ScreenChipRect(i))));
        }

        // 空闲息屏 / 方向锁：行首 y 跟 Paint 里那两行一模一样，矩形来自同一个 SwitchRect
        if (ready && _state.IdleScreenOff is not null)
            list.Add(new Hot(Kind.IdleSwitch, 0, 0, R(SwitchRect(Rows1Y + RowH))));
        if (ready && _state.ScreenLock is not null)
            list.Add(new Hot(Kind.LockSwitch, 0, 0, R(SwitchRect(Rows1Y + 2 * RowH))));

        // 语言：中文 / English
        if (ready && _state.Language is not null)
        {
            list.Add(new Hot(Kind.LangChip, 1, 0, R(LangChipRect(chinese: true))));
            list.Add(new Hot(Kind.LangChip, 0, 0, R(LangChipRect(chinese: false))));
        }

        // 四口定时断电：一个循环档位的胶囊
        for (int i = 0; i < 4; i++)
        {
            if (!ready || _state.Countdowns[i] is null) continue;
            list.Add(new Hot(Kind.Countdown, i, 0, R(CountdownChipRect(i))));
        }

        // USB-A 小电流
        if (ready && _state.UsbLowCurrent is not null)
            list.Add(new Hot(Kind.UsbSwitch, 0, 0, R(SwitchRect(Rows2Y + 4 * RowH))));

        // 快充协议：每口 2~3 个胶囊，右对齐
        for (int port = 0; port < 4; port++)
        {
            var caps = ProtocolCaps.Available(port);
            if (!ready || caps.Length == 0 || _state.ProtocolFlags is null) continue;
            for (int j = 0; j < caps.Length; j++)
            {
                if (_state.ProtocolOn(port, caps[j]) is null) continue;
                list.Add(new Hot(Kind.ProtocolChip, port, j, R(ProtocolChipRect(port, j))));
            }
        }

        // 底部按钮
        if (_read is null) list.Add(new Hot(Kind.Refresh, 0, 0, R(ButtonRect(refresh: true))));
        list.Add(new Hot(Kind.Close, 0, 0, R(ButtonRect(refresh: false))));

        return list;
    }

    private static Hot Hit(List<Hot> hots, int x, int y)
    {
        foreach (var h in hots)
            if (h.Rect.Contains(x, y)) return h;
        return NoHot;
    }

    private bool Same(Hot a, Hot b) => a.Kind == b.Kind && a.A == b.A && a.B == b.B;

    /// <summary>排版自查（0.5.3）：把全部命中矩形按设计尺寸（缩放 = 1）导出成文本，
    /// 好跟 Paint 里每行实际画的 y 逐行核对。之前「点空闲息屏却触发方向锁」就是这里少算了一行，
    /// 留下这个探针，以后再动行距就能一眼看出来对不上。</summary>
    internal void WriteHitProbe(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("kind A B x y w h");
        foreach (var h in BuildHots())
            sb.AppendLine($"{h.Kind} {h.A} {h.B} {h.Rect.X} {h.Rect.Y} {h.Rect.Width} {h.Rect.Height}");
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    protected override bool WantsHandCursor(int x, int y)
        => Hit(BuildHots(), x, y).Kind != Kind.None;

    protected override void OnMouseMove(int x, int y, bool entering)
    {
        var h = Hit(BuildHots(), x, y);
        if (!Same(h, _hover)) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave()
    {
        if (_hover.Kind != Kind.None) { _hover = NoHot; Invalidate(); }
    }

    protected override void OnLeftDown(int x, int y) => _press = Hit(BuildHots(), x, y);

    protected override void OnLeftUp(int x, int y)
    {
        var h = Hit(BuildHots(), x, y);
        var p = _press;
        _press = NoHot;
        if (p.Kind == Kind.None || !Same(p, h)) return;
        Activate(h);
    }

    private void Activate(Hot h)
    {
        switch (h.Kind)
        {
            case Kind.ScreenChip:
                DoWrite("息屏时间", _client.SetScreenTimeoutAsync(ScreenTimeouts.Values[h.A]));
                break;

            case Kind.LangChip:
                DoWrite("屏幕语言", _client.SetLanguageAsync(h.A));
                break;

            case Kind.IdleSwitch:
                DoWrite("空闲息屏", _client.SetIdleScreenOffAsync(_state.IdleScreenOff != true));
                break;

            case Kind.LockSwitch:
                DoWrite("方向锁", _client.SetScreenLockAsync(_state.ScreenLock != true));
                break;

            case Kind.UsbSwitch:
                DoWrite("USB-A 小电流", _client.SetUsbLowCurrentAsync(_state.UsbLowCurrent != true));
                break;

            case Kind.Countdown:
            {
                if (_state.Countdowns[h.A] is not int cur)
                {
                    SetStatus("设备没答这一项的当前值，先点「刷新」", Bad);
                    Invalidate();
                    break;
                }
                int next = CountdownOptions.Next(cur);
                DoWrite(Snapshot.PortNames[h.A] + " 定时断电", _client.SetCountdownAsync(h.A, next));
                break;
            }

            case Kind.ProtocolChip:
            {
                var caps = ProtocolCaps.Available(h.A);
                if (h.B < 0 || h.B >= caps.Length) break;
                var cap = caps[h.B];
                bool? on = _state.ProtocolOn(h.A, cap);
                if (on is null)
                {
                    SetStatus("设备没答协议开关，先点「刷新」", Bad);
                    Invalidate();
                    break;
                }
                DoWrite(Snapshot.PortNames[h.A] + " 的 " + cap.Label(),
                    _client.SetProtocolAsync(h.A, cap, on != true));
                break;
            }

            case Kind.Refresh:
                StartRead(true);
                Invalidate();
                break;

            case Kind.Close:
                Close();
                break;
        }
    }

    // ==================== 绘制 ====================

    private Color Backdrop(int y)
    {
        int h = Math.Max(1, Height);
        float t = Math.Clamp(y / (float)h, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(BgTop.R + (BgBottom.R - BgTop.R) * t),
            (int)Math.Round(BgTop.G + (BgBottom.G - BgTop.G) * t),
            (int)Math.Round(BgTop.B + (BgBottom.B - BgTop.B) * t));
    }

    private void Text(Graphics g, string? text, GdiFont font, Color color, int x, int y, int w, int h,
        TextAnchor anchor = TextAnchor.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        Color c = color.A < 255 ? GdiText.Blend(color, Backdrop(y + h / 2)) : color;
        GdiText.Draw(g, text, font, c, new Rectangle(x, y, w, h), anchor, ellipsis: true);
    }

    protected override void Paint(Graphics g)
    {
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        var all = new RectangleF(0, 0, w, h);
        FillRoundGradient(g, all, RadiusPx, BgTop, BgBottom);
        StrokeRound(g, all, RadiusPx, BorderColor, 1f);

        Text(g, "设备设置", _fTitle, Fg, S(PadX), S(TitleY), S(240), S(30));
        Text(g, "改的是充电器自己的设置，不是这个程序的配置", _fSub, Dim,
            S(PadX), S(SubY), S(DesignW - PadX * 2), S(20));

        _hots = BuildHots();

        // 一、屏幕
        SectionHead(g, "屏幕", Sec1Y, first: true);
        Text(g, "息屏时间", _fLabel, Fg, S(PadX), S(Rows1Y), S(76), S(RowH));
        DrawScreenChips(g);

        Text(g, "空闲息屏", _fLabel, Fg, S(PadX), S(Rows1Y + RowH), S(76), S(RowH));
        DrawSwitchRow(g, Kind.IdleSwitch, Rows1Y + RowH, _state.IdleScreenOff);
        Text(g, "方向锁", _fLabel, Fg, S(PadX), S(Rows1Y + 2 * RowH), S(76), S(RowH));
        DrawSwitchRow(g, Kind.LockSwitch, Rows1Y + 2 * RowH, _state.ScreenLock);
        Text(g, "语言", _fLabel, Fg, S(PadX), S(Rows1Y + 3 * RowH), S(76), S(RowH));
        DrawLangChips(g);

        // 二、端口
        SectionHead(g, "端口", Sec2Y, first: false);
        for (int i = 0; i < 4; i++)
        {
            int y = Rows2Y + i * RowH;
            Text(g, Snapshot.PortNames[i], _fLabel, Fg, S(PadX), S(y), S(52), S(RowH));
            bool known = _state.Countdowns[i] is not null;
            string label = _state.Countdowns[i] is int v ? CountdownOptions.Label(v) : "—";
            var r = R(CountdownChipRect(i));
            DrawChip(g, r, label, on: known && _state.Countdowns[i] != 0,
                disabled: !CanWrite || !known, hot: Same(_hover, HotAt(Kind.Countdown, i)));
            // 右边顺手把这一口此刻实际协商到的协议写出来：定时断电设了多久，跟它现在跑什么协议是一起看的
            string neg = _state.Negotiations[i]?.Text ?? "—";
            Text(g, neg, _fVal, Faint, S(170), S(y), S(DesignW - PadX - 170), S(RowH), TextAnchor.Right);
        }

        {
            int y = Rows2Y + 4 * RowH;
            Text(g, "USB-A 小电流", _fLabel, Fg, S(PadX), S(y), S(160), S(RowH));
            Text(g, "给耳机手环充电时不握手快充", _fNote, Faint,
                S(PadX + 160), S(y), S(swX() - PadX - 168 - PadX + 24), S(RowH), TextAnchor.Right);
            DrawSwitchRow(g, Kind.UsbSwitch, y, _state.UsbLowCurrent);
        }

        // 三、快充协议
        SectionHead(g, "快充协议", Sec3Y, first: false);
        for (int i = 0; i < 4; i++)
        {
            int y = Rows3Y + i * RowH;
            Text(g, Snapshot.PortNames[i], _fLabel, Fg, S(PadX), S(y), S(52), S(RowH));
            DrawProtocolChips(g, i);
        }

        // 状态行 + 按钮
        Text(g, _status, _fNote, _statusColor, S(PadX), S(StatusY), S(DesignW - PadX * 2), S(18));

        DrawButton(g, R(ButtonRect(refresh: true)), "刷新",
            primary: true, hot: Same(_hover, HotAt(Kind.Refresh, 0)), enabled: _read is null);
        DrawButton(g, R(ButtonRect(refresh: false)), "关闭",
            primary: false, hot: Same(_hover, HotAt(Kind.Close, 0)), enabled: true);
    }

    private int swX() => DesignW - PadX - SwitchW;

    // ─────────────────────────────────────────────────────────────────────
    // 布局矩形：唯一的来源
    //
    // 0.5.2 及之前，Paint 里画一遍、BuildHots 里另写一遍同样的公式，结果「空闲息屏」
    // 的命中框少算了一行，点它触发的是下面那行「方向锁」（issue #2 那类反馈）。
    // 现在两边都只调下面这几个方法：绘制错，命中一起错；绘制对，命中一定对。
    // 参数一律是设计坐标（dip），落屏时统一过 R()。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>设计坐标 → 设备像素。绘制与命中都只走这一个换算。</summary>
    private Rectangle R(Rectangle d) => new(S(d.X), S(d.Y), S(d.Width), S(d.Height));

    private static Rectangle ScreenChipRect(int i)
    {
        var timeouts = ScreenTimeouts.Values;
        int totalW = timeouts.Length * ScreenChipW + (timeouts.Length - 1) * ChipGap;
        int x0 = DesignW - PadX - totalW;
        return new Rectangle(x0 + i * (ScreenChipW + ChipGap), Rows1Y + (RowH - ChipH) / 2,
            ScreenChipW, ChipH);
    }

    /// <summary>某个开关所在行（设计 y = 该行行首）上的开关矩形。</summary>
    private static Rectangle SwitchRect(int rowY)
        => new(DesignW - PadX - SwitchW, rowY + (RowH - SwitchH) / 2, SwitchW, SwitchH);

    private static Rectangle LangChipRect(bool chinese)
    {
        int x = DesignW - PadX - (LangCnW + ChipGap + LangEnW);
        int y = Rows1Y + 3 * RowH + (RowH - ChipH) / 2;
        return chinese
            ? new Rectangle(x, y, LangCnW, ChipH)
            : new Rectangle(x + LangCnW + ChipGap, y, LangEnW, ChipH);
    }

    private static Rectangle CountdownChipRect(int i)
        => new(CountdownChipX, Rows2Y + i * RowH + (RowH - ChipH) / 2, CountdownChipW, ChipH);

    private static Rectangle ProtocolChipRect(int port, int j)
    {
        var caps = ProtocolCaps.Available(port);
        if (caps.Length == 0) return Rectangle.Empty;
        int total = caps.Length * ProtocolChipW + (caps.Length - 1) * ChipGap;
        int cx = DesignW - PadX - total;
        int cy = Rows3Y + port * RowH + (RowH - ChipH) / 2;
        return new Rectangle(cx + j * (ProtocolChipW + ChipGap), cy, ProtocolChipW, ChipH);
    }

    private static Rectangle ButtonRect(bool refresh)
    {
        int closeX = DesignW - PadX - BtnW;
        int refreshX = closeX - BtnGap - BtnW;
        return new Rectangle(refresh ? refreshX : closeX, BtnY, BtnW, BtnH);
    }

    /// <summary>取当前 hot 列表里某个按钮的矩形（只是为了让 hover 判断写得短一点）。</summary>
    private Hot HotAt(Kind kind, int a, int b = 0)
    {
        foreach (var h in _hots)
            if (h.Kind == kind && h.A == a && h.B == b) return h;
        return new Hot(kind, a, b, Rectangle.Empty);   // 空矩形：Contains 永远 false
    }

    private void SectionHead(Graphics g, string text, int y, bool first)
    {
        if (!first)
        {
            using var pen = new Pen(SepColor, 1f);
            g.DrawLine(pen, S(PadX), S(y - 10), S(DesignW - PadX), S(y - 10));
        }
        Text(g, text, _fHead, Fg, S(PadX), S(y), S(260), S(20));
    }

    private void DrawScreenChips(Graphics g)
    {
        var timeouts = ScreenTimeouts.Values;
        for (int i = 0; i < timeouts.Length; i++)
        {
            var r = R(ScreenChipRect(i));
            DrawChip(g, r, ScreenTimeouts.Label(timeouts[i]),
                on: _state.ScreenTimeout == timeouts[i],
                disabled: !CanWrite || _state.ScreenTimeout is null,
                hot: Same(_hover, HotAt(Kind.ScreenChip, i)));
        }
    }

    private void DrawLangChips(Graphics g)
    {
        var rc = R(LangChipRect(chinese: true));
        var re = R(LangChipRect(chinese: false));
        bool known = _state.Language is not null;
        DrawChip(g, rc, "中文", on: _state.Language == 1, disabled: !CanWrite || !known,
            hot: Same(_hover, HotAt(Kind.LangChip, 1)));
        DrawChip(g, re, "English", on: _state.Language == 0, disabled: !CanWrite || !known,
            hot: Same(_hover, HotAt(Kind.LangChip, 0)));
    }

    /// <summary>画一行右边那个开关。<paramref name="kind"/> 由调用方显式给出 —— 0.5.3 之前是按
    /// rowY 反推的（跟空闲息屏/方向锁的行号写死在一起），将来在这几行中间插一行就会静默错位。</summary>
    private void DrawSwitchRow(Graphics g, Kind kind, int rowY, bool? on)
        => DrawSwitch(g, R(SwitchRect(rowY)), on, hot: Same(_hover, HotAt(kind, 0)));

    private void DrawProtocolChips(Graphics g, int port)
    {
        var caps = ProtocolCaps.Available(port);
        if (caps.Length == 0) return;
        for (int j = 0; j < caps.Length; j++)
        {
            bool? on = _state.ProtocolOn(port, caps[j]);
            var r = R(ProtocolChipRect(port, j));
            DrawChip(g, r, caps[j].Label(), on: on == true,
                disabled: !CanWrite || on is null,
                hot: Same(_hover, HotAt(Kind.ProtocolChip, port, j)));
        }
    }

    private void DrawChip(Graphics g, Rectangle r, string text, bool on, bool disabled, bool hot)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        Color fill = disabled ? ChipOffBg : on ? ChipOnBg : hot ? Color.FromArgb(40, 46, 56) : ChipBg;
        Color border = disabled ? SepColor : on ? Ok : hot ? Color.FromArgb(96, 104, 116) : BorderColor;
        Color fg = disabled ? Faint : on ? Ok : hot ? Fg : Dim;
        using (var path = RoundRect(rf, S(7)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
        }
        GdiText.Draw(g, text, _fChip, fg, r, TextAnchor.Center, ellipsis: true);
    }

    private void DrawSwitch(Graphics g, Rectangle r, bool? on, bool hot)
    {
        bool disabled = on is null;
        bool isOn = on == true;
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        Color track = disabled ? ChipOffBg : isOn ? Ok : TrackOff;
        using (var path = RoundRect(rf, r.Height / 2f))
        {
            using (var b = new SolidBrush(track)) g.FillPath(b, path);
            if (hot && !disabled)
            {
                using var p = new Pen(Color.FromArgb(130, 140, 152), 1f);
                g.DrawPath(p, path);
            }
        }
        int d = r.Height - 6;
        int kx = isOn ? r.Right - 3 - d : r.X + 3;
        Color knob = disabled ? Color.FromArgb(122, 128, 137)
                   : isOn ? Color.FromArgb(240, 255, 250)
                   : Color.FromArgb(196, 203, 212);
        using var kb = new SolidBrush(knob);
        g.FillEllipse(kb, new RectangleF(kx, r.Y + 3, d, d));
    }

    private void DrawButton(Graphics g, Rectangle r, string text, bool primary, bool hot, bool enabled)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        using var path = RoundRect(rf, S(10));
        if (primary)
        {
            Color fill = !enabled ? ChipOffBg : hot ? Color.FromArgb(74, 226, 172) : Ok;
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            GdiText.Draw(g, text, _fBtn, enabled ? Color.FromArgb(14, 20, 18) : Faint, r, TextAnchor.Center, ellipsis: true);
        }
        else
        {
            using (var b = new SolidBrush(hot ? Color.FromArgb(38, 44, 54) : ChipBg)) g.FillPath(b, path);
            using (var p = new Pen(BorderColor, 1f)) g.DrawPath(p, path);
            GdiText.Draw(g, text, _fBtn, Fg, r, TextAnchor.Center, ellipsis: true);
        }
    }

    // ==================== 诊断渲染 ====================

    public void ForceScale(float scale, float fontBase = 1.25f)
    {
        if (scale < 0.5f || scale > 4f) return;
        Scale = scale;
        _fontBase = fontBase > 0f ? fontBase : scale;
        ApplyScale();
    }

    /// <summary>把这一帧画进 PNG（会话内自检用）。四角抠成透明，跟真机的圆角窗口一致。</summary>
    public void SaveRender(string path)
    {
        int w = Math.Max(1, S(DesignW)), h = Math.Max(1, S(DesignH));
        int ow = Width, oh = Height;
        Width = w;
        Height = h;
        try
        {
            using var surf = Surface.Create(w, h);
            if (surf is null) return;
            surf.G.Clear(BgBottom);
            Paint(surf.G);
            using var bmp = surf.ToBitmap();
            using var shot = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g2 = Graphics.FromImage(shot))
            {
                g2.Clear(Color.Transparent);
                using var clip = RoundRect(new RectangleF(0, 0, w, h), 14 * Scale);
                g2.SetClip(clip);
                g2.DrawImage(bmp, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllBytes(path, ms.ToArray());
            LogSink.Write("[settings-ui] rendered " + path + " (" + w + "x" + h + ")");
        }
        finally
        {
            Width = ow;
            Height = oh;
        }
    }

    /// <summary>渲染自查用：造一份「全都读到了」的假数据，好让截图上不是一片「—」。</summary>
    public void UseDemoData()
    {
        var st = new DeviceSettingsState
        {
            ScreenTimeout = 2,
            IdleScreenOff = true,
            ScreenLock = false,
            UsbLowCurrent = false,
            Language = 1,
            ProtocolFlags = 0x01030A0Du,   // C1 PD+UFCS、C2 PPS、C3 UFCS+SCP、A UFCS
            At = DateTime.Now,
        };
        for (int i = 0; i < 4; i++) st.Countdowns[i] = i switch { 0 => 0, 1 => 30, 2 => 120, _ => 0 };
        st.Negotiations[0] = new Negotiation(7, 65);
        st.Negotiations[2] = new Negotiation(10, 30);
        _state = st;
        _read = null;
        _write = null;
        _demo = true;
        SetStatus("读到 16 项 · 0 秒前", Faint);
    }
}
