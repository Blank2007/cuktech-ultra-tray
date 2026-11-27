namespace CuktechTray;

internal static class Program
{
    private const string MutexName = @"Local\CuktechTray.SingleInstance";
    private const string ShowEventName = @"Local\CuktechTray.ShowPanel";

    // 单实例互斥体要一直活着：进程退出时系统才释放它
    private static Mutex? _single;

    [STAThread]
    private static int Main(string[] args)
    {
        LogSink.Write("[start] v" + typeof(Program).Assembly.GetName().Version + " aot");
        try
        {
            return Real(args);
        }
        catch (Exception ex)
        {
            LogSink.Write("[fatal] " + ex);
            return 9;
        }
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>自查图的落地目录。<c>--out=&lt;目录&gt;</c> 优先（CI 和 tools/check-ui.ps1 用它往临时目录里写）；
    /// 否则从 exe 往上找仓库根（有 CHANGELOG.md 的那层）再进 docs —— exe 在 bin\Release\... 下也能找到。
    /// 都找不到才退到 %LOCALAPPDATA%\CuktechTray\probe，免得往只读目录里写。</summary>
    private static string OutDir(string[] args)
    {
        foreach (var a in args)
        {
            if (!a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = a.Substring("--out=".Length).Trim('"');
            if (dir.Length == 0) continue;
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
        var probe = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(probe); i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(probe, "CHANGELOG.md")))
            {
                var docs = System.IO.Path.Combine(probe, "docs");
                System.IO.Directory.CreateDirectory(docs);
                return docs;
            }
            probe = System.IO.Path.GetDirectoryName(probe.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? "";
        }
        var fallback = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CuktechTray", "probe");
        System.IO.Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string OutFile(string[] args, string name) =>
        System.IO.Path.Combine(OutDir(args), name);

    private static int Real(string[] args)
    {
        // 安装 / 卸载是一次性动作：不进消息循环、不抢单实例，跑完直接返回
        // （绿色版和安装版共用这一个 exe，安装包就是程序自己）
        int? cli = Installer.RunCli(args);
        if (cli.HasValue) return cli.Value;

        // 打包时用来自查 GitHub 上的最新版本（平时走托盘菜单里那一项）
        if (Has(args, "--check-update"))
        {
            var r = UpdateCheck.RunAsync().GetAwaiter().GetResult();
            LogSink.Write("[update] current=" + UpdateCheck.Current
                + (r.Ok ? " latest=" + r.Latest + " via=" + r.Via : " error=" + r.Error));
            return r.Ok ? 0 : 1;
        }

        // --standalone：诊断用（--render / 各种 *-probe），不参与单实例检查。
        // 之所以要它：真机上正经装着的那份正在跑时会占着单实例互斥体，
        // 从开发目录再启动一个做渲染会直接「让位退出」，连日志都不留。
        // 正常启动绝不要加：它会让两个实例同时抢蓝牙。
        if (!Has(args, "--standalone"))
        {
            _single = new Mutex(initiallyOwned: true, MutexName, out bool isNew);
            if (!isNew)
            {
                // 已经在跑了：让那个实例把面板弹出来，自己安静退出
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                    ev.Set();
                }
                catch { }
                return 0;
            }
        }

        // 诊断模式连这个也不建：真机那份在跑时，低权限进程连同名事件都打不开
        EventWaitHandle? showEvent = Has(args, "--standalone")
            ? null
            : new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        // 上一次更新留下的 .download / .old 顺手清掉（有残留就说明刚更新过）
        Updater.CleanupStale();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogSink.Write("[crash] " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogSink.Write("[task-crash] " + e.Exception);
            e.SetObserved();
        };

        var settings = Settings.Load();
        if (Has(args, "--pin"))
        {
            settings.PanelPinned = true;
            settings.Save();
        }

        // 回归自测：连上 -> 认证 -> 读四口；退出码 0/1，明细走 logs\app.log
        // （跟 WinForms 版同一份 SelfTest.cs，省得两边各写一套判断）
        if (Has(args, "--selftest"))
        {
            LogSink.Write("[selftest] start mac=" + settings.Mac + " tokenLen=" + settings.TokenHex.Length);
            // 先跑 AOT 专项自查（手写 JSON 转义、设置源生成序列化），再连设备
            bool aotOk = AotChecks.RunAll();
            bool ok = SelfTest.RunAsync(settings).GetAwaiter().GetResult();
            LogSink.Write("[selftest] aot=" + (aotOk ? "ok" : "failed"));
            LogSink.Write("[selftest] " + (ok ? "ok" : "failed"));
            return ok && aotOk ? 0 : 1;
        }

        var ui = new UiDispatcher();

        // 无设备时看排版：三页各渲一张 PNG，方便跟旧版逐像素对比
        if (Has(args, "--render"))
        {
            var probeClient = new DeviceClient(settings, ui);
            var probe = new PanelWindow(probeClient, settings, ui);
            if (probe.Handle == IntPtr.Zero) return 2;
            ui.Attach(probe.Handle, Native.WM_APP_POST);
            // --render-demo：塞一份假数据，好在没接充电器时也能看「已连接」的排版
            bool demo = Has(args, "--render-demo");
            // --render-merged：C3 与 A 合并成一路的假数据（issue #1 那种情形），看合并显示对不对
            bool mergedDemo = demo && Has(args, "--render-merged");
            // --render-scale=2.5：强制按这个缩放渲染，好跟旧版 800x900 的真屏截图逐像素比较
            float renderScale = 0f;
            foreach (var a in args)
            {
                if (!a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) renderScale = v;
            }
            if (renderScale > 0f) probe.ForceScale(renderScale);
            if (Has(args, "--render-pinned")) probe.RestorePinned(true);
            string sizeTag = renderScale > 0f ? "-x" + renderScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "") : "";
            // 曲线页要有点才画得出来：演示模式塞 180 个假点（不写盘，只活在内存里）
            if (demo) PowerHistory.SeedDemo();
            foreach (var (tab, name) in new[]
            {
                (PanelWindow.Tab.Read, "read"),
                (PanelWindow.Tab.Control, "control"),
                (PanelWindow.Tab.Mode, "mode"),
                (PanelWindow.Tab.Chart, "chart"),
            })
            {
                probe.SetTab(tab);
                if (demo) probe.SetDemoSnapshot(DemoSnapshot(tab, mergedDemo));
                probe.SaveRender(OutFile(args, "tmp-" + (mergedDemo ? "demo-merged-" : demo ? "demo-" : "aot-") + name + sizeTag + ".png"));
            }
            LogSink.Write("[probe] rendered" + (demo ? " demo" : ""));
            return 0;
        }

        // 图标自查：把「已连接（绿）/ 未连接（灰）」两枚托盘图标按指定边长放大排一张，
        // 给文档和视频用。画的是 WattIcon 本身，只是边长从 16px 换成 256px。
        if (Has(args, "--render-icons"))
        {
            const int box = 256;
            const int gap = 96;
            using var canvas = new System.Drawing.Bitmap(box * 2 + gap, box,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                g.Clear(System.Drawing.Color.Transparent);
                for (int i = 0; i < 2; i++)
                {
                    var accent = i == 0
                        ? System.Drawing.Color.FromArgb(26, 138, 86)    // 已连接
                        : System.Drawing.Color.FromArgb(94, 99, 106);  // 未连接
                    using var one = WattIcon.CreateBitmap(box, accent);
                    g.DrawImage(one, new System.Drawing.Rectangle(i * (box + gap), 0, box, box));
                }
            }
            canvas.Save(OutFile(args, "shot-tray-icons.png"),
                System.Drawing.Imaging.ImageFormat.Png);
            LogSink.Write("[probe] rendered icons");
            return 0;
        }

        // 任务栏数字自查：低完整性环境里 SetParent 挂不上任务栏，抓不到真机图。
        // 这里用真机同一套字体（Segoe UI Bold 11pt × 缩放）和配色（WattStyle.Total）
        // 把那一小块（84 dip 宽 × 任务栏高）画成透明 PNG，给文档配图用。
        if (Has(args, "--render-taskbar"))
        {
            float ts = 1.25f;
            int th = 60;
            foreach (var a in args)
            {
                if (a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float tsv)) ts = tsv;
                if (a.StartsWith("--render-height=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(a.Substring("--render-height=".Length), out int thv)) th = Math.Max(16, thv);
            }
            int tw = (int)Math.Round(84 * ts);
            double watts = 6.0;
            string text = WattStyle.Format(watts) + "W";
            var color = WattStyle.Total(watts);
            using var tbmp = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(tbmp))
            using (var font = new System.Drawing.Font("Segoe UI", (float)(11.0 * ts),
                       System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point))
            using (var brush = new System.Drawing.SolidBrush(color))
            using (var fmt = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
                FormatFlags = System.Drawing.StringFormatFlags.NoWrap,
            })
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(text, font, brush, new System.Drawing.RectangleF(0, 0, tw, th), fmt);
            }
            tbmp.Save(OutFile(args, "tmp-taskbar-digit.png"),
                System.Drawing.Imaging.ImageFormat.Png);
            LogSink.Write("[probe] rendered taskbar digit " + text + " " + tw + "x" + th);
            return 0;
        }

        // 向导排版自查：不联网也能看到三个阶段长什么样（二维码那一页只画占位框）
        if (Has(args, "--render-wizard"))
        {
            float wizScale = 1.25f;
            foreach (var a in args)
            {
                if (!a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) wizScale = v;
            }
            var wiz = new WizardWindow(settings, ui);
            if (!wiz.IsValid) { wiz.Dispose(); return 3; }
            wiz.ForceScale(wizScale);
            wiz.DemoRender(0, OutFile(args, "tmp-wizard-welcome.png"));
            wiz.DemoRender(1, OutFile(args, "tmp-wizard-scan.png"));
            wiz.DemoRender(2, OutFile(args, "tmp-wizard-pick.png"));
            wiz.Dispose();
            LogSink.Write("[probe] rendered wizard");
            return 0;
        }

        // 首次启动的隐私说明自查：不弹窗，直接把这一屏画成 PNG 看排版
        if (Has(args, "--render-consent"))
        {
            float cs = 1.25f;
            foreach (var a in args)
            {
                if (!a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) cs = v;
            }
            var consent = new ConsentWindow(settings);
            if (!consent.IsValid) { consent.Dispose(); return 3; }
            consent.ForceScale(cs);
            consent.SaveRender(OutFile(args, "tmp-consent.png"));
            consent.Dispose();
            LogSink.Write("[probe] rendered consent");
            return 0;
        }

        // 数值输入窗自查（0.5.4）：任务栏的「自定义左边距 / 自定义宽度」都弹它
        if (Has(args, "--render-number"))
        {
            float ns = 1.25f;
            foreach (var a in args)
            {
                if (!a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) ns = v;
            }
            NumberWindow.RenderSample(OutFile(args, "tmp-number.png"),
                "自定义左边距",
                "从任务栏最左边算起（拖过就记成这个值）",
                "dip", 0, 1900, 512,
                "可以填 0 到 1900",
                ns);
            LogSink.Write("[probe] rendered number");
            return 0;
        }

        // 设备设置窗口排版自查（0.5.0）：不连蓝牙，用 Demo 数据把这一屏画成 PNG
        if (Has(args, "--render-settings"))
        {
            float ss = 1.25f;
            foreach (var a in args)
            {
                if (!a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) ss = v;
            }
            var sclient = new DeviceClient(settings, null);
            using var dwin = new DeviceSettingsWindow(sclient);
            if (!dwin.IsValid) return 3;
            dwin.ForceScale(ss);
            dwin.UseDemoData();
            dwin.SaveRender(OutFile(args, "tmp-settings.png"));
            LogSink.Write("[probe] rendered settings");
            return 0;
        }

        // 设备设置窗口命中矩形自查（0.5.3）：缩放固定 1，导出每个热区的 x/y/w/h
        if (Has(args, "--probe-hits"))
        {
            var hclient = new DeviceClient(settings, null);
            using var hwin = new DeviceSettingsWindow(hclient);
            if (!hwin.IsValid) return 3;
            hwin.ForceScale(1f);
            hwin.UseDemoData();
            hwin.WriteHitProbe(OutFile(args, "tmp-hits.txt"));
            LogSink.Write("[probe] hit rects written");
            return 0;
        }

        // 任务栏数字摆位自查（0.5.4，issue #7）：PlaceX 是纯函数，跟字体无关，直接比表。
        // 五个「机器」场景 × 四档位置 × 四档宽度，看每种组合算出来的左边距。
        if (Has(args, "--probe-taskbar"))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("tbW\tscale\tplace\twidthDip\tw\tgap\tstartLeft\ttrayLeft\tcustomDip\tx");

            var scenes = new (int TbW, double Scale, int StartLeft, int StartRight, int TrayLeft)[]
            {
                (2560, 1.25, 583, 628, 1785),
                (1920, 1.00, 437, 471, 1338),
                (1920, 1.50, 656, 707, 1200),
                (3840, 2.00, 875, 942, 2678),
                (1920, 1.00, -1, -1, 1338),
            };

            var places = new[] { TaskbarPlace.Auto, TaskbarPlace.Center, TaskbarPlace.Right, TaskbarPlace.Custom };
            var widths = new[] { 56, 84, 104, 160 };
            var customs = new[] { 0.0, 512.0, 9999.0 };

            foreach (var sc in scenes)
            {
                int gap = (int)Math.Round(8 * sc.Scale);
                foreach (var place in places)
                {
                    foreach (int wdip in widths)
                    {
                        int w = (int)Math.Round(wdip * sc.Scale);
                        foreach (double cdip in customs)
                        {
                            if (place != TaskbarPlace.Custom && cdip != 0.0) continue;
                            int x = TaskbarWindow.PlaceX(place, sc.TbW, w, gap, sc.StartLeft, sc.StartRight,
                                                         sc.TrayLeft, cdip, sc.Scale);
                            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                                "{0}\t{1:0.00}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8:0.0}\t{9}\n",
                                sc.TbW, sc.Scale, TaskbarWindow.PlaceKey(place), wdip, w, gap, sc.StartLeft,
                                sc.TrayLeft, cdip, x);
                        }
                    }
                }
            }

            File.WriteAllText(OutFile(args, "tmp-taskbar.txt"), sb.ToString());
            LogSink.Write("[probe] taskbar placement table written");
            return 0;
        }

        // 单口采集窗口排版自查（0.5.0）：完成态用真 CaptureAccumulator 喂 250 个假采样点
        if (Has(args, "--render-capture"))
        {
            float cs2 = 1.25f;
            foreach (var a in args)
            {
                if (!a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) cs2 = v;
            }
            var cclient = new DeviceClient(settings, null);
            using var cwin = new CaptureWindow(cclient);
            if (!cwin.IsValid) return 3;
            cwin.ForceScale(cs2);
            cwin.UseDemoData();
            cwin.SaveRender(OutFile(args, "tmp-capture.png"));
            LogSink.Write("[probe] rendered capture");
            return 0;
        }

        // 单口采集 CSV 自查（0.5.x）：把演示数据的逐秒样本写成 CSV，核对表头与行数
        if (Has(args, "--probe-capture-csv"))
        {
            var xclient = new DeviceClient(settings, null);
            using var xwin = new CaptureWindow(xclient);
            if (!xwin.IsValid) return 3;
            xwin.ForceScale(1f);
            xwin.UseDemoData();
            xwin.WriteCsvProbe(OutFile(args, "tmp-capture.csv"));
            LogSink.Write("[probe] capture csv written");
            return 0;
        }

        // 关于窗口排版自查（0.5.0）：不发任何网络请求
        if (Has(args, "--render-about"))
        {
            float as2 = 1.25f;
            foreach (var a in args)
            {
                if (!a.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a.Substring("--render-scale=".Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) as2 = v;
            }
            using var awin = new AboutWindow();
            if (!awin.IsValid) return 3;
            awin.ForceScale(as2);
            awin.SaveRender(OutFile(args, "tmp-about.png"));
            LogSink.Write("[probe] rendered about");
            return 0;
        }

        // 图标字体自查：CreateFontW 找不到字体不会报错，只会静默回退，所以这里把真实字体名读回来
        if (Has(args, "--font-probe")) return DiagFonts();

        // 文字渲染参数扫描：同一段标题用不同的字体质量/字重/字号各画一行，
        // 好拿它跟 0.3.3 真屏截图里的墨量、彩边量逐行比对（脚本：.tmp-inkrows.py）
        if (Has(args, "--ink-probe")) return DiagInk();

        // 半透明混合自查：同样的 alpha 白填充，在屏幕兼容 DDB 与 GDI+ 位图上各画一遍，
        // 看最终像素差多少（页签胶囊底比旧版亮，怀疑就是这里的差异）
        if (Has(args, "--alpha-probe")) return DiagAlpha();

        // 第一次打开：先把隐私说明摆出来，点「同意并继续」才往下走（用户明确要的门）。
        // 上面那些 --render-* / --selftest / --check-update 早就 return 了，不会被这扇门挡住。
        if (!settings.PrivacyAccepted)
        {
            var consent = new ConsentWindow(settings);
            bool agreed = consent.Ask();
            consent.Dispose();
            if (!agreed)
            {
                LogSink.Write("[start] 用户不同意隐私说明，退出");
                return 0;
            }
        }

        // 没配对过：TrayApp 起来 800ms 后自己弹扫码向导（这里只留一行日志）
        if (!settings.IsComplete) LogSink.Write("[start] 未配置：等向导配对");

        var showOnStart = Has(args, "--panel") ? ShowOnStart.Center
            : Has(args, "--tray-only") ? ShowOnStart.None
            : ShowOnStart.Corner;
        bool openControl = Has(args, "--control");

        var app = new TrayApp(settings, showOnStart, ui, openControl);

        // 用户又双击了一次 exe：把面板弹出来
        var waiter = new Thread(() =>
        {
            while (showEvent is not null)
            {
                try
                {
                    if (!showEvent.WaitOne()) return;
                }
                catch (Exception ex)
                {
                    LogSink.Write("[show] 等待信号失败: " + ex.Message);
                    return;
                }
                app.RequestShow();
            }
        })
        { IsBackground = true, Name = "show-waiter" };
        waiter.Start();

        if (Has(args, "--menu-demo")) app.DemoMenu();
        if (Has(args, "--menu-demo-taskbar")) app.DemoMenuTaskbar();

        // 直接把扫码向导弹出来（不走托盘菜单、不看配置是否完整）：
        // --wizard-demo 是自查用，--setup / --wizard 是兼容 0.3.3 的老参数习惯。
        if (Has(args, "--wizard-demo") || Has(args, "--setup") || Has(args, "--wizard")) app.ShowWizard();

        RunLoop();
        LogSink.Write("[exit] 消息循环结束");
        return 0;
    }

    /// <summary>--font-probe：把图标字体真正解析到的字体名打进日志，并渲染一张字形对照图。</summary>
    private static int DiagFonts()
    {
        string[] families = { "Segoe MDL2 Assets", "Segoe Fluent Icons", "Segoe UI Symbol", "Segoe UI" };
        const string glyphs = "\uE718 \uE77A \uE70F \uE713 \uE721";
        const int rowH = 64;
        using var bmp = new System.Drawing.Bitmap(760, rowH * families.Length);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(34, 38, 45));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            for (int i = 0; i < families.Length; i++)
            {
                using var font = GdiFont.Create(families[i], 26, 1);
                IntPtr hdc = g.GetHdc();
                string got;
                try
                {
                    IntPtr old = Native.SelectObject(hdc, font.Handle);
                    var face = new char[64];
                    int n = Native.GetTextFaceW(hdc, 64, face);
                    got = n > 0 ? new string(face, 0, n) : "(unknown)";
                    Native.SelectObject(hdc, old);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
                LogSink.Write("[font-probe] want=" + families[i] + " got=" + got + " px=" + font.PixelHeight);
                GdiText.Draw(g, families[i] + "  " + glyphs, font, System.Drawing.Color.White,
                    new System.Drawing.Rectangle(12, i * rowH + 10, 736, rowH - 16));
            }
        }
        bmp.Save(@"D:\dsh projects\cuktech-ultra-tray\docs\tmp-glyph.png", System.Drawing.Imaging.ImageFormat.Png);
        return 0;
    }

    /// <summary>--ink-probe：把标题文字按不同渲染参数各画一行，用于扫描「哪种设置最接近旧版」。</summary>
    private static int DiagInk()
    {
        var combos = new (string label, uint quality, int weight, int px)[]
        {
            ("q5-w700-p32", Native.CLEARTYPE_QUALITY, Native.FW_BOLD, 32),
            ("q6-w700-p32", Native.CLEARTYPE_NATURAL_QUALITY, Native.FW_BOLD, 32),
            ("q5-w800-p32", Native.CLEARTYPE_QUALITY, 800, 32),
            ("q5-w700-p33", Native.CLEARTYPE_QUALITY, Native.FW_BOLD, 33),
            ("q6-w700-p33", Native.CLEARTYPE_NATURAL_QUALITY, Native.FW_BOLD, 33),
            ("q5-w600-p32", Native.CLEARTYPE_QUALITY, Native.FW_SEMIBOLD, 32),
            ("q4-w700-p32", Native.ANTIALIASED_QUALITY, Native.FW_BOLD, 32),
            ("q5-w800-p33", Native.CLEARTYPE_QUALITY, 800, 33),
        };
        const int rowH = 84;
        const string text = "酷态科 10 号 Ultra";
        using var surf = Surface.Create(800, rowH * combos.Length);
        if (surf is null) return 2;
        Graphics g = surf.G;
        g.Clear(Color.FromArgb(34, 38, 45));
        for (int i = 0; i < combos.Length; i++)
        {
            var (label, quality, weight, px) = combos[i];
            using var font = GdiFont.CreateEx("Microsoft YaHei UI", px, weight, quality);
            GdiText.Draw(g, text, font, Color.FromArgb(236, 239, 243),
                new Rectangle(40, i * rowH, 720, rowH), TextAnchor.Left);
            LogSink.Write("[ink-probe] row=" + i + " " + label + " h=" + font.PixelHeight);
        }
        using var bmp = surf.ToBitmap();
        bmp.Save(@"D:\dsh projects\cuktech-ultra-tray\docs\tmp-ink.png", System.Drawing.Imaging.ImageFormat.Png);
        return 0;
    }

    /// <summary>--alpha-probe：验证 GDI+ 半透明填充在屏幕兼容 DDB 上的真实混合结果。</summary>
    private static int DiagAlpha()
    {
        int[] alphas = { 10, 18, 34, 60, 70 };
        var bg = Color.FromArgb(32, 36, 43);
        int w = 240, rowH = 48;

        using var surf = Surface.Create(w, rowH * alphas.Length);
        if (surf is null) return 2;
        Graphics sg = surf.G;
        sg.Clear(bg);
        for (int i = 0; i < alphas.Length; i++)
        {
            using var b = new SolidBrush(Color.FromArgb(alphas[i], 255, 255, 255));
            sg.FillRectangle(b, 8, i * rowH + 8, w - 16, rowH - 16);
            // 页签实际用的是 RoundRect 路径，一并试
            using var path = RoundRectPath(new Rectangle(120, i * rowH + 8, 100, rowH - 16), (rowH - 16) / 2f);
            sg.FillPath(b, path);
        }
        sg.Flush();
        using var surfBmp = surf.ToBitmap();

        using var refBmp = new System.Drawing.Bitmap(w, rowH * alphas.Length, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics rg = System.Drawing.Graphics.FromImage(refBmp))
        {
            rg.Clear(bg);
            for (int i = 0; i < alphas.Length; i++)
            {
                using var b = new SolidBrush(Color.FromArgb(alphas[i], 255, 255, 255));
                rg.FillRectangle(b, 8, i * rowH + 8, w - 16, rowH - 16);
            }
            rg.Flush();
        }

        for (int i = 0; i < alphas.Length; i++)
        {
            var a1 = surfBmp.GetPixel(40, i * rowH + 24);
            var a2 = refBmp.GetPixel(40, i * rowH + 24);
            var a3 = surfBmp.GetPixel(160, i * rowH + 24);
            LogSink.Write("[alpha-probe] a=" + alphas[i]
                + " ddb=" + a1.R + "," + a1.G + "," + a1.B
                + " gdi+=" + a2.R + "," + a2.G + "," + a2.B
                + " ddb-path=" + a3.R + "," + a3.G + "," + a3.B);
        }
        return 0;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundRectPath(Rectangle r, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>--render-demo 用的假数据：C1 4.0 W + C3 2.0 W = 总 6.0 W，与 docs\shot-panel-live.png 那张真机截图同一组数，
    /// 免得宣传视频里同一程序前后出现两套读数；控制页另给一个已断电的口。</summary>
    private static Snapshot DemoSnapshot(PanelWindow.Tab tab, bool merged = false)
    {
        var snap = new Snapshot
        {
            Connected = true,
            Status = "已连接",
            Firmware = "2.1.2_0073",
            At = new DateTime(2026, 11, 23, 20, 35, 54),   // 跟 docs\shot-panel-live.png 的页脚一致
            SceneMode = 3,
        };

        var c1 = snap.Ports[0];
        c1.HasData = true; c1.Idle = false; c1.Enabled = true;
        c1.Volts = 20.1; c1.Amps = 0.20; c1.Watts = 4.0; c1.State = 0x01;

        var c2 = snap.Ports[1];
        c2.HasData = true; c2.Idle = true; c2.State = 0x00;
        c2.Enabled = tab != PanelWindow.Tab.Control;   // 控制页演一个「已断电」的口

        var c3 = snap.Ports[2];
        c3.HasData = true; c3.Idle = false; c3.Enabled = true;
        c3.Volts = 5.1; c3.Amps = 0.40; c3.Watts = 2.0; c3.State = 0x01;

        var a = snap.Ports[3];
        a.HasData = true; a.Idle = true; a.State = 0x00;

        if (merged)
        {
            // issue #1 的那种情形：C3 与 A 共用一路功率计，C3 报 state=0x11 且带着合计值，
            // A 那边固件按「空闲」报（0x00）。界面应该显示「C3+A 5.1V · 0.40A · 2.0W」+ A 行「并入 C3+A」。
            c3.Watts = 2.0; c3.State = 0x11;
            a.Idle = true; a.State = 0x00;
        }

        return snap;
    }

    internal static void RunLoop()
    {
        while (true)
        {
            int r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }
}
