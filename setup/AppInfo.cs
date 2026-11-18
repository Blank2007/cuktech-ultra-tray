namespace CuktechTraySetup;

/// <summary>安装器用到的固定名称、注册表位置与默认路径。</summary>
internal static class AppInfo
{
    public const string AppName = "酷态科 10 号 Ultra 托盘功率表";
    public const string ShortAppName = "酷态科托盘功率表";
    public const string Publisher = "CuktechTray";

    /// <summary>托盘主程序文件名（与 dist\CuktechTray.exe 一致）。</summary>
    public const string ExecutableName = "CuktechTray.exe";

    /// <summary>安装器自己拷进安装目录的那份副本名，卸载时由它干活。</summary>
    public const string SetupFileName = "CuktechTray-Setup.exe";

    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CuktechTray";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>与主程序 TrayApp.RunValueName 保持一致，两边相互识别。</summary>
    public const string RunValueName = "CuktechTray";

    public const string ShortcutFileName = "酷态科托盘功率表.lnk";
    public const string StartMenuGroup = "CuktechTray";

    /// <summary>主程序自己的数据目录（exe 同目录不可写时的兜底位置）。</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CuktechTray");

    /// <summary>默认安装位置：当前用户目录，写入不需要 UAC。</summary>
    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CuktechTray");

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName);

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuGroup, ShortcutFileName);
}
