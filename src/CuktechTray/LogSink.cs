using System.Text;

namespace CuktechTray;

/// <summary>极简滚动日志：logs/app.log，超过 512KB 自动截断。</summary>
internal static class LogSink
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "logs");
    private static string FilePath => Path.Combine(Dir, "app.log");

    public static event Action<string>? LineWritten;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    var backup = Path.Combine(Dir, "app.old.log");
                    File.Delete(backup);
                    File.Move(FilePath, backup);
                }
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        LineWritten?.Invoke(line);
    }
}
