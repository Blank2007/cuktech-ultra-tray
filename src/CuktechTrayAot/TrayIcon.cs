using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>Shell_NotifyIcon 的托管包装：加图标、改图标、气泡提示、删图标。</summary>
internal sealed class TrayIcon : IDisposable
{
    private Native.NOTIFYICONDATA _data;
    private bool _added;

    public uint Id { get; }

    public TrayIcon(IntPtr owner, uint id, uint callbackMessage)
    {
        Id = id;
        _data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = owner,
            uID = id,
            uCallbackMessage = callbackMessage,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    public bool Add(Icon icon, string tip)
    {
        _data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        _data.hIcon = icon.Handle;
        _data.szTip = Trim(tip, 127);
        _added = Native.Shell_NotifyIconW(Native.NIM_ADD, ref _data);
        if (_added)
        {
            _data.uVersion = 4; // NOTIFYICON_VERSION_4
            Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref _data);
        }
        else LogSink.Write("[tray] Shell_NotifyIcon NIM_ADD 失败 err=" + Marshal.GetLastWin32Error());
        return _added;
    }

    public void Update(Icon icon, string tip)
    {
        if (!_added) return;
        _data.uFlags = Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        _data.hIcon = icon.Handle;
        _data.szTip = Trim(tip, 127);
        if (!Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref _data))
            LogSink.Write("[tray] NIM_MODIFY 失败 err=" + Marshal.GetLastWin32Error());
    }

    public void ShowBalloon(string title, string text, uint flags = Native.NIIF_INFO)
    {
        if (!_added) return;
        _data.uFlags = Native.NIF_INFO;
        _data.szInfoTitle = Trim(title, 63);
        _data.szInfo = Trim(text, 255);
        _data.dwInfoFlags = flags;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref _data);
    }

    private static string Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s.Substring(0, max);
    }

    public void Remove()
    {
        if (!_added) return;
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref _data);
        _added = false;
    }

    public void Dispose() => Remove();
}
