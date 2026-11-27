using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>
/// 托盘图标：一枚小闪电徽标，只表达「连上了没有」。
/// 实时功率交给任务栏那块数字（TaskbarWindow）和鼠标悬停提示，图标不再跟着功率变色、也不再画数字。
/// （NativeAOT 版：图标尺寸问 GetSystemMetrics，明暗色自己算，不依赖 ControlPaint。）
/// </summary>
internal static class WattIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static int IconSize
    {
        get
        {
            int v = Native.GetSystemMetrics(Native.SM_CXSMICON);
            return Math.Max(16, v <= 0 ? 16 : v);
        }
    }

    /// <summary>画一枚闪电徽标。accent 只用来区分「已连接 / 未连接」两种状态。</summary>
    /// <param name="badge">true = 右上角加一颗红点（有新版本）。</param>
    public static Icon Create(Color accent, bool badge = false)
    {
        using var bmp = CreateBitmap(IconSize, accent, badge);
        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// 按指定边长画一枚图标位图。托盘里用 GetSystemMetrics 得到的 16px，
    /// 文档和视频要放大展示，所以把边长交出来（画法完全不变，只是尺寸不同）。
    /// </summary>
    public static Bitmap CreateBitmap(int size, Color accent, bool badge = false)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        FillRoundedBackground(g, size, accent);
        DrawBolt(g, size);
        if (badge) DrawBadge(g, size);
        return bmp;
    }

    /// <summary>
    /// 右上角那颗红点（有新版本时）。外面留一圈浅色描边：任务栏/托盘底色是亮是暗都看不清
    /// 红点压在深绿底上的边界，白边一圈就分开了。
    /// </summary>
    private static void DrawBadge(Graphics g, int size)
    {
        float s = size;
        float r = Math.Max(2.4f, s * 0.24f);
        float ring = Math.Max(0.8f, s * 0.05f);
        float outer = r + ring;
        float cx = s - outer;
        float cy = outer;

        using (var edge = new SolidBrush(Color.FromArgb(248, 249, 251)))
            g.FillEllipse(edge, cx - outer, cy - outer, outer * 2, outer * 2);
        using var brush = new SolidBrush(Color.FromArgb(229, 57, 53));
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
    }

    private static Color Lighten(Color c, float amount) => Color.FromArgb(
        c.A,
        (int)Math.Round(c.R + (255 - c.R) * amount),
        (int)Math.Round(c.G + (255 - c.G) * amount),
        (int)Math.Round(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, float amount) => Color.FromArgb(
        c.A,
        (int)Math.Round(c.R * (1f - amount)),
        (int)Math.Round(c.G * (1f - amount)),
        (int)Math.Round(c.B * (1f - amount)));

    private static void FillRoundedBackground(Graphics g, int size, Color baseColor)
    {
        var rect = new Rectangle(0, 0, size, size);
        int radius = Math.Max(3, (int)Math.Round(size * 0.20));
        using var path = RoundRect(rect, radius);

        Color top = Lighten(baseColor, 0.30f);
        Color bottom = Darken(baseColor, 0.22f);
        using (var grad = new LinearGradientBrush(new Rectangle(0, 0, size, size + 1), top, bottom, 90f))
            g.FillPath(grad, path);

        using (var hi = new Pen(Color.FromArgb(38, 255, 255, 255), 1f))
        {
            var hiRect = new Rectangle(1, 0, size - 2, size - 1);
            using var hiPath = RoundRect(hiRect, Math.Max(2, radius - 1));
            g.DrawPath(hi, hiPath);
        }
    }

    private static void DrawBolt(Graphics g, int size)
    {
        float s = size;
        var pts = new[]
        {
            new PointF(s * 0.55f, s * 0.14f),
            new PointF(s * 0.28f, s * 0.56f),
            new PointF(s * 0.46f, s * 0.56f),
            new PointF(s * 0.40f, s * 0.88f),
            new PointF(s * 0.74f, s * 0.42f),
            new PointF(s * 0.54f, s * 0.42f),
        };
        using var brush = new SolidBrush(Color.White);
        g.FillPolygon(brush, pts);
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
