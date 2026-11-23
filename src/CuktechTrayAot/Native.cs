using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>
/// Win32 声明集中处。AOT 版不引 WinForms，所以窗口、消息、GDI、托盘、会话通知全部走 P/Invoke。
/// 只声明真正用到的入口，保持 NativeAOT 的裁剪结果干净。
/// </summary>
internal static class Native
{
    // ---- 窗口样式 ----
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_CLIPCHILDREN = 0x02000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_LAYERED = 0x00080000;

    // ---- 消息 ----
    internal const uint WM_NCCREATE = 0x0081;
    internal const uint WM_NCDESTROY = 0x0082;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_CREATE = 0x0001;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_PAINT = 0x000F;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_MOUSELEAVE = 0x02A3;
    internal const uint WM_SETCURSOR = 0x0020;
    internal const uint WM_NCHITTEST = 0x0084;
    internal const uint WM_ACTIVATE = 0x0006;
    internal const uint WM_ACTIVATEAPP = 0x001C;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_DPICHANGED = 0x02E0;
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_APP = 0x8000;
    internal const uint WM_TRAYICON = WM_APP + 1;
    internal const uint WM_APP_POST = WM_APP + 2;   // 把后台线程的回调排进 UI 线程
    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_LBUTTONDBLCLK_ = 0x0203;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOW = 5;

    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOMOVE = 0x0002;
    internal const int SWP_NOZORDER = 0x0004;
    internal const int SWP_NOACTIVATE = 0x0010;
    internal const int SWP_SHOWWINDOW = 0x0040;
    internal const int SWP_FRAMECHANGED = 0x0020;
    internal const int SWP_NOOWNERZORDER = 0x0200;
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal static readonly IntPtr HWND_BOTTOM = new(1);

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // ---- 结构 ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public int left, top, right, bottom;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    // ---- user32 ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int cx, int cy, bool repaint);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseW, int ellipseH);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint interval, IntPtr callback);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool KillTimer(IntPtr hWnd, UIntPtr id);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetCapture();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursorW(IntPtr instance, string cursorName);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    internal static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    internal static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    internal static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorInfo(ref CURSORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")]
    internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    internal static extern IntPtr OpenClipboard(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder buffer, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder buffer, int size);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool SetParent(IntPtr hWnd, IntPtr parent);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLongW(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    internal static extern int SetWindowLongW(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr value);

    internal const int GWL_EXSTYLE = -20;
    internal const int IDC_ARROW_ID = 32512;
    internal const int IDC_HAND_ID = 32649;
    internal const int GWL_STYLE = -16;
    internal const int GWLP_HWNDPARENT = -8;

    // ---- 会话通知（锁屏 / 切换用户） ----
    internal const int NOTIFY_FOR_THIS_SESSION = 0;
    internal const uint WM_WTSSESSION_CHANGE = 0x02B1;
    internal const int WTS_SESSION_LOCK = 0x7;
    internal const int WTS_CONSOLE_DISCONNECT = 0x2;
    internal const int WTS_REMOTE_DISCONNECT = 0x4;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

    [DllImport("wtsapi32.dll")]
    internal static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ---- shell ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    internal const uint NIM_ADD = 0;
    internal const uint NIM_MODIFY = 1;
    internal const uint NIM_DELETE = 2;
    internal const uint NIM_SETVERSION = 4;

    internal const uint NIF_MESSAGE = 0x01;
    internal const uint NIF_ICON = 0x02;
    internal const uint NIF_TIP = 0x04;
    internal const uint NIF_STATE = 0x08;
    internal const uint NIF_INFO = 0x10;
    internal const uint NIF_SHOWTIP = 0x80;

    internal const uint NIIF_INFO = 0x01;
    internal const uint NIIF_WARNING = 0x02;
    internal const uint NIIF_ERROR = 0x03;

    // NOTIFYICON_VERSION_4 下，左键点击会另外发 NIN_SELECT / NIN_KEYSELECT，
    // 右键会发 WM_CONTEXTMENU，而不是只给 WM_LBUTTONUP / WM_RBUTTONUP
    internal const uint NIN_SELECT = 0x0400;
    internal const uint NIN_KEYSELECT = 0x0401;
    internal const uint WM_CONTEXTMENU = 0x007B;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr ShellExecuteW(IntPtr hwnd, string? op, string file, string? parameters, string? directory, int showCmd);

    // 裸 Win32 里没有 MessageBox.Show，弹窗直接调这个
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    internal const uint MB_OK = 0x00000000;
    internal const uint MB_OKCANCEL = 0x00000001;
    internal const uint MB_YESNO = 0x00000004;
    internal const uint MB_YESNOCANCEL = 0x00000003;
    internal const uint MB_ICONERROR = 0x00000010;
    internal const uint MB_ICONWARNING = 0x00000030;
    internal const uint MB_ICONINFORMATION = 0x00000040;
    internal const uint MB_ICONQUESTION = 0x00000020;
    internal const int IDCANCEL = 2;
    internal const int IDOK = 1;
    internal const int IDYES = 6;
    internal const int IDNO = 7;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW(string? name);

    // ---- gdi ----
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    // DrawTextW 在 user32.dll 里，不在 gdi32（写成 gdi32 会在 AOT 下抛 EntryPointNotFoundException）
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawTextW(IntPtr hdc, string text, int length, ref RECT rect, uint flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out SIZE size);

    /// <summary>读回 DC 上实际选中的字体名（CreateFontW 找不到字体会静默回退，只有这里能看出来）。</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetTextFaceW(IntPtr hdc, int count, [Out] char[] face);

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    internal static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height, IntPtr srcDc, int srcX, int srcY, uint rop);

    internal const uint SRCCOPY = 0x00CC0020;
    internal const int TRANSPARENT = 1;

    internal const uint DT_LEFT = 0x0000;
    internal const uint DT_CENTER = 0x0001;
    internal const uint DT_RIGHT = 0x0002;
    internal const uint DT_VCENTER = 0x0004;
    internal const uint DT_SINGLELINE = 0x0020;
    internal const uint DT_NOPREFIX = 0x0800;
    internal const uint DT_END_ELLIPSIS = 0x8000;
    internal const uint DT_WORDBREAK = 0x0010;
    internal const uint DT_NOCLIP = 0x0100;

    internal const int FW_NORMAL = 400;
    internal const int FW_SEMIBOLD = 600;
    internal const int FW_BOLD = 700;

    // ---- 分层窗口（任务栏那小段数字靠 UpdateLayeredWindow 画，背景全透明）----
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr hSection, uint offset);

    internal const int SW_SHOWNA = 8;
    internal const int SW_SHOWNORMAL = 1;
    internal const int HWND_TOPMOST = -1;
    internal const uint ULW_ALPHA = 0x00000002;
    internal const byte AC_SRC_OVER = 0;
    internal const byte AC_SRC_ALPHA = 1;

    internal const uint DEFAULT_CHARSET = 1;
    internal const uint ANTIALIASED_QUALITY = 4;
    internal const uint CLEARTYPE_QUALITY = 5;
    internal const uint CLEARTYPE_NATURAL_QUALITY = 6;

    // ---- 常用封装 ----
    internal static int LoWord(IntPtr value) => unchecked((short)(long)value);
    internal static int HiWord(IntPtr value) => unchecked((short)((long)value >> 16));
}
