using System.Drawing;
using System.Text;

namespace CuktechTray;

/// <summary>
/// 输入一个整数的小窗口（0.5.4 起给任务栏数字填「自定义左边距」「自定义宽度」用）。
///
/// 为什么单独做一个窗口而不上 WinForms 的 TextBox：整个程序是 NativeAOT，只有 GDI 那套，
/// 键盘输入本来就靠 WinBase 的 WM_KEYDOWN / WM_CHAR，自己画一个输入框比拉一整个控件库省事得多。
/// 样式跟隐私说明窗口、关于窗口同一套（深色渐变、圆角、绿色主按钮）。
///
/// 用法：<c>var v = NumberWindow.Ask("自定义左边距", "…", "dip", 0, 1755, 512);</c>
/// 返回 null 表示用户取消。
/// </summary>
internal sealed class NumberWindow : WinBase
{
    private const int DesignW = 380;
    private const int DesignH = 248;
    private const int PadX = 26;
    private const int TitleY = 20;
    private const int SubY = 54;
    private const int ValueX = 26;
    private const int ValueY = 92;
    private const int ValueH = 58;
    private const int BtnH = 40;
    private const int BtnY = 186;
    private const int MaxDigits = 4;

    private const int HitNone = -1;
    private const int HitOk = 1;
    private const int HitCancel = 2;

    private static readonly Color BgTop = Color.FromArgb(34, 38, 45);
    private static readonly Color BgBottom = Color.FromArgb(20, 23, 28);
    private static readonly Color BorderColor = Color.FromArgb(58, 64, 74);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = Color.FromArgb(178, 186, 196);
    private static readonly Color Faint = Color.FromArgb(110, 117, 126);
    private static readonly Color FieldBg = Color.FromArgb(26, 30, 36);
    private static readonly Color Ok = WattStyle.Ok;

    private readonly string _title;
    private readonly string _sub;
    private readonly string _unit;
    private readonly string _hint;
    private readonly int _min;
    private readonly int _max;
    private readonly int _initial;

    private string _text = "";
    private bool _caretOn = true;
    private bool _answered;
    private int? _result;
    private int _hover = HitNone;
    private int _press = HitNone;

    private GdiFont _fTitle = null!, _fSub = null!, _fValue = null!, _fUnit = null!, _fBtn = null!, _fFoot = null!;
    private float _fontBase;
    private static readonly UIntPtr CaretTimer = new(1);

    private NumberWindow(string title, string sub, string unit, int min, int max, int initial, string hint)
    {
        _title = title;
        _sub = sub;
        _unit = unit;
        _min = min;
        _max = Math.Max(min, max);
        _initial = Math.Clamp(initial, min, Math.Max(min, max));
        _hint = hint ?? ("可以填 " + min + " 到 " + this._max);

        CreateWin(DesignW, DesignH, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, IntPtr.Zero, "number");
        if (Hwnd == IntPtr.Zero)
        {
            LogSink.Write("[number] CreateWin 失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return;
        }
        ApplyScale();
        SetRoundedRegion(S(14));
    }

    /// <summary>弹出来要一个数。取消返回 null。</summary>
    internal static int? Ask(string title, string sub, string unit, int min, int max, int initial, string? hint = null)
    {
        var win = new NumberWindow(title, sub, unit, min, max, initial, hint ?? "");
        if (!win.IsValid) return null;
        try { return win.Run(); }
        finally { win.Dispose(); }
    }

    private int? Run()
    {
        ShowCentered();
        LogSink.Write("[number] 等用户输入 " + _title + " min=" + _min + " max=" + _max + " now=" + _initial);
        while (!_answered && IsValid)
        {
            int r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        LogSink.Write("[number] 结束 result=" + (_result?.ToString() ?? "取消"));
        return _result;
    }

    private void ShowCentered()
    {
        int w = S(DesignW), h = S(DesignH);
        int x = 120, y = 80;
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
        Native.SetTimer(Hwnd, CaretTimer, 500, IntPtr.Zero);
    }

    private int S(float v) => (int)(v * Scale);

    private void ApplyScale()
    {
        DisposeFonts();
        float s = Scale;
        float fb = _fontBase > 0f ? _fontBase : s;
        _fTitle = GdiFont.Create("Microsoft YaHei UI", 14, s, bold: true, fontBase: fb);
        _fSub = GdiFont.Create("Microsoft YaHei UI", 11, s, fontBase: fb);
        _fValue = GdiFont.Create("Microsoft YaHei UI", 26, s, bold: true, fontBase: fb);
        _fUnit = GdiFont.Create("Microsoft YaHei UI", 11, s, fontBase: fb);
        _fBtn = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, bold: true, fontBase: fb);
        _fFoot = GdiFont.Create("Microsoft YaHei UI", 9.5f, s, fontBase: fb);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, S(DesignW), S(DesignH),
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Invalidate();
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fSub?.Dispose(); _fValue?.Dispose();
        _fUnit?.Dispose(); _fBtn?.Dispose(); _fFoot?.Dispose();
    }

    protected override void OnDpiChanged(float scale, Native.RECT suggested)
    {
        ApplyScale();
        SetRoundedRegion(S(14));
    }

    public override void Dispose()
    {
        try { if (IsValid) Native.KillTimer(Hwnd, CaretTimer); } catch { }
        DisposeFonts();
        base.Dispose();
    }

    // ==================== 键盘 ====================

    protected override void OnChar(char c)
    {
        if (c < '0' || c > '9') return;
        if (_text.Length >= MaxDigits) return;
        if (_text == "0") _text = "";
        _text += c;
        _caretOn = true;
        Invalidate();
    }

    protected override void OnKeyDown(int vk)
    {
        switch (vk)
        {
            case 8:  // Backspace
                if (_text.Length > 0) _text = _text.Substring(0, _text.Length - 1);
                _caretOn = true;
                Invalidate();
                break;
            case 46: // Delete：整串清掉
                _text = "";
                Invalidate();
                break;
            case 13: // Enter
                Accept();
                break;
            case 27: // Esc
                Cancel();
                break;
        }
    }

    protected override void OnTimer(int id)
    {
        if (id != 1) return;
        _caretOn = !_caretOn;
        Invalidate();
    }

    // ==================== 鼠标 ====================

    private Rectangle OkRect() => new Rectangle(S(PadX), S(BtnY), S(200), S(BtnH));
    private Rectangle CancelRect() => new Rectangle(S(DesignW - PadX - 116), S(BtnY), S(116), S(BtnH));

    private int HitTest(int x, int y)
    {
        if (OkRect().Contains(x, y)) return HitOk;
        if (CancelRect().Contains(x, y)) return HitCancel;
        return HitNone;
    }

    protected override bool WantsHandCursor(int x, int y) => HitTest(x, y) != HitNone;

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
        if (p == HitNone || h != p) return;
        if (p == HitOk) Accept();
        else Cancel();
    }

    protected override bool OnClose()
    {
        Cancel();
        return false;
    }

    private void Accept()
    {
        if (_answered) return;
        int v = _initial;
        if (_text.Length > 0 && int.TryParse(_text, out int parsed)) v = parsed;
        if (v < _min) v = _min;
        if (v > _max) v = _max;
        _result = v;
        _answered = true;
        if (IsValid) Native.DestroyWindow(Hwnd);
    }

    private void Cancel()
    {
        if (_answered) return;
        _result = null;
        _answered = true;
        if (IsValid) Native.DestroyWindow(Hwnd);
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

    private void Text(Graphics g, string? text, GdiFont font, Color color, int x, int y, int w, int h, TextAnchor anchor = TextAnchor.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        Color c = color.A < 255 ? GdiText.Blend(color, Backdrop(y + h / 2)) : color;
        GdiText.Draw(g, text, font, c, new Rectangle(x, y, w, h), anchor, ellipsis: true);
    }

    private int Measure(Graphics g, string? text, GdiFont font)
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
        FillRoundGradient(g, all, S(14), BgTop, BgBottom);
        StrokeRound(g, all, S(14), BorderColor, 1f);

        Text(g, _title, _fTitle, Fg, S(PadX), S(TitleY), S(DesignW - PadX * 2 - 90), S(28));
        Text(g, _sub, _fSub, Dim, S(PadX), S(SubY), S(DesignW - PadX * 2), S(22));

        // 输入框
        var field = new RectangleF(S(ValueX), S(ValueY), S(DesignW - ValueX * 2), S(ValueH));
        using (var path = RoundRect(field, S(10)))
        {
            using var b = new SolidBrush(FieldBg);
            g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(52, 58, 68), 1f);
            g.DrawPath(pen, path);
        }

        string shown = _text.Length > 0 ? _text : "0";
        Color shownColor = _text.Length > 0 ? Fg : Color.FromArgb(96, 102, 111);
        int numX = S(ValueX + 16);
        int numY = S(ValueY);
        int numH = S(ValueH);
        Text(g, shown, _fValue, shownColor, numX, numY, S(200), numH);

        // 光标：跟在数字后面闪
        if (_caretOn)
        {
            int tw = Measure(g, shown, _fValue);
            int cx = numX + tw + S(2);
            int caretH = S(30);
            using var caret = new Pen(Color.FromArgb(74, 226, 172), Math.Max(1f, S(2)));
            g.DrawLine(caret, cx, numY + (numH - caretH) / 2, cx, numY + (numH + caretH) / 2);
        }

        Text(g, _unit, _fUnit, Faint, S(DesignW - PadX - 74), numY, S(58), numH, TextAnchor.Right);

        Text(g, _hint, _fFoot, Faint, S(PadX), S(158), S(DesignW - PadX * 2), S(18), TextAnchor.Center);

        DrawButton(g, OkRect(), "确定", true, _hover == HitOk);
        DrawButton(g, CancelRect(), "取消", false, _hover == HitCancel);
    }

    private void DrawButton(Graphics g, Rectangle r, string text, bool primary, bool hot)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        using var path = RoundRect(rf, S(10));
        if (primary)
        {
            using var b = new SolidBrush(hot ? Color.FromArgb(74, 226, 172) : Ok);
            g.FillPath(b, path);
            GdiText.Draw(g, text, _fBtn, Color.FromArgb(14, 20, 18), r, TextAnchor.Center, ellipsis: true);
        }
        else
        {
            using var b = new SolidBrush(hot ? Color.FromArgb(38, 44, 54) : Color.FromArgb(34, 39, 47));
            g.FillPath(b, path);
            using var pen = new Pen(BorderColor, 1f);
            g.DrawPath(pen, path);
            GdiText.Draw(g, text, _fBtn, Fg, r, TextAnchor.Center, ellipsis: true);
        }
    }

    // ==================== 诊断渲染 ====================

    internal void ForceScale(float scale, float fontBase = 1.25f)
    {
        if (scale < 0.5f || scale > 4f) return;
        Scale = scale;
        _fontBase = fontBase > 0f ? fontBase : scale;
        ApplyScale();
    }

    /// <summary>自检渲染：造一个窗口、填一个示例数字、画成 PNG（不依赖截图工具）。</summary>
    internal static void RenderSample(string path, string title, string sub, string unit, int min, int max, int sample, string hint, float scale = 1.25f)
    {
        var win = new NumberWindow(title, sub, unit, min, max, sample, hint);
        try
        {
            if (!win.IsValid) return;
            win.ForceScale(scale);
            win._text = sample.ToString();
            win.SaveRender(path);
        }
        finally { win.Dispose(); }
    }

    internal void SaveRender(string path)
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
            LogSink.Write("[number] rendered " + path + " (" + w + "x" + h + ")");
        }
        finally
        {
            Width = ow;
            Height = oh;
        }
    }
}