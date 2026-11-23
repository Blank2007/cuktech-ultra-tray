using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>
/// GDI 字体：和 WinForms 的 TextRenderer 同一条渲染路径（ClearType、Hinting 一致），
/// 而 GDI+ 的 Graphics.DrawString 排版会略微软偏差。要为「和旧版看起来一模一样」负责，
/// 所以文字统一走 GDI，图形走 GDI+。
/// </summary>
internal sealed class GdiFont : IDisposable
{
    /// <summary>逻辑字号（96dpi 下的像素），用来做纵向对齐计算。</summary>
    public float LogicalPixel { get; }
    public int PixelHeight { get; }
    internal IntPtr Handle { get; private set; }

    private GdiFont(IntPtr handle, int pixelHeight, float logicalPixel)
    {
        Handle = handle;
        PixelHeight = pixelHeight;
        LogicalPixel = logicalPixel;
    }

    /// <summary>按「96dpi 下的逻辑像素」创建，内部乘缩放比例。</summary>
    /// <param name="fontBase">字体量化基准缩放：诊断渲染（如 2.5 倍）时传真机的 1.25，
    /// 字号先按 1.25 取整、再整数倍放大，这样导出的图跟真机同比例，而不是各取各的整。</param>
    public static GdiFont Create(string family, float logicalPx, float scale, bool bold = false, float fontBase = 0f)
    {
        // 必须向上取整：旧版用 GDI+ 的 Font(family, px, style, GraphicsUnit.Pixel)，
        // GDI+ 换算成 LOGFONT 时对 13*1.25=16.25 得到的是 17px 字形而不是 16px，
        // 用 Math.Round 会让标题、页签、页脚整圈汉字小一档（实测宽度差 4%），
        // 大数字这类恰好落在 .5 上的字号两种取整结果相同，所以只有汉字露馅。
        float b = fontBase > 0f ? fontBase : scale;
        int px = (int)MathF.Round(MathF.Ceiling(logicalPx * b) * (scale / b));
        if (px < 1) px = 1;
        // 必须跟 WinForms 的 FontStyle.Bold 一致用 700：Segoe UI 有独立的 Semibold 字体，
        // 用 600 会真的换成那套字形，标题/大数字会比旧版窄一圈。
        IntPtr h = Native.CreateFontW(-px, 0, 0, 0, bold ? Native.FW_BOLD : Native.FW_NORMAL,
            0, 0, 0, Native.DEFAULT_CHARSET, 0, 0, Native.CLEARTYPE_QUALITY, 0, family);
        if (h == IntPtr.Zero)
        {
            h = Native.CreateFontW(-px, 0, 0, 0, bold ? Native.FW_BOLD : Native.FW_NORMAL,
                0, 0, 0, Native.DEFAULT_CHARSET, 0, 0, Native.CLEARTYPE_QUALITY, 0, "Segoe UI");
        }
        return new GdiFont(h, px, logicalPx);
    }

    /// <summary>诊断用：指定像素高度、字重和字体质量（--ink-probe 拿它扫参数）。</summary>
    internal static GdiFont CreateEx(string family, int px, int weight, uint quality)
    {
        if (px < 1) px = 1;
        IntPtr h = Native.CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, Native.DEFAULT_CHARSET, 0, 0,
            quality, 0, family);
        return new GdiFont(h, px, px);
    }

    public int Measure(string? text, IntPtr hdc)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        IntPtr old = Native.SelectObject(hdc, Handle);
        Native.GetTextExtentPoint32W(hdc, text!, text!.Length, out Native.SIZE size);
        Native.SelectObject(hdc, old);
        return size.cx;
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.DeleteObject(Handle);
            Handle = IntPtr.Zero;
        }
    }
}

internal enum TextAnchor
{
    Left,
    Center,
    Right,
}

/// <summary>GDI 文字绘制封装。所有坐标都是物理像素。</summary>
internal static class GdiText
{
    /// <summary>把带 alpha 的前景色按背景色预混成不透明白色，因为 GDI 不支持 alpha 文字。</summary>
    public static Color Blend(Color fg, Color backdrop)
    {
        if (fg.A >= 255) return fg;
        float a = fg.A / 255f;
        return Color.FromArgb(
            255,
            (int)Math.Round(fg.R * a + backdrop.R * (1 - a)),
            (int)Math.Round(fg.G * a + backdrop.G * (1 - a)),
            (int)Math.Round(fg.B * a + backdrop.B * (1 - a)));
    }

    private static uint ToColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    /// <summary>在矩形内画单行文字（默认垂直居中），对齐方式可选，超出宽度加省略号。</summary>
    public static void Draw(Graphics g, string? text, GdiFont font, Color color, Rectangle rect,
        TextAnchor anchor = TextAnchor.Left, bool ellipsis = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        uint flags = Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_NOPREFIX;
        flags |= anchor switch
        {
            TextAnchor.Center => Native.DT_CENTER,
            TextAnchor.Right => Native.DT_RIGHT,
            _ => Native.DT_LEFT,
        };
        if (ellipsis) flags |= Native.DT_END_ELLIPSIS;

        IntPtr hdc = g.GetHdc();
        try
        {
            IntPtr oldFont = Native.SelectObject(hdc, font.Handle);
            uint oldColor = Native.SetTextColor(hdc, ToColorRef(color));
            int oldMode = Native.SetBkMode(hdc, Native.TRANSPARENT);
            var r = new Native.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
            Native.DrawTextW(hdc, text!, text!.Length, ref r, flags);
            Native.SetBkMode(hdc, oldMode);
            Native.SetTextColor(hdc, oldColor);
            Native.SelectObject(hdc, oldFont);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    /// <summary>顶部对齐的单行文字（用于卡片里两行叠排的场合）。</summary>
    public static void DrawTop(Graphics g, string? text, GdiFont font, Color color, Rectangle rect,
        TextAnchor anchor = TextAnchor.Left, bool ellipsis = true)
        => Draw(g, text, font, color, new Rectangle(rect.X, rect.Y - (int)Math.Round(font.PixelHeight * 0.18f),
            rect.Width, rect.Height + (int)Math.Round(font.PixelHeight * 0.36f)), anchor, ellipsis);
}
