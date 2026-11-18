using Microsoft.Win32;

namespace CuktechTraySetup;

/// <summary>“应用和功能”里的卸载项 + HKCU 的开机自启项，全部只写当前用户，不动 HKLM。</summary>
internal static class RegistryStore
{
    public static void WriteUninstallEntry(string installDir, string setupCopyPath, string exePath, long installedBytes)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppInfo.UninstallKeyPath, writable: true)
                ?? throw new InstallException("无法创建卸载信息注册表项：" + AppInfo.UninstallKeyPath);

            key.SetValue("DisplayName", AppInfo.AppName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", PayloadInfo.AppVersion, RegistryValueKind.String);
            key.SetValue("Publisher", AppInfo.Publisher, RegistryValueKind.String);
            key.SetValue("DisplayIcon", exePath + ",0", RegistryValueKind.String);
            key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
            key.SetValue("UninstallString", Quote(setupCopyPath) + " --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", Quote(setupCopyPath) + " --uninstall --silent", RegistryValueKind.String);
            key.SetValue("EstimatedSize", (int)Math.Max(1, installedBytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
            key.SetValue("Language", "zh-CN", RegistryValueKind.String);
        }
        catch (InstallException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InstallException("写入卸载信息失败（当前用户注册表不可写）：" + ex.Message, ex);
        }
    }

    public static string? ReadInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKeyPath);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool HasUninstallEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKeyPath);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static string? ReadUninstallValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKeyPath);
            return key?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static void DeleteUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            throw new InstallException("删除卸载信息失败：" + ex.Message, ex);
        }
    }

    public static void SetAutoStart(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppInfo.RunKeyPath, writable: true)
                ?? throw new InstallException("无法打开开机自启注册表项。");
            key.SetValue(AppInfo.RunValueName, Quote(exePath) + " --tray-only", RegistryValueKind.String);
        }
        catch (InstallException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InstallException("写入开机自启失败：" + ex.Message, ex);
        }
    }

    public static string? ReadAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppInfo.RunKeyPath);
            return key?.GetValue(AppInfo.RunValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public static void DeleteAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppInfo.RunKeyPath, writable: true);
            key?.DeleteValue(AppInfo.RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("[registry] 删除开机自启失败：" + ex.Message);
        }
    }

    public static Dictionary<string, string?> ReadUninstallValues()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "DisplayName", "DisplayVersion", "Publisher", "DisplayIcon", "InstallLocation",
                     "UninstallString", "QuietUninstallString", "EstimatedSize", "NoModify", "NoRepair",
                 })
        {
            result[name] = ReadUninstallValue(name);
        }
        return result;
    }

    /// <summary>路径里带空格时必须加引号，否则 UninstallString 传到资源管理器里会断掉。</summary>
    public static string Quote(string path) => "\"" + path + "\"";
}
