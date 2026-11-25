using System.Drawing;
using System.Runtime.InteropServices;
using CuktechMible;

namespace CuktechTray;

/// <summary>
/// 单口采集窗口（对齐 macOS 0.1.3 / PR #4 的「单口采集」）。
/// 干什么：挑一路端口 → 每秒读一次 → 停下来算充入电量 Wh、平均 / 峰值功率、电压区间、换档次数。
///
/// 三个设计约束决定了它长这样：
/// ① 采集的累加在 DeviceClient 的后台循环里（每秒一拍），窗口**不订阅事件**：
///    事件是从后台线程 Post 过来的，而这个窗口跑在自己的模态消息循环里，没有 UiDispatcher，
///    订阅了就得自己 marshal 回来。改成 Native.SetTimer 每 250ms 轮询
///    「Capturing / CaptureStats / LastCapture」这几个已经是快照的属性，线程安全且简单。
/// ② 采样只有 1 Hz，界面却要 4 Hz 问一次 —— 所以 OnTimer 里做值比较，只有数字真的变了才 Invalidate()。
/// ③ 「采集结束」不能靠「Capturing 从 true 变 false」判定：FinishCapture 是先写 _capturePort=-1、
///    再写 LastCapture 的，250ms 的轮询有机会正好夹在中间（看到「没在采集 + 结果还是 null」）。
///    所以这里只认「LastCapture 换了个新对象」，两种情况（自己停 / 自己结束）都能立刻显示结果。
/// </summary>
internal sealed class CaptureWindow : WinBase
{
    // ---- 设计尺寸（96 DPI 下的逻辑像素）----
    private const int DesignW = 360;
    private const int DesignH = 440;
    private const int PadX = 24;
    private const int TitleY = 20;
    private const int SubY = 48;
    private const int PortY = 78;
    private const int PortH = 34;
    private const int PortW = 72;          // (360-48-3*8)/4 = 72，四个口正好铺满内边距
    private const int PortGap = 8;
    private const int CardY = 126;
    private const int CardH = 96;
    private const int BigX = 40;           // 卡片内左侧留 16
    private const int BigY = 134;
    private const int ProgY = 192;         // 「已采 N 次 · 有效 X 秒」放在卡片内部下沿
    private const int RowTop = 216;        // 卡片底 222，六行从这里开始
    private const int RowH = 20;           // 6×20=120 → 336，上面要留出状态行(340)和按钮(374)
    private const int StatusY = 344;   // 六行结果到 336，留 8px 再放状态行，往上贴太近
    private const int BtnH = 42;
    private const int BtnY = DesignH - 24 - BtnH;   // 374
    private const int BtnPrimaryW = 132;
    private const int BtnSecondW = 88;
    private const int BtnCloseW = 76;
    private const int BtnGap = 8;
    // 132+8+88+8+76 = 312 = DesignW-PadX*2：三个按钮正好填满一行，右下「关闭」右对齐到 336

    private const int TimerId = 1;

    private const int HitNone = -1;
    private const int HitPrimary = 1;
    private const int HitSecondary = 2;
    private const int HitClose = 3;
    private const int HitPortBase = 100;

    // ---- 配色：跟 PanelWindow / ConsentWindow 完全同一套 ----
    private static readonly Color BgTop = Color.FromArgb(34, 38, 45);
    private static readonly Color BgBottom = Color.FromArgb(20, 23, 28);
    private static readonly Color BorderColor = Color.FromArgb(58, 64, 74);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = WattStyle.Dim;
    private static readonly Color Faint = Color.FromArgb(110, 117, 126);
    private static readonly Color Ok = WattStyle.Ok;
    private static readonly Color Bad = WattStyle.Bad;
    private static readonly Color SepColor = Color.FromArgb(44, 49, 57);
    private static readonly Color CardBg = Color.FromArgb(28, 32, 39);
    private static readonly Color PillBg = Color.FromArgb(34, 39, 47);
    private static readonly Color PillHoverBg = Color.FromArgb(38, 44, 54);
    private static readonly Color PillSelBg = Color.FromArgb(30, 60, 50);
    private static readonly Color BtnDisabledBg = Color.FromArgb(28, 32, 39);
    private static readonly Color OkHot = Color.FromArgb(74, 226, 172);
    private static readonly Color BadHot = Color.FromArgb(252, 146, 146);
    private static readonly Color BtnTextOnOk = Color.FromArgb(14, 20, 18);

    private enum StatusKind { Hint, Ok, Bad }

    private static readonly string[] RowLabels = { "能量", "平均功率", "峰值", "电压区间", "换档次数", "结束原因" };

    private readonly DeviceClient _client;

    private int _selPort;
    private int _hover = HitNone;
    private int _press = HitNone;

    private string _status = "";
    private StatusKind _statusKind = StatusKind.Hint;
    private DateTime _statusUntil;

    // 轮询去抖用的上一次读数（只为了少刷几帧，界面永远读实时值）
    private bool _wasCapturing;
    private bool _wasReady;
    private CaptureAccumulator? _lastStats;
    private CaptureResult? _lastResult;

    private bool _closed;
    private bool _destroyed;

    // 自检渲染用的假数据（--render 那条路，lead 会调 UseDemoData()）
    private bool _demo;
    private CaptureResult? _demoResult;

    private GdiFont _fTitle = null!, _fSub = null!, _fPort = null!, _fBig = null!, _fUnit = null!,
                    _fLabel = null!, _fValue = null!, _fBtn = null!, _fStatus = null!;
    private float _fontBase;

    public CaptureWindow(DeviceClient client)
    {
        _client = client;
        // 万一窗口打开时采集已经在跑（面板那边起的），把选中项对上，否则看着像「没在采集」
        if (client.Capturing) _selPort = Math.Clamp(client.CapturePort, 0, 3);

        CreateWin(DesignW, DesignH, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, IntPtr.Zero, "capture");
        if (Hwnd == IntPtr.Zero)
        {
            LogSink.Write("[capture] CreateWin 失败 err=" + Marshal.GetLastWin32Error());
            return;
        }
        ApplyScale();
        // 采样本来就是 1 Hz，250ms 一轮足够跟手，又不至于把 UI 线程占满
        Native.SetTimer(Hwnd, (UIntPtr)TimerId, 250, IntPtr.Zero);
        LogSink.Write("[capture] 建立 ok hwnd=" + Hwnd + " scale=" + Scale.ToString("F2")
            + " capturing=" + client.Capturing);
    }

    // 截断而不是四舍五入：1.25 倍下 6*1.25=7.5 必须落到 7，用 Math.Round 会让四个口的横坐标一格一格错开
    private int S(float v) => (int)(v * Scale);

    /// <summary>窗口圆角半径（像素）。绘制与 SetWindowRgn 必须用同一个整数值，否则四角会混出一丝黑边。</summary>
    private int RadiusPx => S(14);

    // ==================== 实时值的统一入口（演示开关只在这里分流） ====================

    private CaptureResult? Result => _demo ? _demoResult : _client.LastCapture;
    private CaptureAccumulator? Stats => _demo ? _demoResult?.Stats : _client.CaptureStats;
    private bool Capturing => !_demo && _client.Capturing;
    private bool Ready => _demo || _client.IsReady;

    private void ApplyScale()
    {
        DisposeFonts();
        float s = Scale;
        float fb = _fontBase > 0f ? _fontBase : s;
        _fTitle = GdiFont.Create("Microsoft YaHei UI", 14, s, bold: true, fontBase: fb);
        _fSub = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fPort = GdiFont.Create("Microsoft YaHei UI", 12, s, bold: true, fontBase: fb);
        _fBig = GdiFont.Create("Microsoft YaHei UI", 26, s, bold: true, fontBase: fb);
        _fUnit = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fLabel = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fValue = GdiFont.Create("Microsoft YaHei UI", 11, s, fontBase: fb);
        _fBtn = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, bold: true, fontBase: fb);
        _fStatus = GdiFont.Create("Microsoft YaHei UI", 10, s, fontBase: fb);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, S(DesignW), S(DesignH),
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        SetRoundedRegion(RadiusPx);
        Invalidate();
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fSub?.Dispose(); _fPort?.Dispose(); _fBig?.Dispose(); _fUnit?.Dispose();
        _fLabel?.Dispose(); _fValue?.Dispose(); _fBtn?.Dispose(); _fStatus?.Dispose();
    }

    protected override void OnDpiChanged(float scale, Native.RECT suggested) => ApplyScale();

    // ==================== 模态流程 ====================

    /// <summary>居中摆出来并在这里挡住消息循环，直到窗口被关掉（Esc / 关闭 / Alt+F4）。</summary>
    public void ShowModal()
    {
        if (!IsValid) return;
        if (_client.Capturing) _selPort = Math.Clamp(_client.CapturePort, 0, 3);
        ShowCentered();
        LogSink.Write("[capture] 显示窗口，等用户操作");

        while (!_closed && IsValid)
        {
            int r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        LogSink.Write("[capture] 窗口关闭，capturing=" + _client.Capturing);
    }

    private void ShowCentered()
    {
        int w = S(DesignW), h = S(DesignH);
        int x = 120, y = 80;
        if (Native.GetCursorPos(out var pt))
        {
            IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
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

    // ==================== 命中区 ====================

    private Rectangle PortRect(int i) => new(S(PadX + i * (PortW + PortGap)), S(PortY), S(PortW), S(PortH));
    private Rectangle PrimaryRect() => new(S(PadX), S(BtnY), S(BtnPrimaryW), S(BtnH));
    private Rectangle SecondRect() => new(S(PadX + BtnPrimaryW + BtnGap), S(BtnY), S(BtnSecondW), S(BtnH));
    private Rectangle CloseRect() => new(S(DesignW - PadX - BtnCloseW), S(BtnY), S(BtnCloseW), S(BtnH));

    private int HitTest(int x, int y)
    {
        for (int i = 0; i < 4; i++) if (PortRect(i).Contains(x, y)) return HitPortBase + i;
        if (PrimaryRect().Contains(x, y)) return HitPrimary;
        if (SecondRect().Contains(x, y)) return HitSecondary;
        if (CloseRect().Contains(x, y)) return HitClose;
        return HitNone;
    }

    /// <summary>采集中主按钮仍可点（用来停），其它三口和「复制摘要」按状态决定。</summary>
    private bool Clickable(int hit)
    {
        if (hit >= HitPortBase) return !Capturing && hit - HitPortBase != _selPort;
        return hit switch
        {
            HitPrimary => Capturing || Ready,
            HitSecondary => Result is not null,
            HitClose => true,
            _ => false,
        };
    }

    protected override bool WantsHandCursor(int x, int y) => Clickable(HitTest(x, y));

    protected override void OnMouseMove(int x, int y, bool entering)
    {
        int h = HitTest(x, y);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave()
    {
        if (_hover != HitNone) { _hover = HitNone; Invalidate(); }
    }

    protected override void OnLeftDown(int x, int y) => _press = HitTest(x, y);

    protected override void OnLeftUp(int x, int y)
    {
        int h = HitTest(x, y);
        int p = _press;
        _press = HitNone;
        if (p == HitNone || h != p) return;   // 按下和抬起不在同一处 = 用户改主意了
        if (h >= HitPortBase) { PickPort(h - HitPortBase); return; }
        switch (h)
        {
            case HitPrimary: ToggleCapture(); break;
            case HitSecondary: CopySummary(); break;
            case HitClose: CloseWindow(); break;
        }
    }

    protected override void OnKeyDown(int vk)
    {
        if (vk != 27) return;   // 只认 Esc
        // 采集中按 Esc 不能把正在累计的这段丢了：先让它正常收尾（结果是「手动停止」），再关窗
        if (Capturing) _client.StopCapture();
        CloseWindow();
    }

    private void PickPort(int i)
    {
        if (Capturing)
        {
            // 采集中换口会让两路的采样混进同一个累加器，所以这里只提示、不换
            SetStatus("先停止当前采集，再换口", StatusKind.Bad);
            return;
        }
        if (i == _selPort) return;
        _selPort = i;
        SetStatus("已选 " + Snapshot.PortNames[i], StatusKind.Hint);
        Invalidate();
    }

    private void ToggleCapture()
    {
        if (Capturing)
        {
            _client.StopCapture();
            SetStatus("正在停止…", StatusKind.Hint);
            Invalidate();
            return;
        }
        if (!Ready)
        {
            SetStatus("还没连上充电器，请等面板显示「已连接」", StatusKind.Bad);
            return;
        }
        var r = _client.StartCapture(_selPort);
        if (!r.Ok)
        {
            SetStatus(r.Error, StatusKind.Bad);
            return;
        }
        SetStatus("开始采集 " + Snapshot.PortNames[_selPort] + "，每秒一次", StatusKind.Ok);
        Invalidate();
    }

    private void CopySummary()
    {
        var res = Result;
        if (res is null) return;
        // 用系统消息框而不是自己画：摘要就是要能 Ctrl+C 出来，原生 Dialog 自带选中与复制
        Native.MessageBoxW(Hwnd, res.SummaryText, "采集摘要（Ctrl+C 可以复制）",
            Native.MB_OK | Native.MB_ICONINFORMATION);
        Invalidate();
    }

    private void CloseWindow()
    {
        if (!IsValid) return;
        if (Capturing) _client.StopCapture();   // 关窗前先收尾，别把正在累计的这段丢了
        Native.DestroyWindow(Hwnd);
    }

    protected override bool OnClose()
    {
        Native.KillTimer(Hwnd, (UIntPtr)TimerId);
        if (Capturing) _client.StopCapture();
        _closed = true;
        return true;   // 交给 WinBase 走默认的 DestroyWindow
    }

    protected override void OnDestroy()
    {
        if (_destroyed) return;   // WM_DESTROY 与 WM_NCDESTROY 都会到这儿
        _destroyed = true;
        _closed = true;
        if (Hwnd != IntPtr.Zero) Native.KillTimer(Hwnd, (UIntPtr)TimerId);
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status = text;
        _statusKind = kind;
        // 一句话只留 3 秒，之后退回默认提示，免得旧错误一直挂在那儿误导人
        _statusUntil = DateTime.UtcNow.AddSeconds(3);
        Invalidate();
    }

    // ==================== 轮询 ====================

    protected override void OnTimer(int id)
    {
        if (id != TimerId) return;
        if (_demo) return;   // 演示数据是死的，不用轮询

        bool changed = false;

        // 新结果出现（自己点停的、空载/断连自己结束的都算）→ 把结束原因说出来
        var result = _client.LastCapture;
        if (result is not null && !ReferenceEquals(result, _lastResult))
        {
            _lastResult = result;
            SetStatus("采集结束：" + result.ReasonText, StatusKind.Ok);
            changed = true;
        }

        if (_client.Capturing != _wasCapturing) { _wasCapturing = _client.Capturing; changed = true; }
        if (_client.IsReady != _wasReady) { _wasReady = _client.IsReady; changed = true; }

        // 采样只有 1 Hz，没必要每 250ms 重绘；采样数 / 功率 / 有效秒任一个动了才刷
        var stats = _client.CaptureStats;
        if (stats is null)
        {
            if (_lastStats is not null) { _lastStats = null; changed = true; }
        }
        else if (_lastStats is null
                 || _lastStats.SampleCount != stats.SampleCount
                 || Math.Abs(_lastStats.LastPower - stats.LastPower) > 0.01
                 || Math.Abs(_lastStats.EffectiveSeconds - stats.EffectiveSeconds) > 0.01)
        {
            _lastStats = stats;
            changed = true;
        }

        if (_statusUntil != default && DateTime.UtcNow >= _statusUntil)
        {
            _status = "";
            _statusUntil = default;
            changed = true;
        }

        if (changed) Invalidate();
    }

    // ==================== 绘制 ====================

    private static void Text(Graphics g, string? text, GdiFont font, Color color, int x, int y, int w, int h)
    {
        if (string.IsNullOrEmpty(text)) return;
        GdiText.Draw(g, text, font, color, new Rectangle(x, y, w, h), TextAnchor.Left, ellipsis: true);
    }

    private static int Measure(Graphics g, string? text, GdiFont font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        IntPtr hdc = g.GetHdc();
        try { return font.Measure(text, hdc); }
        finally { g.ReleaseHdc(hdc); }
    }

    protected override void Paint(Graphics g)
    {
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        var all = new RectangleF(0, 0, w, h);
        FillRoundGradient(g, all, RadiusPx, BgTop, BgBottom);
        StrokeRound(g, all, RadiusPx, BorderColor, 1f);

        Text(g, "单口采集", _fTitle, Fg, S(PadX), S(TitleY), S(DesignW - PadX * 2), S(26));
        Text(g, "选一路，按秒记，停下来给你算 Wh", _fSub, Faint, S(PadX), S(SubY), S(DesignW - PadX * 2), S(20));

        for (int i = 0; i < 4; i++) DrawPort(g, i);
        DrawCard(g);
        DrawRows(g);
        DrawStatus(g);
        DrawButtons(g);
    }

    private void DrawPort(Graphics g, int i)
    {
        var r = PortRect(i);
        bool selected = i == _selPort;
        bool locked = Capturing && !selected;    // 采集中另外三口灰掉：换口会把两路采样混进一个累加器

        Color fill, border, text;
        if (selected) { fill = PillSelBg; border = Ok; text = Ok; }
        else if (locked) { fill = BtnDisabledBg; border = SepColor; text = Faint; }
        else if (_hover == HitPortBase + i) { fill = PillHoverBg; border = BorderColor; text = Fg; }
        else { fill = PillBg; border = BorderColor; text = Dim; }

        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        using (var path = RoundRect(rf, S(8)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
        }
        GdiText.Draw(g, Snapshot.PortNames[i], _fPort, text, r, TextAnchor.Center, ellipsis: true);
    }

    private void DrawCard(Graphics g)
    {
        var rf = new RectangleF(S(PadX), S(CardY), S(DesignW - PadX * 2), S(CardH));
        FillRound(g, rf, S(10), CardBg);
        StrokeRound(g, rf, S(10), SepColor, 1f);

        double power = LivePower();
        string num = power.ToString("0.0");
        var bigRect = new Rectangle(S(BigX), S(BigY), S(210), S(42));
        // 没输出就用 Faint：0.0 W 亮成绿色会让人以为「在充」
        GdiText.Draw(g, num, _fBig, power > 0.05 ? Ok : Faint, bigRect, TextAnchor.Left, ellipsis: true);

        // 单位紧跟数字右边，并且压到数字的基线上（居中会比数字高半行，看着飘）
        int nw = Measure(g, num, _fBig);
        Text(g, "W", _fUnit, Dim, S(BigX) + nw + S(5), S(BigY + 16), S(26), S(22));

        Text(g, ProgressText(), _fSub, Faint, S(BigX), S(ProgY), S(DesignW - BigX - PadX - 4), S(20));
    }

    /// <summary>大数字：采集中的实时功率优先，停下来之后留着最后一次的读数（比归零更有信息量）。</summary>
    private double LivePower()
    {
        if (Stats is { } st) return st.LastPower;
        return Result?.Stats.LastPower ?? 0;
    }

    private string ProgressText()
    {
        if (Stats is not { } st) return "还没开始";
        return "已采 " + st.SampleCount + " 次 · 有效 " + (int)st.EffectiveSeconds + " 秒";
    }

    private void DrawRows(Graphics g)
    {
        int labelW = S(120);
        int rowW = S(DesignW - PadX * 2);
        for (int i = 0; i < RowLabels.Length; i++)
        {
            int y = S(RowTop + i * RowH);
            Text(g, RowLabels[i], _fLabel, Faint, S(PadX), y, labelW, S(RowH));
            string v = RowText(i);
            bool real = v != "—" && v != "采集中…" && v != "样本太少";
            GdiText.Draw(g, v, _fValue, real ? Fg : Faint,
                new Rectangle(S(PadX), y, rowW, S(RowH)), TextAnchor.Right, ellipsis: true);
        }
    }

    /// <summary>
    /// 六行结果。有结果就照 CaptureResult 的文字原样用（它已经处理好单位与「—」）；
    /// 采集中的能量行给实时值 —— 但样本不足 2 个时写「采集中…」而不是 0.00 Wh：
    /// 刚点开始那一秒总不可能已经充了多少电，写 0.00 是误导。
    /// </summary>
    private string RowText(int i)
    {
        var res = Result;
        var st = Stats;
        bool tooFew = res is { } r && r.Stats.SampleCount < 2;
        switch (i)
        {
            case 0:
                if (Capturing)
                    return st is { SampleCount: >= 2 } ? st.EnergyWh.ToString("0.00") + " Wh" : "采集中…";
                if (res is null) return "—";
                if (tooFew) return "样本太少";
                return res.EnergyText + " Wh";
            case 1: return res is null || tooFew ? "—" : res.AverageText + " W";
            case 2: return res is null || tooFew ? "—" : res.PeakText + " W";
            case 3: return res?.VoltageText ?? "—";
            case 4: return res is null ? "—" : res.Stats.VoltageChanges + " 次";
            default: return res?.ReasonText ?? "—";
        }
    }

    private void DrawStatus(Graphics g)
    {
        bool idle = string.IsNullOrEmpty(_status);
        string text = idle ? "选一路开始" : _status;
        Color c = idle ? Faint : _statusKind switch
        {
            StatusKind.Ok => Ok,
            StatusKind.Bad => Bad,
            _ => Dim,
        };
        Text(g, text, _fStatus, c, S(PadX), S(StatusY), S(DesignW - PadX * 2), S(18));
    }

    private void DrawButtons(Graphics g)
    {
        bool cap = Capturing;
        bool on = cap || Ready;
        string label = cap ? "停止" : "开始采集 " + Snapshot.PortNames[_selPort];
        Color fill = !on ? BtnDisabledBg : cap ? (_hover == HitPrimary ? BadHot : Bad) : (_hover == HitPrimary ? OkHot : Ok);
        Color fg = !on ? Faint : cap ? Color.FromArgb(255, 255, 255) : BtnTextOnOk;
        DrawButton(g, PrimaryRect(), label, fill, fg, !on);

        // 「复制摘要」没有结果就是灰的：点了也弹不出东西，不如直接不可用
        bool canCopy = Result is not null;
        DrawButton(g, SecondRect(), "复制摘要",
            canCopy && _hover == HitSecondary ? PillHoverBg : PillBg,
            canCopy ? Fg : Faint, true);

        DrawButton(g, CloseRect(), "关闭", _hover == HitClose ? PillHoverBg : PillBg, Fg, true);
    }

    private void DrawButton(Graphics g, Rectangle r, string text, Color fill, Color fg, bool stroke)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        using var path = RoundRect(rf, S(10));
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (stroke)
        {
            using var pen = new Pen(BorderColor, 1f);
            g.DrawPath(pen, path);
        }
        GdiText.Draw(g, text, _fBtn, fg, r, TextAnchor.Center, ellipsis: true);
    }

    public override void Dispose()
    {
        DisposeFonts();
        base.Dispose();
    }

    // ==================== 诊断渲染 ====================

    public void ForceScale(float scale, float fontBase = 1.25f)
    {
        if (scale < 0.5f || scale > 4f) return;
        Scale = scale;
        _fontBase = fontBase > 0f ? fontBase : scale;
        ApplyScale();
    }

    /// <summary>
    /// 演示数据：给「没接充电器也要出图」的自检用。造一份真的 CaptureResult ——
    /// 直接塞 250 个假采样点过一遍 CaptureAccumulator，走的就是真机上那条累加路径，
    /// 所以画出来的电量/平均/峰值/电压区间/换档次数彼此自洽，不是硬编的字符串。
    /// 用法：new CaptureWindow(client) → ForceScale(1.25f) → UseDemoData() → SaveRender(path)。
    /// </summary>
    public void UseDemoData()
    {
        if (_demoResult is null)
        {
            _demoResult = BuildDemoResult();
            _selPort = _demoResult.PortIndex;
            _status = "采集结束：" + _demoResult.ReasonText;
            _statusKind = StatusKind.Ok;
            _statusUntil = default;   // 出图时不希望这行 3 秒后自己消失
        }
        _demo = true;
        Invalidate();
    }

    private static CaptureResult BuildDemoResult()
    {
        // 5 段 × 50 个采样点（每点 1 秒）：9V→12V→9V 的重新协商让「换档次数」不是 0
        (double V, double A)[] seg = { (9.0, 2.8), (9.0, 3.0), (12.0, 2.5), (12.0, 3.0), (9.0, 2.9) };
        const int per = 50;
        var acc = new CaptureAccumulator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        for (int s = 0; s < seg.Length; s++)
        {
            for (int i = 0; i < per; i++)
            {
                var d = new PortData { State = 0x01, Voltage = seg[s].V, Current = seg[s].A };
                acc.Add(d, t0.AddSeconds(s * per + i));
            }
        }
        return new CaptureResult
        {
            PortIndex = 1,
            PortName = Snapshot.PortNames[1],
            StartedAt = t0,
            EndedAt = t0.AddSeconds(per * seg.Length),
            Stats = acc,
            Reason = CaptureEndReason.Stopped,
        };
    }

    /// <summary>把当前一帧画进 PNG（四角透明，照 ConsentWindow 的写法，不依赖截图工具）。</summary>
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
                using var clip = RoundRect(new RectangleF(0, 0, w, h), RadiusPx);
                g2.SetClip(clip);
                g2.DrawImage(bmp, 0, 0, w, h);
            }
            // 走内存再落盘：Image.Save 直接写路径在部分 GDI+ 状态下会报「A generic error occurred in GDI+」
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllBytes(path, ms.ToArray());
            LogSink.Write("[capture] rendered " + path + " (" + w + "x" + h + ")");
        }
        finally
        {
            Width = ow;
            Height = oh;
        }
    }
}
