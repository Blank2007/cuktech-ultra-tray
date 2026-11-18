using System.Runtime.InteropServices;

namespace CuktechTraySetup;

/// <summary>
/// WinExe 默认没有控制台。静默模式被命令行调用时，尽量复用父进程的控制台把进度打出来；
/// 拿不到控制台（比如被管道捕获）就只写日志文件。
/// </summary>
internal static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    private static bool _attached;

    public static void Attach()
    {
        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                _attached = true;
                var stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
            }
        }
        catch
        {
            _attached = false;
        }
    }

    public static void WriteLine(string message)
    {
        if (!_attached) return;
        try { Console.Out.WriteLine(message); }
        catch { _attached = false; }
    }
}
