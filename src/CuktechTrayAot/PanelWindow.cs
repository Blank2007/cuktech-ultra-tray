using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>
/// 无边框深色浮层，三个页签：读数 / 控制 / 模式。
/// 这是 v0.4.0 的 NativeAOT + Win32 版本，画法与旧 WinForms 版逐像素对齐
/// （同一套设计尺寸、同一套颜色、同一批字体字号），区别只是画笔从 TextRenderer/GDI+ 换成自己封的 GdiText/GDI+。
/// </summary>
internal sealed class PanelWindow : WinBase
{
    // ---- 设计尺寸（96 DPI 下的逻辑像素，实际按 DPI 缩放）----
    private const int DesignW = 320;
    // 0.4.2：原来 360 高、行距 42，四行读数一路顶到页脚，看着挤成一坨。
    // 现在整体放高 36，行距 42→48，模式卡 44→50、卡距 52→58，页脚跟着下沉。
    private const int DesignH = 404;
    private const int TabY = 46;
    private const int TabH = 24;
    private const int TabW = 58;
    private const int TabGap = 6;
    private const int BigY = 82;
    private const int LineY = 146;
    private const int HeadY = 152;
    private const int RowTop = 176;
    private const int RowStep = 48;
    private const int FootY = 372;
    // 曲线页（0.5.4 新增的第四个页签）：图表区上沿在副标题下面，下沿给图例和峰值/平均留两行
    private const int ChartTop = 138;
    private const int ChartBottom = 330;
    private const int ChartAxisW = 30;      // 左边留给刻度数字（"160W" 这种）

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
    // 0.4.8：原来 C2 / A 那两行有一层隔行底纹（8% 白），被指出「四行应该长得一样」，整条去掉了
    private static readonly Color BarIdle = Color.FromArgb(58, 63, 72);

    private const string PinGlyph = "\uE718";
    private const string UnpinGlyph = "\uE77A";

    public enum Tab { Read, Control, Mode, Chart }

    private enum StatusKind { None, Busy, Ok, Bad }

    private readonly DeviceClient _client;
    private readonly UiDispatcher _ui;
    private readonly Settings _settings;
    private Snapshot _snap = new();
    private Tab _tab = Tab.Read;

    private DateTime _shownAt;
    private bool _dragging;
    private Native.POINT _dragGrab;

    private bool _busy;
    private bool _demoRender;   // --render-demo：假装已就绪，好看到「能操作」时的配色
    private string _status = "";
    private StatusKind _statusKind;
    private int _hoverTab = -1;
    private bool _hoverSpan;
    private int _hoverSwitch = -1;
    private int _hoverMode = -1;
    private bool _pinHover;
    private int _awayTicks;
    private string _footOverride = "";
    private DateTime _footOverrideUntil;

    private GdiFont _fTitle = null!, _fStatus = null!, _fBig = null!, _fUnit = null!,
                    _fHead = null!, _fName = null!, _fVal = null!, _fValBold = null!,
                    _fFoot = null!, _fTab = null!, _fIcon = null!;

    public int AutoHideAfterMs { get; set; }
    public bool SuppressAutoHide { get; set; }
    public bool Pinned { get; private set; }

    /// <summary>
    /// 面板是否「始终在最前」。跟 Pinned 互相独立：Pinned 只是「不自动收起」，
    /// 这个是压在别的窗口上面；两个可以同时开（固定住、又始终在最前）。
    /// </summary>
    public bool AlwaysOnTop { get; private set; }
    public Tab CurrentTab => _tab;

    /// <summary>最近一次自动收起的时间。点托盘图标的瞬间常常先触发一次「失焦收起」，
    /// 这一次点击不应该又把它弹回来，靠这个时间戳做 300ms 去抖。</summary>
    public DateTime LastAutoHiddenAt { get; private set; }

    public event Action? PinChanged;
    public event Action? CloseRequested;

    public PanelWindow(DeviceClient client, Settings settings, UiDispatcher ui)
    {
        _client = client;
        _ui = ui;
        _settings = settings;
        LogSink.Write("[panel] 1 CreateWin");
        CreateWin(DesignW, DesignH, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP,
            IntPtr.Zero, "panel");
        LogSink.Write("[panel] 2 hwnd=" + Hwnd + " scale=" + Scale.ToString("F2"));
        if (Hwnd == IntPtr.Zero) return;
        _snap = client.Last ?? new Snapshot();
        LogSink.Write("[panel] 3 snap");
        ApplyScale();
        LogSink.Write("[panel] 4 scaled");
        Native.SetTimer(Hwnd, (UIntPtr)1, 350, IntPtr.Zero);
        client.Updated += OnClientUpdated;
        LogSink.Write("[panel] 建立 ok scale=" + Scale.ToString("F2"));
    }

    // 截断而不是四舍五入：旧版每一处都写的 (int)(v * _s)，1.25 倍下 6*1.25=7.5 会被截成 7，
    // 用 Math.Round 会变 8，页签/卡片这些靠间距累加的横坐标就一格一格错开（第三格差 2px）
    private int S(float v) => (int)(v * Scale);

    /// <summary>窗口圆角半径（像素）。绘制和 SetWindowRgn 必须用同一个整数值，
    /// 否则 region 比背景圆角大半个像素时，抗锯齿边缘会和底色混出一丝暗角。</summary>
    private int RadiusPx => S(14);

    /// <summary>能不能真的下发命令（决定按钮是亮色还是灰掉）。</summary>
    private bool CanWrite => (_demoRender || _client.IsReady) && _snap.Connected && !_busy;

    private void OnClientUpdated(Snapshot snap)
        => _ui.Post(_ =>
        {
            _snap = snap?.Clone() ?? new Snapshot();
            Invalidate();
        }, null);

    protected override void OnDpiChanged(float scale, Native.RECT suggested)
    {
        ApplyScale();
    }

    /// <summary>字体量化基准：真机是窗口 DPI 缩放；--render-scale 诊断渲染时由外部指定 1.25。</summary>
    private float _fontBase;

    private void ApplyScale()
    {
        DisposeFonts();
        float s = Scale;
        float fb = _fontBase > 0f ? _fontBase : s;
        LogSink.Write("[panel] fonts start s=" + s.ToString("F2"));
        _fTitle = GdiFont.Create("Microsoft YaHei UI", 13, s, bold: true, fontBase: fb);
        LogSink.Write("[panel] f1=" + _fTitle.Handle);
        _fStatus = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, fontBase: fb);
        _fBig = GdiFont.Create("Segoe UI", 38, s, bold: true, fontBase: fb);
        _fUnit = GdiFont.Create("Segoe UI", 14, s, fontBase: fb);
        // 表头要显示「接口/电压/电流/功率」，必须用中文字体：写成 Segoe UI 时汉字走
        // 字体链接回退，回退字体的 ascent 与 Segoe UI 不同，DrawText 垂直居中会整体上移 2px
        // （旧版 PanelForm.cs:191 用的就是 Microsoft YaHei UI）
        _fHead = GdiFont.Create("Microsoft YaHei UI", 11, s, fontBase: fb);
        _fName = GdiFont.Create("Segoe UI", 13, s, bold: true, fontBase: fb);
        _fVal = GdiFont.Create("Segoe UI", 13, s, fontBase: fb);
        _fValBold = GdiFont.Create("Segoe UI", 14, s, bold: true, fontBase: fb);
        _fFoot = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fTab = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, fontBase: fb);
        _fIcon = GdiFont.Create("Segoe MDL2 Assets", 13, s, fontBase: fb);
        LogSink.Write("[panel] fonts ok");
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, S(DesignW), S(DesignH),
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        LogSink.Write("[panel] pos ok");
        // 圆角是用窗口 region 裁出来的，不是画出来的：双缓冲走的是屏幕兼容 DDB（没有 alpha
        // 通道），Paint 只铺一块圆角背景，矩形窗口的四个角就会露出底色（0.4.2 之前就是这样，
        // 用户看到的是「圆角外补了四个黑角」）。
        SetRoundedRegion(RadiusPx);
        LogSink.Write("[panel] region r=" + S(14) + " w=" + Width + " h=" + Height);
        Invalidate();
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fStatus?.Dispose(); _fBig?.Dispose(); _fUnit?.Dispose();
        _fHead?.Dispose(); _fName?.Dispose(); _fVal?.Dispose(); _fValBold?.Dispose();
        _fFoot?.Dispose(); _fTab?.Dispose(); _fIcon?.Dispose();
    }

    // ==================== 绘制 ====================

    private Color BackdropAt(int y)
    {
        int h = Math.Max(1, Height);
        float t = Math.Clamp(y / (float)h, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(BgTop.R + (BgBottom.R - BgTop.R) * t),
            (int)Math.Round(BgTop.G + (BgBottom.G - BgTop.G) * t),
            (int)Math.Round(BgTop.B + (BgBottom.B - BgTop.B) * t));
    }

    private void Text(Graphics g, string? text, GdiFont font, Color color, float x, float y, float w, float h,
        TextAnchor anchor = TextAnchor.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        var rect = new Rectangle((int)x, (int)y, (int)w, (int)h);
        Color c = color.A < 255 ? GdiText.Blend(color, BackdropAt(rect.Y + rect.Height / 2)) : color;
        GdiText.Draw(g, text, font, c, rect, anchor, ellipsis: true);
    }

    private int MeasureText(Graphics g, string? text, GdiFont font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        IntPtr hdc = g.GetHdc();
        try { return font.Measure(text, hdc); }
        finally { g.ReleaseHdc(hdc); }
    }

    protected override void Paint(Graphics g)
    {
        float s = Scale;
        var full = new RectangleF(0, 0, Width, Height);

        using (var path = RoundRect(full, RadiusPx))
        using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, Height), BgTop, BgBottom))
            g.FillPath(bg, path);
        StrokeRound(g, new RectangleF(0, 0, Width - 1, Height - 1), RadiusPx, BorderColor, 1f);

        float pad = 16 * s;
        Text(g, "酷态科 10 号 Ultra", _fTitle, Fg, pad, 12 * s, 150 * s, 22 * s);

        Color dotColor = _snap.Connected
            ? Ok
            : (_snap.Status.Contains('…') || _snap.Status.Contains("连接") ? Warn : Bad);
        var pinRect = PinButtonRect;
        DrawStatusLabel(g, _snap.Status, dotColor, pinRect.Left - 10 * s);

        using (var pinPath = RoundRect(pinRect, 6 * s))
        {
            // 平时不给底（0.3.3 就是这样）：常显的淡底会让顶部比旧版多一块灰斑
            Color pinBg = Pinned ? Color.FromArgb(40, Ok) : _pinHover ? Color.FromArgb(24, Fg) : Color.FromArgb(0, Fg);
            if (pinBg.A > 0)
                using (var b = new SolidBrush(pinBg)) g.FillPath(b, pinPath);
            // 不加描边：0.3.3 的图钉固定后只有一层绿底（PanelForm.cs:243-248），
            // 多描一圈边会让按钮看着比旧版亮一块
        }
        GdiText.Draw(g, Pinned ? UnpinGlyph : PinGlyph, _fIcon,
            Pinned ? Ok : (_pinHover ? Fg : Faint), pinRect, TextAnchor.Center);

        DrawTab(g, TabReadRect, "读数", _tab == Tab.Read, _hoverTab == 0);
        DrawTab(g, TabControlRect, "控制", _tab == Tab.Control, _hoverTab == 1);
        DrawTab(g, TabModeRect, "模式", _tab == Tab.Mode, _hoverTab == 2);
        DrawTab(g, TabChartRect, "曲线", _tab == Tab.Chart, _hoverTab == 3);

        if (_tab == Tab.Read) PaintReadPage(g);
        else if (_tab == Tab.Control) PaintControlPage(g);
        else if (_tab == Tab.Mode) PaintModePage(g);
        else PaintChartPage(g);

        var (footText, footColor) = FootStatus();
        Text(g, footText, _fFoot, footColor, pad, FootY * s, Width - pad * 2, 18 * s);
    }

    private void PaintReadPage(Graphics g)
    {
        float s = Scale;
        float pad = 16 * s;

        double total = _snap.TotalWatts;
        string big = _snap.Connected ? WattStyle.Format(total) : "--";
        Color bigColor = _snap.Connected ? WattStyle.Total(total) : Faint;
        var bigRect = new Rectangle((int)pad, (int)(BigY * s), (int)(220 * s), (int)(52 * s));
        GdiText.Draw(g, big, _fBig, bigColor, bigRect, TextAnchor.Left);
        int bigW = MeasureText(g, big, _fBig);
        Text(g, "W", _fUnit, Dim, pad + bigW + 4 * s, (BigY + 14) * s, 30 * s, 24 * s);
        Text(g, "总输出", _fHead, Faint, pad + bigW + 40 * s, (BigY + 16) * s, 70 * s, 20 * s);

        using (var pen = new Pen(SepColor, Math.Max(1, s)))
            g.DrawLine(pen, pad, LineY * s, Width - pad, LineY * s);

        Text(g, "接口", _fHead, Faint, pad, HeadY * s, 40 * s, 18 * s);
        Text(g, "电压", _fHead, Faint, 56 * s, HeadY * s, 76 * s, 18 * s, TextAnchor.Right);
        Text(g, "电流", _fHead, Faint, 132 * s, HeadY * s, 76 * s, 18 * s, TextAnchor.Right);
        Text(g, "功率", _fHead, Faint, 208 * s, HeadY * s, 96 * s, 18 * s, TextAnchor.Right);

        for (int i = 0; i < 4; i++)
            DrawPort(g, _snap.Ports[i], RowTop * s + i * RowStep * s, i);
    }

    private void PaintControlPage(Graphics g)
    {
        float s = Scale;
        float pad = 16 * s;

        Text(g, "点右边的开关，充电器立刻就断/通，不用再保存", _fFoot, Dim, pad, 112 * s, 260 * s, 18 * s);

        Text(g, "接口", _fHead, Faint, pad, HeadY * s, 40 * s, 18 * s);
        Text(g, "实时读数", _fHead, Faint, 54 * s, HeadY * s, 120 * s, 18 * s);
        Text(g, "开关", _fHead, Faint, 256 * s, HeadY * s, 48 * s, 18 * s, TextAnchor.Right);

        for (int i = 0; i < 4; i++)
            DrawControlRow(g, _snap.Ports[i], RowTop * s + i * RowStep * s, i);
    }

    private void PaintModePage(Graphics g)
    {
        float s = Scale;
        float pad = 16 * s;
        Text(g, "点一下就切，充电器立刻换策略，不用再保存", _fFoot, Dim, pad, 100 * s, 262 * s, 18 * s);
        for (int i = 0; i < DeviceClient.SceneModes.Length; i++)
            DrawModeCard(g, ModeCardRect(i), i);
    }

    // ==================== 曲线页（0.5.4） ====================

    /// <summary>四路各自的线色：蓝 / 绿 / 橙 / 紫。挑的是深色底上能互相区分、也不跟「绿=正常、红=报错」撞的色相。</summary>
    private static readonly Color[] LineColors =
    {
        Color.FromArgb(59, 130, 246),
        Color.FromArgb(34, 197, 94),
        Color.FromArgb(245, 158, 11),
        Color.FromArgb(167, 139, 250),
    };

    /// <summary>图例文字。第三路固定写 C3+A：合并时它就是一条线，分开时也只是这一路的曲线。</summary>
    private static readonly string[] LineNames = { "C1", "C2", "C3+A", "A" };

    /// <summary>点时长胶囊循环的四档（秒）。</summary>
    internal static readonly int[] SpanChoices = { 60, 300, 900, 1800 };

    internal int ChartSpan
    {
        get
        {
            int v = _settings.ChartSeconds;
            return v >= 10 && v <= 1800 ? v : 60;
        }
    }

    internal static string SpanLabel(int sec) => sec switch
    {
        60 => "最近 1 分钟",
        300 => "最近 5 分钟",
        900 => "最近 15 分钟",
        1800 => "最近 30 分钟",
        _ => "最近 " + sec + " 秒",
    };

    /// <summary>点一下时长胶囊：在四档预设里循环；当前是自定义值（菜单里填的）就回到最小那档。</summary>
    private void CycleChartSpan()
    {
        int cur = ChartSpan;
        int next = SpanChoices[0];
        for (int i = 0; i < SpanChoices.Length; i++)
        {
            if (SpanChoices[i] == cur) { next = SpanChoices[(i + 1) % SpanChoices.Length]; break; }
        }
        SetChartSpan(next);
    }

    internal void SetChartSpan(int sec)
    {
        if (sec < 10 || sec > 1800) return;
        _settings.ChartSeconds = sec;
        _settings.Save();
        Invalidate();
    }

    /// <summary>纵轴顶：20W 一档向上取整，最小 20W。固定 20 的倍数，刻度写出来是整数，看着不脏。</summary>
    private static double NiceMax(double w)
        => w <= 20 ? 20 : Math.Ceiling(w / 20.0) * 20;

    private void PaintChartPage(Graphics g)
    {
        float s = Scale;
        float pad = 16 * s;
        int span = ChartSpan;

        Text(g, "每路一条线，点右边换时长", _fFoot, Dim, pad, 100 * s, 176 * s, 18 * s);
        var spanRect = ChartSpanRect;
        using (var path = RoundRect(spanRect, 10 * s))
        {
            using (var b = new SolidBrush(_hoverSpan ? Color.FromArgb(38, 44, 54) : Color.FromArgb(26, 30, 36)))
                g.FillPath(b, path);
            using (var pen = new Pen(BorderColor, 1f)) g.DrawPath(pen, path);
        }
        Text(g, SpanLabel(span), _fFoot, Fg, spanRect.X, spanRect.Y, spanRect.Width, spanRect.Height, TextAnchor.Center);

        int x0 = S(16 + ChartAxisW), x1 = S(DesignW - 16), y0 = S(ChartTop), y1 = S(ChartBottom);
        var pts = PowerHistory.Snapshot(span);

        double max = 0;
        // 每一路自己在窗口里的峰值：整条都是 0 的那一路不画线（插着但没输出，画出来就是贴在 0 轴上的一条横线，
        // 看着像坐标轴重了一条，其实一点信息都没有 —— 用户就是这么反馈的第一眼印象）
        var peakEach = new double[4];
        foreach (var p in pts)
        {
            for (int i = 0; i < 4; i++)
            {
                double w = p.At(i);
                if (w > max) max = w;
                if (w > peakEach[i]) peakEach[i] = w;
            }
        }
        double top = NiceMax(max);

        for (int k = 0; k <= 2; k++)
        {
            double v = top * k / 2.0;
            int y = y1 - (int)((y1 - y0) * (v / top));
            using (var pen = new Pen(k == 0 ? Color.FromArgb(52, 57, 66) : SepColor, 1f))
                g.DrawLine(pen, x0, y, x1, y);
            if (k > 0)
                Text(g, ((int)Math.Round(v)) + "W", _fFoot, Faint, pad, y - S(9), S(ChartAxisW - 4), S(16), TextAnchor.Right);
        }
        Text(g, "0W", _fFoot, Faint, pad, y1 - S(9), S(ChartAxisW - 4), S(16), TextAnchor.Right);

        if (pts.Count < 2)
        {
            Text(g, "数据还不够，等十几秒再看", _fFoot, Faint, x0, S((ChartTop + ChartBottom) / 2), x1 - x0, S(18), TextAnchor.Center);
        }
        else
        {
            double from = PowerHistory.Now - span;
            var seg = new List<PointF>();
            for (int i = 0; i < 4; i++)
            {
                if (peakEach[i] < 0.5) continue;   // 这一路整段都没输出，不画
                seg.Clear();
                using var pen = new Pen(LineColors[i], Math.Max(1f, 1.6f * s)) { LineJoin = LineJoin.Round };
                void Flush()
                {
                    if (seg.Count >= 2) g.DrawLines(pen, seg.ToArray());
                    else if (seg.Count == 1)
                        using (var b = new SolidBrush(LineColors[i]))
                            g.FillEllipse(b, seg[0].X - 1.5f * s, seg[0].Y - 1.5f * s, 3 * s, 3 * s);
                    seg.Clear();
                }
                foreach (var p in pts)
                {
                    double w = p.At(i);
                    if (w < 0) { Flush(); continue; }
                    float x = x0 + (float)((p.T - from) / span * (x1 - x0));
                    if (x < x0) x = x0;
                    if (x > x1) x = x1;
                    float y = y1 - (float)(Math.Min(w, top) / top * (y1 - y0));
                    seg.Add(new PointF(x, y));
                }
                Flush();
            }
        }

        // 图例：四个小方块 + 名字，一行排开
        float lx = pad;
        for (int i = 0; i < 4; i++)
        {
            var box = new Rectangle((int)lx, (int)(336 * s + 3 * s), S(10), S(10));
            bool live = peakEach[i] >= 0.5;
            FillRound(g, box, 2 * s, live ? LineColors[i] : Faint);
            Text(g, LineNames[i], _fFoot, live ? Dim : Faint, lx + 14 * s, 334 * s, 52 * s, 16 * s);
            lx += 62 * s;
        }

        if (pts.Count >= 2)
        {
            double peak = 0, sum = 0;
            int n = 0;
            foreach (var p in pts)
            {
                double w = Math.Max(0, p.At(0)) + Math.Max(0, p.At(1)) + Math.Max(0, p.At(2)) + Math.Max(0, p.At(3));
                if (w > peak) peak = w;
                sum += w;
                n++;
            }
            Text(g, "峰值 " + WattStyle.Format(peak) + " W", _fFoot, Dim, pad, FootY * s - 18 * s, 132 * s, 16 * s);
            Text(g, "平均 " + WattStyle.Format(n > 0 ? sum / n : 0) + " W", _fFoot, Dim, 150 * s, FootY * s - 18 * s, 138 * s, 16 * s);
        }
    }

    private void DrawModeCard(Graphics g, Rectangle r, int index)
    {
        float s = Scale;
        var (value, name, hint) = DeviceClient.SceneModes[index];
        bool active = _snap.SceneMode == value;
        bool hover = _hoverMode == index;
        bool canWrite = CanWrite;

        int alpha = active ? 28 : hover ? 16 : 9;
        using (var path = RoundRect(r, 10 * s))
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255))) g.FillPath(b, path);
            var edge = active
                ? Color.FromArgb(canWrite ? 96 : 44, Ok)
                : Color.FromArgb(hover ? 44 : 16, 255, 255, 255);
            using (var pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
        }

        if (active)
        {
            var bar = new Rectangle(r.X + S(1), r.Y + S(10), Math.Max(1, S(3)), r.Height - S(20));
            using var barPath = RoundRect(bar, 1.5f * s);
            using var barBrush = new SolidBrush(canWrite ? Ok : Color.FromArgb(110, Ok));
            g.FillPath(barBrush, barPath);
        }

        var nameColor = active ? Fg : canWrite ? Color.FromArgb(198, 205, 213) : Color.FromArgb(126, 133, 142);
        Text(g, name, _fName, nameColor, r.X + 14 * s, r.Y + 6 * s, r.Width - 84 * s, 20 * s);
        var hintColor = active ? Color.FromArgb(158, 166, 176) : Color.FromArgb(116, 124, 134);
        Text(g, hint, _fFoot, hintColor, r.X + 14 * s, r.Y + 26 * s, r.Width - 84 * s, 16 * s);

        if (active)
        {
            string tag = "当前";
            int tw = (int)(MeasureText(g, tag, _fFoot) + 16 * s);
            var tr = new Rectangle(r.Right - tw - S(12), r.Y + (r.Height - S(20)) / 2, tw, S(20));
            using (var tp = RoundRect(tr, 10 * s))
            using (var tb = new SolidBrush(Color.FromArgb(canWrite ? 60 : 34, Ok)))
                g.FillPath(tb, tp);
            Text(g, tag, _fFoot, canWrite ? Ok : Color.FromArgb(130, Ok), tr.X, tr.Y, tr.Width, tr.Height, TextAnchor.Center);
        }
    }

    private void DrawControlRow(Graphics g, PortReading p, float top, int index)
    {
        float s = Scale;
        float pad = 16 * s;
        bool on = p.Enabled;

        var barColor = !p.Enabled ? Color.FromArgb(70, 76, 86)
                     : p.Working ? WattStyle.Port(p.Watts)
                     : BarIdle;
        var bar = new Rectangle(S(4), (int)(top + 4 * s), Math.Max(1, S(3)), S(14));
        FillRound(g, bar, 1.5f * s, barColor);

        bool mergedOwner = _snap.IsMergedOwner(index);
        Text(g, mergedOwner ? Snapshot.MergedName : p.Name, _fName, on ? Fg : Color.FromArgb(80, 85, 94), pad, top, 40 * s, 22 * s);

        string vals;
        Color valColor;
        // 灰阶跟 0.3.3 完全一致：(95,100,109) 用于「已断电 / — / 空载」，别用 Dim(154,160,166)
        if (!p.Enabled) { vals = "已断电"; valColor = Color.FromArgb(95, 100, 109); }
        else if (_snap.IsMergedFollower(index)) { vals = "并入 " + Snapshot.MergedName; valColor = Color.FromArgb(95, 100, 109); }
        else if (!p.HasData) { vals = "—"; valColor = Color.FromArgb(95, 100, 109); }
        else if (p.Idle) { vals = "空载"; valColor = Color.FromArgb(95, 100, 109); }
        else
        {
            vals = p.Volts.ToString("F1") + "V · " + p.Amps.ToString("F2") + "A · " + p.Watts.ToString("F1") + "W";
            valColor = Fg;
        }
        Text(g, vals, _fVal, valColor, 54 * s, top, 196 * s, 22 * s);

        DrawSwitch(g, SwitchRect(index), p.Enabled, _hoverSwitch == index);
    }

    private void DrawSwitch(Graphics g, Rectangle r, bool on, bool hover)
    {
        float s = Scale;
        if (r.Width < 8 || r.Height < 6) return;

        bool canWrite = CanWrite;
        Color track = on
            ? (canWrite ? Ok : Color.FromArgb(56, 96, 82))
            : (canWrite ? Color.FromArgb(74, 80, 92) : Color.FromArgb(58, 63, 72));

        float radius = Math.Min(r.Width, r.Height) / 2f;
        using (var path = RoundRect(r, radius))
        {
            using (var b = new SolidBrush(track)) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(hover ? 170 : 52, 255, 255, 255), 1f))
                g.DrawPath(pen, path);
        }

        int d = r.Height - S(6);
        if (d < 6) d = r.Height - 2;
        if (d < 4) return;
        int ky = r.Y + (r.Height - d) / 2;
        int kx = on ? r.Right - d - S(3) : r.X + S(3);
        using (var knob = new SolidBrush(canWrite ? Color.FromArgb(242, 246, 248) : Color.FromArgb(146, 154, 162)))
            g.FillEllipse(knob, kx, ky, d, d);

        string label = on ? "开" : "关";
        var labelColor = on ? Color.FromArgb(16, 38, 30) : Color.FromArgb(214, 221, 229);
        int lx = on ? r.X + S(7) : r.X + d + S(5);
        int lw = r.Width - d - S(9);
        if (lw > 4) Text(g, label, _fTab, labelColor, lx, r.Y, lw, r.Height);
    }

    /// <summary>
    /// 顶部状态：一个圆点 + 「已连接」几个字，**不铺任何底色、不描边**。
    /// 0.4.3 一度把底色连同文字一起去掉，用户说文字是要的，只是那块 30% 透明绿底比读数还抢眼。
    /// 文字串仍取 _snap.Status（已连接 / 连接中… / 未连接）。
    /// </summary>
    private void DrawStatusLabel(Graphics g, string text, Color dotColor, float right)
    {
        float s = Scale;
        float dot = 8 * s;
        float gap = 6 * s;
        float tw = MeasureText(g, text, _fStatus);
        float h = 22 * s;               // 跟原来胶囊同高同中线（y=11..33）
        float x = right - (dot + gap + tw);
        float y = 11 * s;
        using (var b = new SolidBrush(dotColor))
            g.FillEllipse(b, x, y + (h - dot) / 2f, dot, dot);
        Text(g, text, _fStatus, Fg, x + dot + gap, y, tw + 2 * s, h);
    }

    private void DrawTab(Graphics g, Rectangle r, string text, bool active, bool hover)
    {
        int alpha = active ? 34 : hover ? 18 : 10;
        using (var path = RoundRect(r, r.Height / 2f))
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255))) g.FillPath(b, path);
            if (active)
                using (var pen = new Pen(Color.FromArgb(70, Ok))) g.DrawPath(pen, path);
        }
        Text(g, text, _fTab, active ? Fg : Faint, r.X, r.Y, r.Width, r.Height, TextAnchor.Center);
    }

    private void DrawPort(Graphics g, PortReading p, float top, int index)
    {
        float s = Scale;
        float pad = 16 * s;
        bool on = _snap.Connected && p.Enabled;
        bool working = on && p.Working;

        var barColor = working ? WattStyle.Port(p.Watts) : BarIdle;
        var bar = new Rectangle(S(4), (int)(top + 4 * s), Math.Max(1, S(3)), S(14));
        FillRound(g, bar, 1.5f * s, barColor);

        // 合并显示（issue #1）：C3+A 共用一路功率计，正主那行写「C3+A」，另一行说明并到哪去了
        bool mergedOwner = _snap.IsMergedOwner(index);

        var nameColor = working ? Fg : p.Enabled ? Faint : Color.FromArgb(80, 85, 94);
        Text(g, mergedOwner ? Snapshot.MergedName : p.Name, _fName, nameColor, pad, top, 40 * s, 22 * s);

        if (working)
        {
            Text(g, p.Volts.ToString("F1"), _fVal, Fg, 56 * s, top, 62 * s, 22 * s, TextAnchor.Right);
            Text(g, "V", _fFoot, Faint, 56 * s + 62 * s + 1 * s, top, 12 * s, 22 * s);
            Text(g, p.Amps.ToString("F2"), _fVal, Fg, 132 * s, top, 62 * s, 22 * s, TextAnchor.Right);
            Text(g, "A", _fFoot, Faint, 132 * s + 62 * s + 1 * s, top, 12 * s, 22 * s);
            Text(g, p.Watts.ToString("F1"), _fValBold, WattStyle.Port(p.Watts), 200 * s, top, 92 * s, 22 * s, TextAnchor.Right);
            Text(g, "W", _fFoot, Faint, Width - pad - 8 * s, top, 12 * s, 22 * s);
        }
        else if (_snap.IsMergedFollower(index))
        {
            // 这一路的功率被并进了上面那行，所以不说「空闲」，直接说明它去哪了
            Text(g, "并入 " + Snapshot.MergedName, _fVal, Color.FromArgb(120, 127, 137),
                56 * s, top, Width - pad - 56 * s, 22 * s, TextAnchor.Right);
        }
        else
        {
            string hint = !p.Enabled ? "已关闭" : p.HasData ? "空闲" : "—";
            Text(g, hint, _fVal, Color.FromArgb(95, 100, 109), 56 * s, top, Width - pad - 56 * s, 22 * s, TextAnchor.Right);
        }
    }

    private (string Text, Color Color) FootStatus()
    {
        if (_footOverride.Length > 0 && DateTime.UtcNow < _footOverrideUntil)
            return (_footOverride, Fg);
        if (_status.Length > 0)
        {
            Color c = _statusKind switch
            {
                StatusKind.Ok => Ok,
                StatusKind.Bad => Bad,
                StatusKind.Busy => Dim,
                _ => Faint,
            };
            return (_status, c);
        }
        if (_snap.Error is { Length: > 0 } err) return (err, Bad);
        if (_snap.At == DateTime.MinValue) return ("等待数据…", Faint);
        return ("固件 " + (_snap.Firmware.Length > 0 ? _snap.Firmware : "?") + " · 更新 " + _snap.At.ToString("HH:mm:ss"), Faint);
    }

    // ==================== 命中区域 ====================

    private Rectangle TabReadRect => new(S(16), S(TabY), S(TabW), S(TabH));

    private Rectangle TabControlRect
    {
        get
        {
            var r = TabReadRect;
            return new Rectangle(r.Right + S(TabGap), r.Y, r.Width, r.Height);
        }
    }

    private Rectangle TabModeRect
    {
        get
        {
            var r = TabControlRect;
            return new Rectangle(r.Right + S(TabGap), r.Y, r.Width, r.Height);
        }
    }

    private Rectangle TabChartRect
    {
        get
        {
            var r = TabModeRect;
            return new Rectangle(r.Right + S(TabGap), r.Y, r.Width, r.Height);
        }
    }

    /// <summary>曲线页右上角那枚「最近 N 分钟」胶囊，点一下换一档。</summary>
    private Rectangle ChartSpanRect => new(Width - S(16) - S(92), S(94), S(92), S(20));

    private Rectangle ModeCardRect(int i) => new(S(14), S(128 + i * 58), Width - S(28), S(50));

    private int ModeHit(int x, int y)
    {
        if (_tab != Tab.Mode) return -1;
        var p = new Point(x, y);
        for (int i = 0; i < DeviceClient.SceneModes.Length; i++)
            if (ModeCardRect(i).Contains(p)) return i;
        return -1;
    }

    private Rectangle SwitchRect(int i) => new(Width - S(16) - S(48), S(RowTop + i * RowStep - 3), S(48), S(28));

    private int SwitchHit(int x, int y)
    {
        if (_tab != Tab.Control) return -1;
        var p = new Point(x, y);
        for (int i = 0; i < 4; i++)
            if (SwitchRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>跟右边那个状态点同一行：点在 y=11..33，图钉 20×20 落在 10..30，看着是一条线。</summary>
    // 20×20、距右 16、距顶 10，与旧版 PanelForm.cs:635-636 完全一致
    private Rectangle PinButtonRect => new(Width - S(16) - S(20), S(10), S(20), S(20));

    // ==================== 交互 ====================

    protected override bool WantsHandCursor(int x, int y)
        => PinButtonRect.Contains(x, y)
        || TabReadRect.Contains(x, y) || TabControlRect.Contains(x, y)
        || TabModeRect.Contains(x, y) || TabChartRect.Contains(x, y)
        || SwitchHit(x, y) >= 0 || ModeHit(x, y) >= 0
        || (_tab == Tab.Chart && ChartSpanRect.Contains(x, y));

    protected override void OnLeftDown(int x, int y)
    {
        if (PinButtonRect.Contains(x, y)) { TogglePin(); return; }
        if (TabReadRect.Contains(x, y)) { SetTab(Tab.Read); return; }
        if (TabControlRect.Contains(x, y)) { SetTab(Tab.Control); return; }
        if (TabModeRect.Contains(x, y)) { SetTab(Tab.Mode); return; }
        if (TabChartRect.Contains(x, y)) { SetTab(Tab.Chart); return; }
        if (_tab == Tab.Chart && ChartSpanRect.Contains(x, y)) { CycleChartSpan(); return; }

        int mode = ModeHit(x, y);
        if (mode >= 0) { _ = SetModeAsync(DeviceClient.SceneModes[mode].Value); return; }

        int sw = SwitchHit(x, y);
        if (sw >= 0) { _ = TogglePortAsync(sw); return; }

        _dragging = true;
        Native.GetCursorPos(out var pt);
        Native.GetWindowRect(Hwnd, out var wr);
        _dragGrab = new Native.POINT(pt.X - wr.Left, pt.Y - wr.Top);
    }

    protected override void OnLeftUp(int x, int y) => _dragging = false;

    protected override void OnMouseMove(int x, int y, bool entering)
    {
        bool pinHover = PinButtonRect.Contains(x, y);
        bool changed = pinHover != _pinHover;
        _pinHover = pinHover;

        int tabHover = TabReadRect.Contains(x, y) ? 0
                     : TabControlRect.Contains(x, y) ? 1
                     : TabModeRect.Contains(x, y) ? 2
                     : TabChartRect.Contains(x, y) ? 3 : -1;
        bool spanHover = _tab == Tab.Chart && ChartSpanRect.Contains(x, y);
        if (spanHover != _hoverSpan) changed = true;
        _hoverSpan = spanHover;
        int swHover = SwitchHit(x, y);
        int modeHover = ModeHit(x, y);
        if (tabHover != _hoverTab || swHover != _hoverSwitch || modeHover != _hoverMode) changed = true;
        _hoverTab = tabHover;
        _hoverSwitch = swHover;
        _hoverMode = modeHover;
        if (changed) Invalidate();

        if (_dragging)
        {
            Native.GetCursorPos(out var pt);
            int nx = pt.X - _dragGrab.X, ny = pt.Y - _dragGrab.Y;
            // 别让面板被拖到屏幕外面去：至少留 60dip 露在屏幕里，否则位置一存盘下次就找不回来了
            ClampKeepVisible(ref nx, ref ny, S(DesignW), S(DesignH), S(60), S(60));
            Native.SetWindowPos(Hwnd, IntPtr.Zero, nx, ny, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
    }

    protected override void OnMouseLeave()
    {
        if (_hoverTab == -1 && _hoverSwitch == -1 && _hoverMode == -1 && !_pinHover && !_hoverSpan) return;
        _hoverTab = -1;
        _hoverSwitch = -1;
        _hoverMode = -1;
        _hoverSpan = false;
        _pinHover = false;
        Invalidate();
    }

    protected override void OnKeyDown(int vk)
    {
        if (vk == 0x1B) { HideNow("esc"); return; }  // ESC
        if (vk == 0x09)                              // TAB：循环切页
        {
            SetTab(_tab switch
            {
                Tab.Read => Tab.Control,
                Tab.Control => Tab.Mode,
                Tab.Mode => Tab.Chart,
                _ => Tab.Read,
            });
            return;
        }
        // 1/2/3/4 直达四页，跟页签从左到右上顺序一致
        if (vk >= 0x31 && vk <= 0x34) SetTab((Tab)(vk - 0x31));
    }

    protected override void OnActivate(bool active)
    {
        if (active) return;
        if (!SuppressAutoHide && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 350)
            HideNow("deactivate");
    }

    protected override void OnWake() => _ui.Drain();

    protected override void OnTimer(int id)
    {
        if (id != 1) return;
        GuardTick();
    }

    /// <summary>开发期自检用：塞一份假快照，好在没设备的时候检查读数页排版。</summary>
    internal void SetDemoSnapshot(Snapshot snap)
    {
        _snap = snap;
        _demoRender = true;
        Invalidate();
    }

    /// <summary>渲染诊断用：强制按指定缩放画（例如 2.5 倍，好跟旧版 800x900 截图逐像素比）。</summary>
    internal void ForceScale(float scale, float fontBase = 1.25f)
    {
        if (scale < 0.5f || scale > 4f) return;
        Scale = scale;
        _fontBase = fontBase > 0f ? fontBase : scale;
        ApplyScale();   // 字体是按 Scale 建的，改缩放必须重建，否则字号和排版对不上
    }

    /// <summary>把当前一帧画进 PNG（会话内自检用：不依赖截图工具，也不受 DPI 虚拟化影响）。</summary>
    public void SaveRender(string path)
    {
        int w = Math.Max(1, S(DesignW)), h = Math.Max(1, S(DesignH));
        // Paint 是照 Width/Height 画的（窗口客户区），渲染时得让它们跟位图一致，
        // 否则 ForceScale 之后背景只铺满原来的客户区，右下一大片空白。
        int ow = Width, oh = Height;
        Width = w;
        Height = h;
        try
        {
            // 必须走跟真机完全同一条渲染路径（屏幕兼容 DDB）：换成 Bitmap+FromImage
            // 导出的图会比真机还虚，拿它跟旧版截图比对会得出错误结论。
            using var surf = Surface.Create(w, h);
            if (surf is null) return;
            surf.G.Clear(BgBottom);
            Paint(surf.G);
            using var bmp = surf.ToBitmap();
            // 导出的诊断图也把四角抠成透明：文档和视频素材直接用这些 PNG，四角留一块深色方块很扎眼。
            using var shot = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g2 = Graphics.FromImage(shot))
            {
                g2.Clear(Color.Transparent);
                using var clip = RoundRect(new RectangleF(0, 0, w, h), RadiusPx);
                g2.SetClip(clip);
                g2.DrawImage(bmp, 0, 0, w, h);
            }
            shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            Width = ow;
            Height = oh;
        }
    }

    private void GuardTick()
    {
        if (!Visible || Pinned || SuppressAutoHide) { _awayTicks = 0; return; }

        var alive = DateTime.UtcNow - _shownAt;
        if (AutoHideAfterMs > 0 && alive.TotalMilliseconds > AutoHideAfterMs) { HideNow("ttl"); return; }
        if (alive.TotalMilliseconds < 400) { _awayTicks = 0; return; }
        if (IsOwnWindow(Native.GetForegroundWindow())) { _awayTicks = 0; return; }
        if (++_awayTicks < 2) return;
        _awayTicks = 0;
        HideNow("guard");
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    public void TogglePin()
    {
        Pinned = !Pinned;
        SuppressAutoHide = Pinned;
        if (Pinned) _shownAt = DateTime.UtcNow;
        FootOverride(Pinned ? "已固定到桌面，不会自己收起了" : "已取消固定，点别处就收起");
        PinChanged?.Invoke();
        Invalidate();
    }

    public void RestorePinned(bool value)
    {
        Pinned = value;
        SuppressAutoHide = value;
    }

    /// <summary>
    /// 置顶开关。置顶是窗口的一个持久样式，跟显示/隐藏无关，所以这里直接落一次即可；
    /// 每次 ShowAt 里会再落一次，防止某些显示路径把 Z 序拉回去。
    /// </summary>
    /// <param name="toast">true = 在页脚提示一句（用户主动切换时）；启动时按配置恢复就不吵。</param>
    public void SetAlwaysOnTop(bool value, bool toast = false)
    {
        bool changed = AlwaysOnTop != value;
        AlwaysOnTop = value;
        ApplyTopMost();
        if (changed)
        {
            LogSink.Write("[panel] alwaysOnTop=" + value);
            if (toast) FootOverride(value ? "面板已置顶，会压在其他窗口上面" : "已取消置顶，会被其他窗口盖住");
        }
        Invalidate();
    }

    public void ToggleAlwaysOnTop() => SetAlwaysOnTop(!AlwaysOnTop, toast: true);

    /// <summary>只改 Z 序：不移动、不改大小、不抢焦点。</summary>
    private void ApplyTopMost()
    {
        if (!IsValid) return;
        Native.SetWindowPos(Hwnd,
            new IntPtr(AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST),
            0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void FootOverride(string text, double seconds = 3.0)
    {
        _footOverride = text;
        _footOverrideUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    public void SetTab(Tab tab)
    {
        _tab = tab;
        _hoverTab = -1;
        _hoverSwitch = -1;
        _hoverMode = -1;
        _status = "";
        _statusKind = StatusKind.None;
        _snap = _client.Last?.Clone() ?? _snap;
        if (tab == Tab.Mode) _ = RefreshModeAsync();
        Invalidate();
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status = text;
        _statusKind = kind;
        Invalidate();
    }

    private async Task RefreshModeAsync()
    {
        if (!_client.IsReady) return;
        try
        {
            var res = await _client.RefreshSceneModeAsync();
            if (!res.Ok) _ui.Post(_ => SetStatus("读当前模式失败：" + res.Error, StatusKind.Bad), null);
        }
        catch { }
    }

    private async Task SetModeAsync(int mode)
    {
        if (_busy) return;
        if (!_client.IsReady || !_snap.Connected)
        {
            SetStatus("还没连上充电器，先等下", StatusKind.Bad);
            return;
        }
        _busy = true;
        SetStatus("正在切换…", StatusKind.Busy);
        try
        {
            var res = await _client.SetSceneModeAsync(mode);
            string name = "未知";
            foreach (var m in DeviceClient.SceneModes) if (m.Value == mode) name = m.Name;
            _ui.Post(_ =>
            {
                _busy = false;
                _snap.SceneMode = mode;
                if (res.Ok) SetStatus("已切到「" + name + "」", StatusKind.Ok);
                else SetStatus("切换失败：" + res.Error, StatusKind.Bad);
            }, null);
        }
        catch (Exception ex)
        {
            _ui.Post(_ => { _busy = false; SetStatus("切换失败：" + ex.Message, StatusKind.Bad); }, null);
        }
    }

    private async Task TogglePortAsync(int index)
    {
        if (_busy) return;
        if (!_client.IsReady || !_snap.Connected)
        {
            SetStatus("还没连上充电器，先等下", StatusKind.Bad);
            return;
        }
        var port = _snap.Ports[index];
        bool target = !port.Enabled;
        _busy = true;
        SetStatus("正在" + (target ? "打开 " : "关闭 ") + port.Name + "…", StatusKind.Busy);
        try
        {
            var res = await _client.SetPortEnabledAsync(index, target);
            _ui.Post(_ =>
            {
                _busy = false;
                if (res.Ok)
                {
                    port.Enabled = target;
                    SetStatus(port.Name + (target ? " 已通电" : " 已断电"), StatusKind.Ok);
                }
                else SetStatus(port.Name + " 操作失败：" + res.Error, StatusKind.Bad);
            }, null);
        }
        catch (Exception ex)
        {
            _ui.Post(_ => { _busy = false; SetStatus(port.Name + " 操作失败：" + ex.Message, StatusKind.Bad); }, null);
        }
    }

    // ==================== 显示 / 隐藏 ====================

    public void ShowNear(Native.POINT anchor)
    {
        int w = S(DesignW), h = S(DesignH);
        IntPtr mon = Native.MonitorFromPoint(anchor, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(mon, ref mi)) return;
        var wa = mi.rcWork;
        int x = Math.Clamp(anchor.X - w / 2, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - w - 8));
        int y = anchor.Y - h - 8;
        if (y < wa.Top + 8) y = Math.Min(anchor.Y + 12, Math.Max(wa.Top + 8, wa.Bottom - h - 8));
        ShowAt(x, y);
    }

    public void ShowAt(int x, int y)
    {
        Native.SetWindowPos(Hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _shownAt = DateTime.UtcNow;
        _awayTicks = 0;
        Native.ShowWindow(Hwnd, Native.SW_SHOW);
        if (AlwaysOnTop) ApplyTopMost();   // 显示之后再压一次，别被这次显示把 Z 序带回普通层
        if (!Pinned)
        {
            Native.SetForegroundWindow(Hwnd);
            Native.SetActiveWindow(Hwnd);
            Native.SetFocus(Hwnd);
        }
        Invalidate();
    }

    /// <summary>
    /// 按记住的位置显示。位置已经不在任何屏幕上（换过分辨率、缩放过、或者被拖出了界）就挪回离它最近的屏幕，
    /// 否则窗口照样「存在且可见」，但用户什么都看不见 —— 0.5.1 就是这么把面板弄丢的。
    /// </summary>
    public void ShowAtClamped(int x, int y)
    {
        int w = S(DesignW), h = S(DesignH);
        int nx = x, ny = y;
        ClampKeepVisible(ref nx, ref ny, w, h, w, h);
        if (nx != x || ny != y)
            LogSink.Write("[panel] 记住的位置在屏幕外 " + x + "," + y + " → 挪到 " + nx + "," + ny);
        ShowAt(nx, ny);
    }

    /// <summary>
    /// 把窗口左上角收进离它最近的那块屏幕。keepW/keepH 是必须露在屏幕里的宽高：
    /// 给整块宽高 = 完全收进来；给小一点 = 允许一半探出去（拖动时用）。
    /// </summary>
    private static void ClampKeepVisible(ref int x, ref int y, int w, int h, int keepW, int keepH)
    {
        var pt = new Native.POINT { X = x, Y = y };
        IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(mon, ref mi)) return;
        var wa = mi.rcWork;
        int loX = wa.Left - w + keepW, hiX = wa.Right - keepW;
        int loY = wa.Top - h + keepH, hiY = wa.Bottom - keepH;
        if (loX <= hiX) { if (x < loX) x = loX; if (x > hiX) x = hiX; }
        if (loY <= hiY) { if (y < loY) y = loY; if (y > hiY) y = hiY; }
    }

    public void ShowAtCurrent()
    {
        Native.GetWindowRect(Hwnd, out var r);
        _shownAt = DateTime.UtcNow;
        _awayTicks = 0;
        Native.ShowWindow(Hwnd, Native.SW_SHOW);
        if (AlwaysOnTop) ApplyTopMost();
        if (!Pinned)
        {
            Native.SetForegroundWindow(Hwnd);
            Native.SetActiveWindow(Hwnd);
        }
        Invalidate();
    }

    public void HideNow(string why, bool force = false)
    {
        if (Pinned && !force) return;
        if (!Visible) return;
        LastAutoHiddenAt = DateTime.UtcNow;
        Native.ShowWindow(Hwnd, Native.SW_HIDE);
        LogSink.Write("[panel] hide (" + why + ")");
        CloseRequested?.Invoke();
    }

    /// <summary>锁屏 / 切换用户 / 远程断开：没人看着的时候浮层没必要留着。</summary>
    public void OnSessionLocked()
    {
        if (!Pinned) HideNow("session-lock");
    }

    /// <summary>窗口在屏幕上的矩形（算面板位置、判断鼠标在不在里面都靠它）。</summary>
    public Native.RECT ScreenRect()
    {
        if (!IsValid) return default;
        Native.GetWindowRect(Hwnd, out var r);
        return r;
    }

    public bool ContainsCursor()
    {
        if (!Visible) return false;
        if (!Native.GetCursorPos(out var pt)) return false;
        var r = ScreenRect();
        return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
    }

    public bool IsForeground => IsValid && Native.GetForegroundWindow() == Hwnd;

    public override void Dispose()
    {
        _client.Updated -= OnClientUpdated;
        DisposeFonts();
        Native.KillTimer(Hwnd, (UIntPtr)1);
        base.Dispose();
    }
}