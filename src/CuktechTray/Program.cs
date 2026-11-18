namespace CuktechTray;

internal static class Program
{
    private const string MutexName = @"Local\CuktechTray.SingleInstance";
    private const string ShowEventName = @"Local\CuktechTray.ShowPanel";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isNew);
        if (!isNew)
        {
            // 已经在跑了：让那个实例把面板弹出来，自己安静退出（双击 exe 才有反应）
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => LogSink.Write("[ui-crash] " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogSink.Write("[crash] " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogSink.Write("[task-crash] " + e.Exception);
            e.SetObserved();
        };

        var settings = Settings.Load();
        if (args.Any(a => a.Equals("--pin", StringComparison.OrdinalIgnoreCase))) settings.PanelPinned = true;
        bool forceSetup = args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase));
        bool autoScan = args.Any(a => a.Equals("--setup-scan", StringComparison.OrdinalIgnoreCase));
        if ((forceSetup || autoScan || !settings.IsComplete)
            && !args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // 任何一台新电脑上第一次运行：走扫码向导拿到自己的 MAC 与 token
            Application.EnableVisualStyles();
            if (!SetupWizardForm.Run(settings, autoScan)) return;
        }
        LogSink.Write($"[start] v{typeof(Program).Assembly.GetName().Version} mac={settings.Mac} tokenLen={settings.TokenHex.Length}");

        if (args.Any(a => a.Equals("--dump-icons", StringComparison.OrdinalIgnoreCase)))
        {
            IconDump.Run();
            return;
        }

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.RunAsync(settings).GetAwaiter().GetResult() ? 0 : 1;
            return;
        }

        // 默认启动后弹一下面板（右下角，失焦自动收起）；--tray-only 只蹲托盘，--panel 屏幕中央常显（截图用）
        var showOnStart = args.Any(a => a.Equals("--panel", StringComparison.OrdinalIgnoreCase)) ? ShowOnStart.Center
            : args.Any(a => a.Equals("--tray-only", StringComparison.OrdinalIgnoreCase)) ? ShowOnStart.None
            : ShowOnStart.Corner;
        // --control：开程序就把控制页摆出来（顺手也方便截图 / 做快捷方式）
        bool openControl = args.Any(a => a.Equals("--control", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApp(settings, showOnStart, showEvent, openControl));
    }
}
