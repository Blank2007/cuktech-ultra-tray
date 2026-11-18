using System.Runtime.InteropServices;

namespace CuktechTraySetup;

/// <summary>通过 WScript.Shell 的 COM 对象创建/读取 .lnk（不引 interop 程序集，避免再带一个 dll）。</summary>
internal static class Shortcuts
{
    public static void Create(string shortcutPath, string targetExe, string workingDir)
    {
        var dir = Path.GetDirectoryName(shortcutPath);
        if (string.IsNullOrEmpty(dir)) throw new InstallException("快捷方式路径无效：" + shortcutPath);

        try
        {
            Directory.CreateDirectory(dir);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InstallException("系统缺少 WScript.Shell，无法创建快捷方式。");

            object? shell = null;
            object? link = null;
            try
            {
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InstallException("无法创建 WScript.Shell 实例。");

                dynamic s = shell;
                link = s.CreateShortcut(shortcutPath);
                dynamic l = link;
                l.TargetPath = targetExe;
                l.WorkingDirectory = workingDir;
                l.IconLocation = targetExe + ",0";
                l.Description = AppInfo.AppName;
                l.WindowStyle = 1;
                l.Save();
            }
            finally
            {
                ReleaseCom(link);
                ReleaseCom(shell);
            }
        }
        catch (InstallException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InstallException("创建快捷方式失败：" + ex.Message, ex);
        }
    }

    public static void Delete(string shortcutPath)
    {
        try
        {
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);

            // 开始菜单里我们独占一个组，空了就把组也删掉
            var parent = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(parent) &&
                Path.GetFileName(parent).Equals(AppInfo.StartMenuGroup, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex)
        {
            Log.Write("[shortcut] 删除快捷方式失败：" + ex.Message);
        }
    }

    public static bool Exists(string shortcutPath) => File.Exists(shortcutPath);

    /// <summary>读回 .lnk 的目标路径，用于自检。</summary>
    public static string? ReadTarget(string shortcutPath)
    {
        object? shell = null;
        object? link = null;
        try
        {
            if (!File.Exists(shortcutPath)) return null;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            dynamic s = shell;
            link = s.CreateShortcut(shortcutPath);
            dynamic l = link;
            string target = l.TargetPath;
            return target;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(link);
            ReleaseCom(shell);
        }
    }

    private static void ReleaseCom(object? o)
    {
        if (o is null) return;
        try
        {
            if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
        catch
        {
            // 忽略
        }
    }
}
