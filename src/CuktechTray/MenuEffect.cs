using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CuktechTray;

/// <summary>
/// 把右键菜单调成 Win11 那种「圆角 + 亚克力毛玻璃」。
/// 两件事都借系统 API：圆角走 DWM 的 DWMWA_WINDOW_CORNER_PREFERENCE（Win11 22000+），
/// 背景模糊走 user32 的 SetWindowCompositionAttribute（ACCENT_ENABLE_ACRYLICBLURBEHIND）。
/// 任何一步失败都静默跳过：老系统上菜单还是那块深色不透明底，只是没有圆角和模糊。
/// </summary>
internal static class MenuEffect
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_ROUND = 2;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>已经挂上模糊的窗口。绘制圆角边框时要知道自己是不是透明的。</summary>
    private static readonly HashSet<IntPtr> Blurred = new HashSet<IntPtr>();

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>给菜单和它所有子菜单挂效果。子菜单是第一次展开才建窗口的，所以用 HandleCreated 补一遍。</summary>
    internal static void Apply(ToolStripDropDown dd)
    {
        if (dd is null) return;
        if (dd.IsHandleCreated) Stylize(dd.Handle);
        else dd.HandleCreated += (s, _) => Stylize(((ToolStripDropDown)s!).Handle);

        foreach (ToolStripItem item in dd.Items)
        {
            switch (item)
            {
                case ToolStripMenuItem mi when mi.HasDropDownItems:
                    Apply(mi.DropDown);
                    break;
                case ToolStripSplitButton sb:
                    Apply(sb.DropDown);
                    break;
            }
        }
    }

    /// <summary>这个窗口现在是不是「透出毛玻璃」的状态（决定要不要画不透明底）。</summary>
    internal static bool IsBlurred(IntPtr hwnd) => hwnd != IntPtr.Zero && Blurred.Contains(hwnd);

    private static void Stylize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // 圆角不支持就算了
        }

        // 想要真模糊得靠 DWM 的窗口背景类型（Win11 22H2+）；失败就退回下面那层亚克力 tint
        try
        {
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
        catch
        {
        }

        // GradientColor 是 0xAABBGGRR，跟 Color.ToArgb 的字节顺序相反：菜单底色 (30,34,41) → tint 0xF229221E
        // alpha 给到 F2：透一点点底下的画面，但文字对比度不塌
        var policy = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = unchecked((int)0xF229221E),
            AnimationId = 0,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, mem, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = mem,
                SizeOfData = size,
            };
            if (SetWindowCompositionAttribute(hwnd, ref data) != 0) Blurred.Add(hwnd);
        }
        catch
        {
            // 模糊不支持就算了，菜单会退回不透明底
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }
}
