using System.Drawing;
using System.Text;

namespace CuktechTray;

/// <summary>
/// 「关于」窗口（0.4.8 起）：版本号、项目主页、致谢，加一个「检查更新」按钮。
///
/// 之前版本信息只在托盘菜单里以一行文字露脸，谁提的 issue、谁带来的 PR、固件实测到哪一版，
/// 用户和帮忙的人都看不到。这扇窗就是把这些话一次说清楚，顺便把「检查更新」从菜单搬进来：
/// 菜单点一下就联网弹框，用户不知道它会连网；放在这里，按钮旁边写着「点才查」，反而更诚实。
///
/// 样式跟隐私说明（ConsentWindow）同一套：深色渐变、圆角、绿色主按钮。它俩都是「偶尔打开、
/// 看完就关」的对话框，观感必须一致，所以配色/画法/模态循环/PNG 导出都是照那份抄的。
/// </summary>
internal sealed class AboutWindow : WinBase
{
    // ---- 设计尺寸（96 DPI 下的逻辑像素，实际按 DPI 缩放）----
    private const int DesignW = 400;
    private const int DesignH = 360;
    private const int PadX = 24;

    private const int TitleY = 20;
    private const int TitleH = 26;
    private const int SubY = 50;
    private const int SubLineH = 18;
    private const int InfoY = 96;
    private const int InfoLineH = 24;
    private const int SepY = 176;          // 信息块与致谢之间那条淡分割线
    private const int ThanksY = 192;
    private const int ThanksHeadH = 18;
    private const int ThanksLineH = 17;
    private const int StatusY = 272;
    private const int StatusH = 18;

    private const int BtnH = 40;
    private const int BtnY = DesignH - PadX - BtnH;   // 296
    private const int BtnW = 110;
    private const int BtnGap = 10;
    private const int CloseW = 80;

    // 轮询后台检查任务用的定时器：200ms 问一次 IsCompleted 就够了，
    // 一次检查最快也要几百毫秒（两次 HTTP 各自的连接超时是 8 秒），再密只是白烧 CPU。
    private const int TimerId = 1;
    private const uint ProbeMs = 200;

    private const int HitNone = -1;
    private const int HitHome = 1;
    private const int HitCheck = 2;
    private const int HitOpen = 3;
    private const int HitClose = 4;

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

    /// <summary>信息块三行：label 在左、value 右对齐。第 0 行整行可点（项目主页）。</summary>
    private static readonly (string Label, string Value)[] InfoRows =
    {
        ("项目主页", "github.com/Blank2007/cuktech-ultra-tray"),
        ("手机 App", "米家"),
        ("实测固件", "2.1.2_0073"),
    };

    /// <summary>
    /// 致谢两段，用真实的 issue / PR 号和人名（编造的名单比不写还难看）。
    /// 第一段拆成「谁报的 + 修了什么」，第二段把三位 PR 作者和各自带来的东西绑在一起，
    /// 这样读的人一眼能对上号，也不用为了凑版面把话写长。
    /// </summary>
    private static readonly string[] ThanksParas =
    {
        "issue #1 的 @KingGxxT：报的 C3 与 A 合并不显示，0.4.7 才修好。",
        "PR #3 的 @a1288852、PR #4 的 @editwe：设备设置、单口采集、面板图钉都是他们带进来的。",
    };

    private enum CheckState { Idle, Checking, Latest, Newer, Failed }

    private CheckState _state = CheckState.Idle;
    private Version? _latest;
    private Task<UpdateCheck.Result>? _check;
    private bool _closed;

    private int _hover = HitNone;
    private int _press = HitNone;

    private GdiFont _fTitle = null!, _fVer = null!, _fSub = null!, _fLabel = null!, _fValue = null!,
                    _fHead = null!, _fBody = null!, _fStatus = null!, _fBtn = null!;
    private float _fontBase;

    public AboutWindow()
    {
        CreateWin(DesignW, DesignH, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, IntPtr.Zero, "about");
        if (Hwnd == IntPtr.Zero)
        {
            LogSink.Write("[about] CreateWin 失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return;
        }
        // 构造函数里刻意什么都不查：没点过「检查更新」就不许发网络请求，
        // 这是 ConsentWindow 里写给用户的那句承诺（「只有你点检查更新时才访问 GitHub 一次」）。
        ApplyScale();
        LogSink.Write("[about] 建立 ok hwnd=" + Hwnd + " scale=" + Scale.ToString("F2"));
    }

    // 截断而不是四舍五入：一堆横坐标是按间距累加出来的，四舍五入会让右边几行各差 1px
    private int S(float v) => (int)(v * Scale);

    /// <summary>窗口圆角半径（像素）。绘制和 SetWindowRgn 必须用同一个整数值，
    /// 否则 region 与抗锯齿圆角差半个像素，四个角会混出一丝暗边。</summary>
    private int RadiusPx => S(14);

    private void ApplyScale()
    {
        DisposeFonts();
        float s = Scale;
        float fb = _fontBase > 0f ? _fontBase : s;
        _fTitle = GdiFont.Create("Microsoft YaHei UI", 15, s, bold: true, fontBase: fb);
        _fVer = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fSub = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fLabel = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fValue = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fHead = GdiFont.Create("Microsoft YaHei UI", 11, s, bold: true, fontBase: fb);
        _fBody = GdiFont.Create("Microsoft YaHei UI", 10, s, fontBase: fb);
        _fStatus = GdiFont.Create("Microsoft YaHei UI", 10, s, fontBase: fb);
        _fBtn = GdiFont.Create("Microsoft YaHei UI", 11, s, bold: true, fontBase: fb);
        if (!IsValid) return;

        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, S(DesignW), S(DesignH),
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        // 圆角是 SetWindowRgn 裁出来的，不是画出来的：双缓冲走屏幕兼容 DDB（没有 alpha 通道），
        // 不裁的话矩形窗口的四个角会露出底色，看着像「圆角外面补了四个黑角」。
        SetRoundedRegion(RadiusPx);
        Invalidate();
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fVer?.Dispose(); _fSub?.Dispose(); _fLabel?.Dispose();
        _fValue?.Dispose(); _fHead?.Dispose(); _fBody?.Dispose(); _fStatus?.Dispose();
        _fBtn?.Dispose();
    }

    protected override void OnDpiChanged(float scale, Native.RECT suggested) => ApplyScale();

    // ==================== 模态流程 ====================

    /// <summary>
    /// 摆出来并在这里挡住消息循环，直到窗口被关掉。调用方（菜单）在这之后才继续，
    /// 跟 ConsentWindow.Ask 同一套做法：窗口是模态的，但不是 WinForms 的 ShowDialog。
    /// </summary>
    public void ShowModal()
    {
        if (!IsValid) return;
        ShowCentered();
        LogSink.Write("[about] 显示关于窗口");

        while (!_closed && IsValid)
        {
            int r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        // 关窗时可能还有一次检查在飞：定时器在这里再补一刀（OnClose 里已经停过一次），
        // 免得残留的 WM_TIMER 打进已经没人处理的消息队列。
        KillCheckTimer();
        LogSink.Write("[about] 已关闭 valid=" + IsValid);
        Dispose();
    }

    /// <summary>居中到鼠标所在那块显示器的工作区（多屏时别跑到另一块屏上）。</summary>
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

    // ==================== 可点区域（设计像素）====================

    private Rectangle HomeRect() => new(S(PadX), S(InfoY), S(DesignW - PadX * 2), S(InfoLineH));
    private Rectangle CheckRect() => new(S(PadX), S(BtnY), S(BtnW), S(BtnH));
    private Rectangle OpenRect() => new(S(PadX + BtnW + BtnGap), S(BtnY), S(BtnW), S(BtnH));
    private Rectangle CloseRect() => new(S(DesignW - PadX - CloseW), S(BtnY), S(CloseW), S(BtnH));

    private int HitTest(int x, int y)
    {
        if (HomeRect().Contains(x, y)) return HitHome;
        if (CheckRect().Contains(x, y)) return HitCheck;
        if (OpenRect().Contains(x, y)) return HitOpen;
        if (CloseRect().Contains(x, y)) return HitClose;
        return HitNone;
    }

    private bool Busy => _state == CheckState.Checking;

    protected override bool WantsHandCursor(int x, int y)
    {
        int h = HitTest(x, y);
        if (h == HitNone) return false;
        // 检查中的主按钮是灰的、按下去也不动，就别给手型：
        // 光标说「能点」而点击没反应，比直接看到禁用色更让人以为程序卡住了。
        return !(h == HitCheck && Busy);
    }

    protected override void OnMouseMove(int x, int y, bool entering)
    {
        int h = HitTest(x, y);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave()
    {
        if (_hover != HitNone) { _hover = HitNone; Invalidate(); }
    }

    protected override void OnLeftDown(int x, int y)
    {
        int h = HitTest(x, y);
        _press = (h == HitCheck && Busy) ? HitNone : h;
    }

    protected override void OnLeftUp(int x, int y)
    {
        int h = HitTest(x, y);
        int p = _press;
        _press = HitNone;
        if (p == HitNone || h != p) return;     // 按下之后滑走了就不算点击
        switch (p)
        {
            case HitHome: OpenUrl(UpdateCheck.RepoUrl); break;
            case HitCheck: StartCheck(); break;
            case HitOpen: OpenUrl(UpdateCheck.RepoUrl + "/releases"); break;
            case HitClose: CloseSelf(); break;
        }
    }

    protected override void OnKeyDown(int vk)
    {
        if (vk == 27) CloseSelf();      // Esc = 关闭（对话框唯一的约定俗成，别的键都不管）
    }

    protected override bool OnClose()
    {
        // 走默认销毁路径，只把「循环该退了」这件事记下来；定时器一起停掉
        _closed = true;
        KillCheckTimer();
        return true;
    }

    protected override void OnDestroy() => KillCheckTimer();

    private void CloseSelf()
    {
        if (_closed) return;
        _closed = true;
        KillCheckTimer();
        if (IsValid) Native.DestroyWindow(Hwnd);
    }

    public override void Dispose()
    {
        KillCheckTimer();
        DisposeFonts();
        base.Dispose();
    }

    private void OpenUrl(string url)
    {
        LogSink.Write("[about] 打开 " + url);
        Native.ShellExecuteW(Hwnd, "open", url, null, null, 5);   // 5 = SW_SHOW
    }

    // ==================== 检查更新 ====================

    private void StartCheck()
    {
        if (Busy || _check is not null) return;

        // 绝不能在这条消息循环所在的线程上等 RunAsync：它内部「先代理后直连」，
        // 每次连接超时 8 秒、请求超时 12 秒，同步等就是整扇窗白掉好几十秒。
        // 丢给线程池，用 WM_TIMER 回来问 IsCompleted —— 后台线程一个字都不画。
        _state = CheckState.Checking;
        _latest = null;
        _check = Task.Run(() => UpdateCheck.RunAsync());
        Native.SetTimer(Hwnd, (UIntPtr)TimerId, ProbeMs, IntPtr.Zero);
        Invalidate();
    }

    protected override void OnTimer(int id)
    {
        if (id != TimerId) return;
        var task = _check;
        if (task is null) { KillCheckTimer(); return; }
        if (!task.IsCompleted) return;

        _check = null;
        KillCheckTimer();
        FinishCheck(task);
    }

    private void FinishCheck(Task<UpdateCheck.Result> task)
    {
        UpdateCheck.Result res;
        try
        {
            res = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // RunAsync 自己把异常都包成 Ok=false 了，走到这里只可能是 Task 层面的意外
            LogSink.Write("[about] 检查更新失败：" + ex.Message);
            _state = CheckState.Failed;
            _latest = null;
            Invalidate();
            return;
        }

        if (!res.Ok || res.Latest is null)
        {
            LogSink.Write("[about] 检查更新失败：" + (res.Ok ? "返回里没有版本号" : res.Error));
            _state = CheckState.Failed;
            _latest = null;
        }
        else if (res.Latest is Version latest && latest > UpdateCheck.Current)
        {
            _latest = latest;
            _state = CheckState.Newer;
            LogSink.Write("[about] 有新版本 " + latest + "（当前 " + UpdateCheck.Current + "）via=" + res.Via);
        }
        else
        {
            _latest = res.Latest;
            _state = CheckState.Latest;
            LogSink.Write("[about] 已是最新 " + res.Latest + "（当前 " + UpdateCheck.Current + "）");
        }
        Invalidate();
    }

    private void KillCheckTimer()
    {
        try { if (IsValid) Native.KillTimer(Hwnd, (UIntPtr)TimerId); } catch { }
    }

    // ==================== 绘制 ====================

    private string StatusText => _state switch
    {
        CheckState.Checking => "正在检查…",
        CheckState.Latest => "已经是最新的（v" + UpdateCheck.Current + "）",
        CheckState.Newer => "看到 v" + _latest + " 了，去下载页拿",
        CheckState.Failed => "没连上 GitHub，稍后再试",
        _ => "想看看有没有新版，点右边的按钮",
    };

    private Color StatusColor => _state switch
    {
        CheckState.Checking => Warn,
        CheckState.Latest => Ok,
        CheckState.Newer => Warn,
        CheckState.Failed => Bad,
        _ => Faint,
    };

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
        var rect = new Rectangle(x, y, w, h);
        Color c = color.A < 255 ? GdiText.Blend(color, Backdrop(rect.Y + rect.Height / 2)) : color;
        GdiText.Draw(g, text, font, c, rect, anchor, ellipsis: true);
    }

    private int Measure(Graphics g, string? text, GdiFont font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        IntPtr hdc = g.GetHdc();
        try { return font.Measure(text, hdc); }
        finally { g.ReleaseHdc(hdc); }
    }

    /// <summary>
    /// 按像素宽度断行：一个字一个字量过去，超宽就断开，断点尽量落在空格或「、」上。
    /// 「、」也算断点是因为致谢里人名和 PR 号靠顿号串联（@a1288852、@editwe），
    /// 只认空格的话会从「PR #4 的」后面硬断，第二行开头孤零零一个 @editwe。
    /// 致谢里有 @ 开头的人名，换台机器字体或缩放不同宽度就会变，写死换行在别的 DPI 下
    /// 会溢出或者空出一截，所以交给实测。
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
                if (cur[i] != ' ' && cur[i] != '、') continue;
                cut = i + (cur[i] == '、' ? 1 : 0);   // 顿号留在上一行行尾，不放到下一行开头
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
        FillRoundGradient(g, all, RadiusPx, BgTop, BgBottom);
        StrokeRound(g, all, RadiusPx, BorderColor, 1f);

        int textW = S(DesignW - PadX * 2);

        // 标题行：左边名字，右边版本号。两段共用一个矩形，一个左对齐一个右对齐，
        // 不用去算各自的宽度，字体换了也不会对不齐。
        Text(g, "酷态科托盘功率表", _fTitle, Fg, S(PadX), S(TitleY), textW, S(TitleH));
        Text(g, "v" + UpdateCheck.Current, _fVer, Faint, S(PadX), S(TitleY), textW, S(TitleH),
            TextAnchor.Right);

        // 副标题两句分开写死：这是全窗唯一「断在哪有意义」的文案（「不是官方的」得自成一行）
        Text(g, "酷态科 10 号 Ultra 的托盘功率表，第三方小工具，", _fSub, Faint,
            S(PadX), S(SubY), textW, S(SubLineH));
        Text(g, "不是小米或酷态科官方出的。", _fSub, Faint,
            S(PadX), S(SubY) + S(SubLineH), textW, S(SubLineH));

        // 信息块：label 左、value 右；第 0 行（项目主页）整行可点，hover 时 value 换 Ok 色
        for (int i = 0; i < InfoRows.Length; i++)
        {
            int y = S(InfoY) + S(InfoLineH) * i;
            var (label, value) = InfoRows[i];
            Text(g, label, _fLabel, Faint, S(PadX), y, textW, S(InfoLineH));
            bool hot = i == 0 && _hover == HitHome;
            Text(g, value, _fValue, hot ? Ok : Fg, S(PadX), y, textW, S(InfoLineH), TextAnchor.Right);
        }

        // 一条很淡的分割线：深色底上光靠留白，信息块和致谢会糊成一片
        using (var pen = new Pen(SepColor, 1f))
            g.DrawLine(pen, S(PadX), S(SepY), S(DesignW - PadX), S(SepY));

        // 致谢
        int ty = S(ThanksY);
        int step = Math.Max(1, S(ThanksLineH));
        Text(g, "谢谢这些朋友", _fHead, Fg, S(PadX), ty, textW, S(ThanksHeadH));
        ty += S(ThanksHeadH);

        var lines = new List<string>();
        foreach (string para in ThanksParas) lines.AddRange(Wrap(g, para, _fBody, textW));

        // 只放得下三行（状态行在 272，再往下就压上去了）。真挤不下时最后一行补省略号，
        // 宁可少说一句，也不能让文字盖到状态行上。
        int maxLines = Math.Min(3, Math.Max(1, (S(StatusY) - ty) / step));
        for (int i = 0; i < lines.Count && i < maxLines; i++)
        {
            string line = (i == maxLines - 1 && lines.Count > maxLines) ? lines[i] + "…" : lines[i];
            Text(g, line, _fBody, Dim, S(PadX), ty, textW, step);
            ty += step;
        }

        Text(g, StatusText, _fStatus, StatusColor, S(PadX), S(StatusY), textW, S(StatusH));

        DrawButton(g, CheckRect(), Busy ? "检查中…" : "检查更新", true, _hover == HitCheck && !Busy);
        DrawButton(g, OpenRect(), "打开下载页", false, _hover == HitOpen);
        DrawButton(g, CloseRect(), "关闭", false, _hover == HitClose);
    }

    private void DrawButton(Graphics g, Rectangle r, string text, bool primary, bool hot)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        using var path = RoundRect(rf, S(10));
        if (primary)
        {
            if (Busy)
            {
                // 检查中：按钮留着位置但按下去没反应，底色压暗、字换 Faint，
                // 一眼看出是「在忙」而不是「坏了」
                using (var b = new SolidBrush(SepColor)) g.FillPath(b, path);
                GdiText.Draw(g, text, _fBtn, Faint, r, TextAnchor.Center, ellipsis: true);
                return;
            }
            using (var b = new SolidBrush(hot ? Color.FromArgb(74, 226, 172) : Ok)) g.FillPath(b, path);
            GdiText.Draw(g, text, _fBtn, Color.FromArgb(14, 20, 18), r, TextAnchor.Center, ellipsis: true);
        }
        else
        {
            using (var b = new SolidBrush(hot ? Color.FromArgb(38, 44, 54) : Color.FromArgb(34, 39, 47)))
                g.FillPath(b, path);
            using (var pen = new Pen(BorderColor, 1f)) g.DrawPath(pen, path);
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

    /// <summary>把当前一帧画进 PNG（会话内自检用，不依赖截图工具）。四角是靠 alpha 抠出来的，
    /// 因为窗口本身是 SetWindowRgn 裁的不透明窗口，直接 BitBlt 抓图会连黑角一起带上。</summary>
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
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllBytes(path, ms.ToArray());
            LogSink.Write("[about] rendered " + path + " (" + w + "x" + h + ")");
        }
        finally
        {
            Width = ow;
            Height = oh;
        }
    }
}
