using System.Drawing;
using System.Text;

namespace CuktechTray;

/// <summary>
/// 第一次打开时的隐私说明（0.4.7 起）。用户原话：「隐私协议是软件第一次打开需要用户同意的那种」。
/// 0.4.6 只是把说明做成向导里的一行小字和一个可以点开的弹窗，那不叫「同意」——这里单独做一扇门：
/// 不点「同意并继续」，程序就不会往下走；点「不同意，退出」直接退出，什么都不写。
///
/// 样式跟扫码向导同一套（深色渐变、圆角、绿色主按钮），因为它就是用户看到的第一屏。
/// 正文按小节排，每行宽度用 GDI 实测再断行，免得换台机器（字体或缩放不同）就冒出横向截断。
/// </summary>
internal sealed class ConsentWindow : WinBase
{
    private const int DesignW = 500;
    private const int DesignH = 546;   // 0.4.7：原来 604 太松，正文下面空出一大块，收紧到刚够
    private const int PadX = 28;
    private const int BtnH = 42;
    private const int BtnY = DesignH - 24 - BtnH;      // 480
    private const int TitleY = 22;
    private const int SubY = 56;
    private const int BodyTop = 92;
    private const int HeadLineH = 22;
    private const int BodyLineH = 20;
    private const int SectionGap = 12;

    private const int HitNone = -1;
    private const int HitAgree = 1;
    private const int HitDecline = 2;

    private static readonly Color BgTop = Color.FromArgb(34, 38, 45);
    private static readonly Color BgBottom = Color.FromArgb(20, 23, 28);
    private static readonly Color BorderColor = Color.FromArgb(58, 64, 74);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = Color.FromArgb(178, 186, 196);
    private static readonly Color Faint = Color.FromArgb(110, 117, 126);
    private static readonly Color Ok = WattStyle.Ok;

    /// <summary>四段正文：跟 README 的「隐私」一节、向导里的隐私弹窗保持同一套说法。</summary>
    private static readonly (string Head, string[] Lines)[] Sections =
    {
        ("一、扫码登录时会做什么", new[]
        {
            "只跟小米云要一张登录二维码，你在手机米家 App 里扫码、确认。",
            "账号、密码、验证码都在你手机上，本程序看不见，也拿不到。",
            "小米云只返回这台充电器的 Mac 和 12 字节 token。",
        }),
        ("二、数据留在哪里", new[]
        {
            "只写进本机的 settings.json：便携版在 exe 同目录，安装版在 %LOCALAPPDATA%\\CuktechTray。",
            "登录会话 session.json 也留在同一目录，只是为了下次重新配置时少扫一次码。",
        }),
        ("三、不会做的事", new[]
        {
            "不上传任何数据。日常读功率走电脑和充电器之间的蓝牙，不经过网络。",
            "启动时会在后台查一次更新（默认开，右键菜单里能关），只拉公开的发布列表。",
        }),
        ("四、想清干净", new[]
        {
            "删掉 settings.json 和 session.json，或者用安装程序卸载，本机就什么都不剩。",
            "蓝牙那一侧不留任何东西，充电器自身也不存电脑的信息。",
        }),
    };

    private readonly Settings _settings;

    private int _hover = HitNone;
    private int _press = HitNone;
    private bool _answered;
    private bool _agreed;

    private GdiFont _fTitle = null!, _fSub = null!, _fHead = null!, _fBody = null!, _fBtn = null!, _fFoot = null!;
    private float _fontBase;

    public ConsentWindow(Settings settings)
    {
        _settings = settings;
        CreateWin(DesignW, DesignH, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, IntPtr.Zero, "consent");
        if (Hwnd == IntPtr.Zero)
        {
            LogSink.Write("[consent] CreateWin 失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return;
        }
        ApplyScale();
        SetRoundedRegion(S(14));
        LogSink.Write("[consent] 建立 ok hwnd=" + Hwnd + " scale=" + Scale.ToString("F2"));
    }

    private int S(float v) => (int)(v * Scale);

    private void ApplyScale()
    {
        DisposeFonts();
        float s = Scale;
        float fb = _fontBase > 0f ? _fontBase : s;
        _fTitle = GdiFont.Create("Microsoft YaHei UI", 14, s, bold: true, fontBase: fb);
        _fSub = GdiFont.Create("Microsoft YaHei UI", 11, s, fontBase: fb);
        _fHead = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, bold: true, fontBase: fb);
        _fBody = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fBtn = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, bold: true, fontBase: fb);
        _fFoot = GdiFont.Create("Microsoft YaHei UI", 9.5f, s, fontBase: fb);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, S(DesignW), S(DesignH),
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Invalidate();
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fSub?.Dispose(); _fHead?.Dispose();
        _fBody?.Dispose(); _fBtn?.Dispose(); _fFoot?.Dispose();
    }

    protected override void OnDpiChanged(float scale, Native.RECT suggested)
    {
        ApplyScale();
        SetRoundedRegion(S(14));
    }

    // ==================== 模态流程 ====================

    /// <summary>
    /// 把窗口摆出来并在这里挡住消息循环，直到用户点了哪一个按钮。
    /// 返回 true 表示同意（可以继续启动），false 表示不同意（调用方退出）。
    /// 窗口都建不出来的时候返回 true：宁可少拦一次，也别让程序起不来。
    /// </summary>
    public bool Ask()
    {
        if (!IsValid) return true;
        ShowCentered();
        LogSink.Write("[consent] 显示隐私说明，等用户点同意");

        while (!_answered && IsValid)
        {
            int r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        LogSink.Write("[consent] 用户" + (_agreed ? "同意" : "不同意") + "，窗口 valid=" + IsValid);
        return _agreed;
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
    }

    private Rectangle AgreeRect() => new Rectangle(S(PadX), S(BtnY), S(236), S(BtnH));
    private Rectangle DeclineRect() => new Rectangle(S(DesignW - PadX - 168), S(BtnY), S(168), S(BtnH));

    private int HitTest(int x, int y)
    {
        if (AgreeRect().Contains(x, y)) return HitAgree;
        if (DeclineRect().Contains(x, y)) return HitDecline;
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
        if (p == HitAgree) Agree();
        else Decline();
    }

    protected override void OnKeyDown(int vk)
    {
        if (vk == 27) Decline();       // Esc = 不同意
        else if (vk == 13) Agree();    // Enter = 同意
    }

    protected override bool OnClose()
    {
        // 直接点右上角/Alt+F4 等于不同意，不能当成「已经同意了」
        LogSink.Write("[consent] WM_CLOSE，按不同意处理");
        Decline();
        return false;
    }

    private void Agree()
    {
        if (_answered) return;
        _settings.PrivacyAccepted = true;
        try { _settings.Save(); }
        catch (Exception ex) { LogSink.Write("[consent] 保存 settings 失败: " + ex.Message); }
        _agreed = true;
        _answered = true;
        if (IsValid) Native.DestroyWindow(Hwnd);
    }

    private void Decline()
    {
        if (_answered) return;
        _agreed = false;
        _answered = true;
        if (IsValid) Native.DestroyWindow(Hwnd);
    }

    public override void Dispose()
    {
        DisposeFonts();
        base.Dispose();
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

    private void Text(Graphics g, string? text, GdiFont font, Color color, int x, int y, int w, int h)
    {
        if (string.IsNullOrEmpty(text)) return;
        Color c = color.A < 255 ? GdiText.Blend(color, Backdrop(y + h / 2)) : color;
        GdiText.Draw(g, text, font, c, new Rectangle(x, y, w, h), TextAnchor.Left, ellipsis: true);
    }

    private void TextC(Graphics g, string? text, GdiFont font, Color color, int x, int y, int w, int h)
    {
        if (string.IsNullOrEmpty(text)) return;
        Color c = color.A < 255 ? GdiText.Blend(color, Backdrop(y + h / 2)) : color;
        GdiText.Draw(g, text, font, c, new Rectangle(x, y, w, h), TextAnchor.Center, ellipsis: true);
    }

    private int Measure(Graphics g, string? text, GdiFont font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        IntPtr hdc = g.GetHdc();
        try { return font.Measure(text, hdc); }
        finally { g.ReleaseHdc(hdc); }
    }

    /// <summary>
    /// 按像素宽度断行：一个一个字量过去，超宽就断开。
    /// 断点优先落在最近的空格上（往回找 40 个字以内），这样路径和英文不会被切一半 ——
    /// 之前 %LOCALAPPDATA%\CuktechTray 就被断成了两截。
    /// </summary>
    private List<string> Wrap(Graphics g, string text, GdiFont font, int maxW)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var cur = new StringBuilder();
        foreach (char ch in text)
        {
            cur.Append(ch);
            if (Measure(g, cur.ToString(), font) <= maxW) continue;

            int cut = cur.Length - 1;
            for (int i = cur.Length - 2; i > 0 && i > cur.Length - 40; i--)
            {
                if (cur[i] != ' ') continue;
                cut = i;
                break;
            }
            string head = cur.ToString(0, cut).TrimEnd();
            string tail = cur.ToString(cut, cur.Length - cut).TrimStart();
            if (head.Length > 0) result.Add(head);
            cur.Clear();
            cur.Append(tail);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    protected override void Paint(Graphics g)
    {
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        var all = new RectangleF(0, 0, w, h);
        FillRoundGradient(g, all, S(14), BgTop, BgBottom);
        StrokeRound(g, all, S(14), BorderColor, 1f);

        Text(g, "隐私说明", _fTitle, Fg, S(PadX), S(TitleY), S(260), S(30));
        Text(g, "第一次打开，先花一分钟看完这段。同意就能用，不同意直接退出。",
            _fSub, Dim, S(PadX), S(SubY), S(DesignW - PadX * 2), S(22));

        int textW = S(DesignW - PadX * 2);
        int y = S(BodyTop);
        Text(g, "这是第三方小工具，不是小米或酷态科官方出的。", _fBody, Faint,
            S(PadX), y, textW, S(BodyLineH));
        y += S(BodyLineH) + S(6);

        foreach (var (head, lines) in Sections)
        {
            Text(g, head, _fHead, Fg, S(PadX), y, textW, S(HeadLineH));
            y += S(HeadLineH);
            foreach (var line in lines)
            {
                foreach (var seg in Wrap(g, "· " + line, _fBody, textW))
                {
                    Text(g, seg, _fBody, Dim, S(PadX), y, textW, S(BodyLineH));
                    y += S(BodyLineH);
                }
            }
            y += S(SectionGap);
        }

        // 按钮上方一行小字：说清楚「不同意」会发生什么，省得用户以为点错了会被记仇
        TextC(g, "点「不同意，退出」不会写任何文件，也不会连蓝牙。", _fFoot, Faint,
            S(PadX), S(BtnY - 26), S(DesignW - PadX * 2), S(18));

        DrawButton(g, AgreeRect(), "同意并继续", true, _hover == HitAgree);
        DrawButton(g, DeclineRect(), "不同意，退出", false, _hover == HitDecline);
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

    /// <summary>把当前一帧画进 PNG（会话内自检用，不依赖截图工具）。</summary>
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
            LogSink.Write("[consent] rendered " + path + " (" + w + "x" + h + ")");
        }
        finally
        {
            Width = ow;
            Height = oh;
        }
    }
}
