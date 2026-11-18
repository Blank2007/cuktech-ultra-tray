using System.Runtime.InteropServices;
using System.Text;

namespace DockProbe;

internal static class Native
{
    internal const int GWL_STYLE = -16;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string cls, string? name);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")] internal static extern void SetLastError(int code);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(int access, bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr proc, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int need);

    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr p);

    [DllImport("shcore.dll")]
    internal static extern int GetProcessDpiAwareness(IntPtr proc, out int awareness);

    internal static string IntegrityOf(IntPtr proc)
    {
        if (!OpenProcessToken(proc, 0x0008, out var token)) return "openToken err=" + Marshal.GetLastWin32Error();
        try
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out int need);
            IntPtr buf = Marshal.AllocHGlobal(need);
            try
            {
                if (!GetTokenInformation(token, 25, buf, need, out _)) return "tokInfo err=" + Marshal.GetLastWin32Error();
                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte count = Marshal.ReadByte(sid, 1);
                int last = Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
                string name = last >= 0x4000 ? "High" : last >= 0x3000 ? "Medium" : last >= 0x2000 ? "Low" : "other";
                return name + "(0x" + last.ToString("X") + ")";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { }
    }

    internal static string DpiOf(IntPtr proc)
    {
        int rc = GetProcessDpiAwareness(proc, out int a);
        if (rc != 0) return "err=" + rc;
        return a switch { 0 => "unaware", 1 => "system", 2 => "permonitor", _ => "?" + a };
    }
}

internal sealed class ProbeForm : Form
{
    internal ProbeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = new Rectangle(700, 1300, 90, 40);
        Text = "dockprobe";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
            return cp;
        }
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.AppendLine(s);

        var self = Native.GetCurrentProcess();
        Line("self IL=" + Native.IntegrityOf(self) + " dpi=" + Native.DpiOf(self));

        var tray = Native.FindWindow("Shell_TrayWnd", null);
        Native.GetWindowThreadProcessId(tray, out uint trayPid);
        Line("tray=0x" + tray.ToInt64().ToString("X") + " pid=" + trayPid);
        var trayProc = Native.OpenProcess(0x0400, false, trayPid);
        Line("tray IL=" + (trayProc == IntPtr.Zero ? "openProcess err=" + Marshal.GetLastWin32Error() : Native.IntegrityOf(trayProc)));
        if (trayProc != IntPtr.Zero) Line("tray dpi=" + Native.DpiOf(trayProc));

        using var f = new ProbeForm();
        IntPtr h = f.Handle;
        Line("createHandle=0x" + h.ToInt64().ToString("X"));
        Line("style before=0x" + Native.GetWindowLong(h, Native.GWL_STYLE).ToString("X"));

        int style = Native.GetWindowLong(h, Native.GWL_STYLE);
        Native.SetWindowLong(h, Native.GWL_STYLE, (style & ~Native.WS_POPUP) | Native.WS_CHILD);

        Native.SetLastError(0);
        IntPtr before = Native.GetParent(h);
        Native.SetLastError(0);
        IntPtr ret = Native.SetParent(h, tray);
        int err = Marshal.GetLastWin32Error();
        IntPtr after = Native.GetParent(h);
        Line("setparent before=0x" + before.ToInt64().ToString("X") + " ret=0x" + ret.ToInt64().ToString("X") +
             " err=" + err + " after=0x" + after.ToInt64().ToString("X") + " ok=" + (after == tray));

        if (after != tray)
        {
            f.Show();
            Application.DoEvents();
            Native.SetLastError(0);
            IntPtr ret2 = Native.SetParent(h, tray);
            int err2 = Marshal.GetLastWin32Error();
            Line("retry-after-show ret=0x" + ret2.ToInt64().ToString("X") + " err=" + err2 +
                 " parent=0x" + Native.GetParent(h).ToInt64().ToString("X"));
        }

        var path = Path.Combine(AppContext.BaseDirectory, "dockprobe.txt");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Console.WriteLine(sb.ToString());
    }
}
