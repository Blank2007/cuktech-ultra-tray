using System.Text;

namespace CuktechTraySetup;

/// <summary>安装日志：固定写到 %TEMP%\CuktechTray-Setup.log，同时回显到控制台（若挂了控制台）。</summary>
internal static class Log
{
    private static readonly object Gate = new();

    /// <summary>日志路径：优先 %TEMP%，写不进去时回退到 %LOCALAPPDATA%\CuktechTray\logs\。</summary>
    public static string FilePath { get; } = ResolveLogPath();

    private static string ResolveLogPath()
    {
        var primary = Path.Combine(Path.GetTempPath(), "CuktechTray-Setup.log");
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CuktechTray",
            "logs",
            "CuktechTray-Setup.log");

        foreach (var candidate in new[] { primary, fallback })
        {
            try
            {
                var folder = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                using (new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    // 能打开就说明这个位置可写
                }
                return candidate;
            }
            catch
            {
                // 换下一个候选路径
            }
        }

        return primary;
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不影响安装
            }
        }
        ConsoleBridge.WriteLine(message);
    }

    public static void Reset()
    {
        lock (Gate)
        {
            try { File.Delete(FilePath); } catch { /* 忽略 */ }
        }
    }
}
