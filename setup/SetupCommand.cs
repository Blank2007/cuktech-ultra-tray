namespace CuktechTraySetup;

/// <summary>安装/卸载选项集合（引擎层，不关心 GUI）。</summary>
internal sealed class InstallOptions
{
    public string InstallDir { get; init; } = AppInfo.DefaultInstallDir;
    public bool DesktopShortcut { get; init; } = true;
    public bool StartMenuShortcut { get; init; } = true;
    public bool AutoStart { get; init; }
    public bool LaunchAfter { get; init; }
    /// <summary>true = 直接关掉正在运行的实例，不再询问。</summary>
    public bool Force { get; init; }
    public bool Silent { get; init; }
}

internal sealed class UninstallOptions
{
    /// <summary>null = 优先读注册表里的 InstallLocation，再退回默认位置。</summary>
    public string? InstallDir { get; init; }
    public bool PurgeData { get; init; }
    public bool Force { get; init; }
    public bool Silent { get; init; }
}

/// <summary>命令行解析。GUI 模式 = 不带 --silent 也没有别的动作参数。</summary>
internal sealed class SetupCommand
{
    public bool Uninstall { get; private set; }
    public bool Silent { get; private set; }
    public bool Force { get; private set; }
    public bool Check { get; private set; }
    public bool ShowHelp { get; private set; }
    /// <summary>卸载器已把自身搬到 %TEMP% 再跑，避免删自己的目录。</summary>
    public bool Relocated { get; private set; }
    public string? InstallDir { get; private set; }
    public string? ReportPath { get; private set; }
    public bool? DesktopShortcut { get; private set; }
    public bool? StartMenuShortcut { get; private set; }
    public bool? AutoStart { get; private set; }
    public bool? LaunchAfter { get; private set; }
    public bool? PurgeData { get; private set; }
    public string? Error { get; private set; }

    public static SetupCommand Parse(string[] args)
    {
        var c = new SetupCommand();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Length == 0) continue;

            if (Is(a, "--help") || Is(a, "-h") || Is(a, "/?") || Is(a, "-?")) { c.ShowHelp = true; continue; }
            if (Is(a, "--uninstall") || Is(a, "-u")) { c.Uninstall = true; continue; }
            if (Is(a, "--silent") || Is(a, "-s") || Is(a, "/s")) { c.Silent = true; continue; }
            if (Is(a, "--force")) { c.Force = true; continue; }
            if (Is(a, "--check")) { c.Check = true; continue; }
            if (Is(a, "--relocated")) { c.Relocated = true; continue; }
            if (Is(a, "--shortcuts")) { c.DesktopShortcut = true; c.StartMenuShortcut = true; continue; }
            if (Is(a, "--no-shortcuts")) { c.DesktopShortcut = false; c.StartMenuShortcut = false; continue; }
            if (Is(a, "--start-menu")) { c.StartMenuShortcut = true; continue; }
            if (Is(a, "--no-start-menu")) { c.StartMenuShortcut = false; continue; }
            if (Is(a, "--autostart")) { c.AutoStart = true; continue; }
            if (Is(a, "--no-autostart")) { c.AutoStart = false; continue; }
            if (Is(a, "--launch")) { c.LaunchAfter = true; continue; }
            if (Is(a, "--no-launch")) { c.LaunchAfter = false; continue; }
            if (Is(a, "--purge-data")) { c.PurgeData = true; continue; }
            if (Is(a, "--keep-data")) { c.PurgeData = false; continue; }

            if (Is(a, "--dir") || Is(a, "--install-dir"))
            {
                if (!TryTakeValue(args, ref i, out var v)) { c.Error = a + " 后面缺路径"; break; }
                c.InstallDir = v;
                continue;
            }
            if (Is(a, "--report"))
            {
                if (!TryTakeValue(args, ref i, out var v)) { c.Error = a + " 后面缺文件路径"; break; }
                c.ReportPath = v;
                continue;
            }

            if (c.Error is null)
            {
                c.Error = "无法识别的参数：" + a;
            }
        }
        return c;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[index + 1];
            index++;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool Is(string arg, string name) => arg.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 静默安装的默认值。已经装过的话按“升级”来：
    /// 装回老目录（绝不装出第二份），快捷方式和自启按现在装着的状态回填
    /// （否则 --silent 升级会把用户原有的快捷方式和开机自启一起删掉）。
    /// </summary>
    public InstallOptions ToInstallOptions()
    {
        var upgrading = RegistryStore.HasUninstallEntry();
        return new InstallOptions
        {
            InstallDir = InstallDir is not null
                ? Paths.Normalize(InstallDir)
                : ExistingInstallDir() ?? AppInfo.DefaultInstallDir,
            DesktopShortcut = DesktopShortcut ?? (upgrading ? Shortcuts.Exists(AppInfo.DesktopShortcutPath) : !Silent),
            StartMenuShortcut = StartMenuShortcut ?? (upgrading ? Shortcuts.Exists(AppInfo.StartMenuShortcutPath) : true),
            AutoStart = AutoStart ?? (upgrading && !string.IsNullOrEmpty(RegistryStore.ReadAutoStart())),
            LaunchAfter = LaunchAfter ?? !Silent,
            Force = Force || Silent,
            Silent = Silent,
        };
    }

    /// <summary>注册表里记着的安装目录；没装过、或者记的值不可用时返回 null。</summary>
    public static string? ExistingInstallDir()
    {
        if (!RegistryStore.HasUninstallEntry()) return null;
        var dir = RegistryStore.ReadInstallLocation();
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            return Paths.Normalize(dir!);
        }
        catch
        {
            return null;
        }
    }

    public UninstallOptions ToUninstallOptions() => new()
    {
        InstallDir = InstallDir is null ? null : Paths.Normalize(InstallDir),
        PurgeData = PurgeData ?? false,
        Force = Force || Silent,
        Silent = Silent,
    };

    public const string HelpText =
        "酷态科 10 号 Ultra 托盘功率表 · 安装程序\r\n" +
        "\r\n" +
        "  双击运行                       图形向导安装\r\n" +
        "  CuktechTray-Setup.exe --uninstall   图形向导卸载（会问是否保留配置）\r\n" +
        "\r\n" +
        "静默模式（无人值守 / 脚本）\r\n" +
        "  --silent, -s          不做界面，直接按参数装/卸。静默安装默认不建快捷方式、\r\n" +
        "                        不开机自启、装完不启动，并且会直接关掉正在运行的实例。\r\n" +
        "  --dir <路径>          安装目录，默认 %LOCALAPPDATA%\\Programs\\CuktechTray\r\n" +
        "  --shortcuts           创建桌面 + 开始菜单快捷方式（静默默认不建）\r\n" +
        "  --no-shortcuts        不创建任何快捷方式\r\n" +
        "  --autostart           写 HKCU\\...\\Run（静默默认不写）\r\n" +
        "  --no-autostart        不写开机自启\r\n" +
        "  --launch              装完立刻启动主程序（静默默认不启动）\r\n" +
        "  --no-launch           装完不启动\r\n" +
        "  --purge-data          卸载时连配置和日志一起删（默认保留）\r\n" +
        "  --keep-data           卸载时保留配置和日志（默认行为）\r\n" +
        "  --force               遇到正在运行的实例直接关闭，不再询问\r\n" +
        "  --check               只做环境检查，不写任何东西\r\n" +
        "  --report <文件>       把结果写成 JSON（安装/卸载/检查都支持）\r\n" +
        "  --help, -h            显示本帮助\r\n" +
        "\r\n" +
        "退出码：0 成功 / 1 失败 / 2 参数错误 / 3 程序在运行 / 4 目标位置需要管理员权限 / 5 用户取消\r\n";
}
