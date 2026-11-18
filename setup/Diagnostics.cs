namespace CuktechTraySetup;

/// <summary>--check：只体检，不动任何文件/注册表。也负责把安装/卸载结果填进报告。</summary>
internal static class Diagnostics
{
    public static SetupReport Check(SetupCommand command)
    {
        var report = new SetupReport { Action = "check" };
        var dir = command.InstallDir is null ? AppInfo.DefaultInstallDir : Paths.Normalize(command.InstallDir);

        report.Add("installDir", dir);
        report.Add("defaultInstallDir", AppInfo.DefaultInstallDir);
        report.Add("dirIsSafe", Paths.UnsafeReason(dir) is null ? "yes" : Paths.UnsafeReason(dir));
        report.Add("dirExists", Directory.Exists(dir) ? "yes" : "no");
        report.Add("requiresElevation", EnvironmentProbe.RequiresElevation(dir) ? "yes" : "no");
        report.Add("canWrite", EnvironmentProbe.CanWriteTo(dir) ? "yes" : "no");
        report.Add("runningInstances", EnvironmentProbe.CountRunning().ToString());
        report.Add("payloadSha256", PayloadInfo.Sha256);
        report.Add("payloadRawBytes", PayloadInfo.RawSize.ToString());
        report.Add("payloadCompressedBytes", PayloadInfo.CompressedSize.ToString());
        report.Add("appVersion", PayloadInfo.AppVersion);
        report.Add("buildTimeUtc", PayloadInfo.BuildTimeUtc);
        report.Add("uninstallKey", AppInfo.UninstallKeyPath);
        report.Add("uninstallKeyExists", RegistryStore.HasUninstallEntry() ? "yes" : "no");
        foreach (var kv in RegistryStore.ReadUninstallValues())
        {
            report.Add("registry." + kv.Key, kv.Value);
        }
        report.Add("autostartValue", RegistryStore.ReadAutoStart());
        report.Add("desktopShortcut", AppInfo.DesktopShortcutPath);
        report.Add("desktopShortcutExists", Shortcuts.Exists(AppInfo.DesktopShortcutPath) ? "yes" : "no");
        report.Add("desktopShortcutTarget", Shortcuts.ReadTarget(AppInfo.DesktopShortcutPath));
        report.Add("startMenuShortcut", AppInfo.StartMenuShortcutPath);
        report.Add("startMenuShortcutExists", Shortcuts.Exists(AppInfo.StartMenuShortcutPath) ? "yes" : "no");
        report.Add("dataDir", AppInfo.DataDir);
        report.Add("dataDirExists", Directory.Exists(AppInfo.DataDir) ? "yes" : "no");
        report.Add("logPath", Log.FilePath);

        report.Step("环境检查完成（未修改任何文件或注册表）");
        ConsoleBridge.WriteLine("安装目录      : " + dir);
        ConsoleBridge.WriteLine("可写          : " + (EnvironmentProbe.CanWriteTo(dir) ? "是" : "否"));
        ConsoleBridge.WriteLine("需要提权      : " + (EnvironmentProbe.RequiresElevation(dir) ? "是" : "否"));
        ConsoleBridge.WriteLine("运行中实例    : " + EnvironmentProbe.CountRunning());
        ConsoleBridge.WriteLine("内嵌主程序    : " + PayloadInfo.RawSize + " 字节, SHA256 " + PayloadInfo.Sha256);
        ConsoleBridge.WriteLine("卸载信息项    : " + (RegistryStore.HasUninstallEntry() ? "已存在" : "不存在"));
        return report;
    }

    public static void FillInstall(SetupReport report, InstallResult result)
    {
        report.Add("installDir", result.InstallDir);
        report.Add("exePath", result.ExePath);
        report.Add("exeBytes", File.Exists(result.ExePath) ? new FileInfo(result.ExePath).Length.ToString() : "0");
        report.Add("payloadSha256", result.PayloadSha256);
        report.Add("setupCopyPath", result.SetupCopyPath);
        report.Add("setupCopyExists", File.Exists(result.SetupCopyPath) ? "yes" : "no");
        report.Add("upgraded", result.Upgraded ? "yes" : "no");
        report.Add("previousVersion", result.PreviousVersion);
        report.Add("version", PayloadInfo.AppVersion);
        report.Add("killedPids", string.Join(",", result.KilledPids));
        report.Add("uninstallKey", AppInfo.UninstallKeyPath);
        report.Add("uninstallKeyExists", RegistryStore.HasUninstallEntry() ? "yes" : "no");
        foreach (var kv in RegistryStore.ReadUninstallValues())
        {
            report.Add("registry." + kv.Key, kv.Value);
        }
        report.Add("autostartValue", RegistryStore.ReadAutoStart());
        report.Add("desktopShortcut", AppInfo.DesktopShortcutPath);
        report.Add("desktopShortcutExists", Shortcuts.Exists(AppInfo.DesktopShortcutPath) ? "yes" : "no");
        report.Add("desktopShortcutTarget", Shortcuts.ReadTarget(AppInfo.DesktopShortcutPath));
        report.Add("startMenuShortcut", AppInfo.StartMenuShortcutPath);
        report.Add("startMenuShortcutExists", Shortcuts.Exists(AppInfo.StartMenuShortcutPath) ? "yes" : "no");
        report.Add("startMenuShortcutTarget", Shortcuts.ReadTarget(AppInfo.StartMenuShortcutPath));
        report.Add("launched", result.Launched ? "yes" : "no");
    }

    public static void FillUninstall(SetupReport report, UninstallResult result)
    {
        report.Add("installDir", result.InstallDir);
        report.Add("installDirExists", Directory.Exists(result.InstallDir) ? "yes" : "no");
        report.Add("removedDirectory", result.RemovedDirectory ? "yes" : "no");
        report.Add("leftovers", string.Join(";", result.Leftovers));
        report.Add("purgedData", result.PurgedData ? "yes" : "no");
        report.Add("dataDirRemoved", result.DataDirRemoved);
        report.Add("dataDirExists", Directory.Exists(AppInfo.DataDir) ? "yes" : "no");
        report.Add("uninstallKey", AppInfo.UninstallKeyPath);
        report.Add("uninstallKeyExists", RegistryStore.HasUninstallEntry() ? "yes" : "no");
        report.Add("autostartValue", RegistryStore.ReadAutoStart());
        report.Add("desktopShortcutExists", Shortcuts.Exists(AppInfo.DesktopShortcutPath) ? "yes" : "no");
        report.Add("startMenuShortcutExists", Shortcuts.Exists(AppInfo.StartMenuShortcutPath) ? "yes" : "no");
        report.Add("runningInstances", EnvironmentProbe.CountRunning().ToString());
    }
}
