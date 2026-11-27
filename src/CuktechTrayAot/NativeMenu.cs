namespace CuktechTray;

/// <summary>
/// 一条菜单项。Sub 非空就是子菜单，Click 是点下去要做的事。
/// </summary>
internal sealed class MenuEntry
{
    public string Text = string.Empty;
    public bool IsSeparator;

    /// <summary>true = 多选项（画系统原生对勾）。</summary>
    public bool Checked;

    /// <summary>单选样式：左边画圆点而不是勾。用在一组互斥的选项上（位置档位、大小档位）。</summary>
    public bool Radio;

    /// <summary>行首那颗小红点：表示「这条菜单上有事要你注意」（现在只用在「检查更新」有新版本时）。</summary>
    public bool Dot;
    public bool Enabled = true;
    public bool Bold;
    public Action? Click;
    public MenuEntry[]? Sub;

    public bool HasSub => Sub is { Length: > 0 };

    public static MenuEntry Item(string text, Action? click = null) => new() { Text = text, Click = click };
    public static MenuEntry BoldItem(string text, Action? click = null) => new() { Text = text, Click = click, Bold = true };
    public static MenuEntry CheckItem(string text, bool on, Action? click = null) => new() { Text = text, Checked = on, Click = click };
    public static MenuEntry RadioItem(string text, bool on, Action? click = null) => new() { Text = text, Checked = on, Radio = true, Click = click };
    public static MenuEntry Dim(string text) => new() { Text = text, Enabled = false };
    public static MenuEntry DotItem(string text, Action? click = null) => new() { Text = text, Click = click, Dot = true };
    public static MenuEntry Sep() => new() { IsSeparator = true };
    public static MenuEntry WithSub(string text, params MenuEntry[] items) => new() { Text = text, Sub = items };
}

/// <summary>
/// 托盘右键菜单。0.5.6 起不再自己开窗口画：直接 CreatePopupMenu + TrackPopupMenuEx，
/// 菜单窗口（类名 #32768）由系统创建，子菜单展开/收起、键盘上下与左右、Esc、点击外部关闭、
/// 高 DPI 缩放、无障碍朗读全部归 user32 管。
///
/// 为什么换掉原来的自绘菜单（MenuWindow.cs）：自绘版靠「根菜单 SetCapture 一把抓」来接收子菜单上的
/// 鼠标，而 WinBase 在 WM_LBUTTONUP 里会 ReleaseCapture，于是「点父项打开子菜单 → 松手」这一步之后
/// 捕获就没了；子菜单窗口自己又不处理点击（它只转发），结果子菜单里任何一项都点不动。
/// 系统菜单天然没有这个问题，观感也跟 TranslucentTB 那类原生托盘菜单一致（深色模式自动深色）。
/// </summary>
internal static class NativeMenu
{
    // 菜单项 id → 要做的事。id 只在这一次弹出期间有效，Show 前后都清空。
    private static readonly Dictionary<int, Action?> Actions = new();
    private static int _nextId = 1000;

    /// <summary>
    /// 在屏幕坐标 (sx, sy) 弹出菜单，用户选完就执行对应动作。
    /// 这个调用会阻塞到菜单关闭（TrackPopupMenuEx 自带消息循环），跟 WinForms 的 TrackPopupMenu 一样。
    /// </summary>
    public static void Show(IntPtr owner, MenuEntry[] items, int sx, int sy)
    {
        Actions.Clear();
        int defaultPos = -1;
        IntPtr menu = Build(items, ref defaultPos);
        if (menu == IntPtr.Zero) return;

        try
        {
            // 「显示功率面板」是主项，让它像系统默认项那样加粗。
            if (defaultPos >= 0) Native.SetMenuDefaultItem(menu, (uint)defaultPos, 1);
            Native.SetForegroundWindow(owner);
            int cmd = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, sx, sy, owner, IntPtr.Zero);
            // 菜单关闭后补一条空消息，避免「点完之后第一下点击被吞」的老毛病。
            Native.PostMessageW(owner, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd <= 0) return;
            if (!Actions.TryGetValue(cmd, out var act) || act is null) return;
            try
            {
                act();
            }
            catch (Exception ex)
            {
                LogSink.Write("[menu] 菜单动作异常: " + ex);
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
            Actions.Clear();
        }
    }

    public static bool IsOpen => false;   // 系统菜单自己管生命周期，这里只需要保持调用方编译不变

    public static void CloseAll() { }     // 同上：没有需要自己关的窗口了

    private static IntPtr Build(MenuEntry[] items, ref int defaultPos)
    {
        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return IntPtr.Zero;

        bool radioGroup = false;
        foreach (var e in items) if (e.Radio) radioGroup = true;
        if (radioGroup) Native.SetRadioCheck(menu, true);

        for (int i = 0; i < items.Length; i++)
        {
            var e = items[i];
            if (e.IsSeparator)
            {
                Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                continue;
            }

            if (e.HasSub)
            {
                var sub = Build(e.Sub!, ref defaultPos);
                if (sub == IntPtr.Zero) continue;
                uint flags = Native.MF_POPUP | (e.Enabled ? 0u : Native.MF_GRAYED);
                Native.AppendMenuW(menu, flags, new UIntPtr((ulong)sub.ToInt64()), e.Text);
                continue;
            }

            uint itemFlags = Native.MF_STRING;
            if (e.Checked) itemFlags |= Native.MF_CHECKED;
            if (!e.Enabled) itemFlags |= Native.MF_GRAYED;

            int id = _nextId++;
            Actions[id] = e.Click;
            string text = e.Dot ? "● " + e.Text : e.Text;
            Native.AppendMenuW(menu, itemFlags, (UIntPtr)(uint)id, text);

            if (e.Bold && defaultPos < 0) defaultPos = i;
        }

        return menu;
    }
}
