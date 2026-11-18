using System.Diagnostics;

namespace CuktechTraySetup;

internal static class EnvironmentProbe
{
    /// <summary>按进程名找正在跑的主程序（CuktechTray-Setup 不会被误伤）。</summary>
    public static List<Process> RunningInstances()
    {
        var list = new List<Process>();
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppInfo.ExecutableName)))
            {
                list.Add(p);
            }
        }
        catch (Exception ex)
        {
            Log.Write("[probe] 枚举进程失败：" + ex.Message);
        }
        return list;
    }

    public static int CountRunning() => RunningInstances().Count;

    /// <summary>关掉所有正在运行的实例，返回被杀掉的 PID。失败只记日志。</summary>
    public static List<int> KillRunning()
    {
        var killed = new List<int>();
        foreach (var p in RunningInstances())
        {
            try
            {
                var pid = p.Id;
                Log.Write($"[kill] 关闭正在运行的实例 PID {pid}");
                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(5000)) Log.Write($"[kill] PID {pid} 5 秒内没退出，继续尝试");
                killed.Add(pid);
            }
            catch (Exception ex)
            {
                Log.Write("[kill] 关闭实例失败：" + ex.Message);
            }
            finally
            {
                p.Dispose();
            }
        }

        if (killed.Count > 0) Thread.Sleep(400); // 等文件句柄释放
        return killed;
    }

    /// <summary>
    /// 这个目录是否需要管理员权限才能写。Program Files / Windows 这类系统目录直接判定为需要，
    /// 其余位置用一次真实写入探测。
    /// </summary>
    public static bool RequiresElevation(string dir)
    {
        var full = Paths.Normalize(dir);
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonProgramFiles,
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System,
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) continue;
            var rootFull = Paths.Normalize(root);
            if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return !CanWriteTo(dir);
    }

    public static bool CanWriteTo(string dir)
    {
        try
        {
            var probeDir = Paths.ExistingAncestor(dir);
            if (!Directory.Exists(probeDir)) return false;
            var probeFile = Path.Combine(probeDir, ".cuktech-probe-" + Guid.NewGuid().ToString("N"));
            using (var fs = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
