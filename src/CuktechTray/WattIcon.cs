using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>托盘图标的观感方案。</summary>
internal enum IconStyle
{
    /// <summary>功率色渐变底 + 白色数字（默认）。</summary>
    Badge = 0,
    /// <summary>近黑深色底 + 功率色数字，最像系统原生托盘图标。</summary>
    Dark = 1,
    /// <summary>纯色底 + 细亮边 + 白数字，扁平风。</summary>
    Flat = 2,
    /// <summary>无底透明，只有带描边的数字，跟任务栏最融合。</summary>
    Minimal = 3,
    /// <summary>功率色底 + 白色闪电，只靠颜色和提示文字表达功率。</summary>
    Bolt = 4,
}

/// <summary>
/// 托盘图标绘制：把总功率画成数字。
/// 小尺寸下用 3×5 点阵而不是缩小系统字体，保证 16px 依然锐利；
/// 颜色 / 圆角 / 描边 / 高光都按像素对齐，避免托盘里糊成一团。
/// </summary>
internal static class WattIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { "111", "101", "101", "101", "111" },
        ['1'] = new[] { "010", "110", "010", "010", "111" },
        ['2'] = new[] { "111", "001", "111", "100", "111" },
        ['3'] = new[] { "111", "001", "111", "001", "111" },
        ['4'] = new[] { "101", "101", "111", "001", "001" },
        ['5'] = new[] { "111", "100", "111", "001", "111" },
        ['6'] = new[] { "111", "100", "111", "101", "111" },
        ['7'] = new[] { "111", "001", "001", "010", "010" },
        ['8'] = new[] { "111", "101", "111", "101", "111" },
        ['9'] = new[] { "111", "101", "111", "001", "111" },
        ['-'] = new[] { "000", "000", "111", "000", "000" },
    };

    private static int IconSize => Math.Max(16, SystemInformation.SmallIconSize.Width);

    public static Icon Create(string text, Color accent) => Create(text, accent, IconStyle.Badge);

    public static Icon Create(string text, Color accent, IconStyle style)
    {
        int size = IconSize;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (style == IconStyle.Bolt)
            {
                FillRoundedBackground(g, size, accent);
                DrawBolt(g, size);
            }
            else if (style == IconStyle.Minimal)
            {
                // 无底：只画数字本身，靠任务栏深色背景托住；加一点点暗描边避免在浅色任务栏上消失
                DrawGlyphs(g, Normalize(text), size, accent, outline: Color.FromArgb(80, 0, 0, 0));
            }
            else
            {
                Color bg, fg;
                switch (style)
                {
                    case IconStyle.Dark:
                        bg = Color.FromArgb(38, 42, 50);
                        fg = Color.FromArgb(238, 240, 243);
                        break;
                    case IconStyle.Flat:
                        bg = accent;
                        fg = Color.White;
                        break;
                    default:
                        bg = accent;
                        fg = Color.White;
                        break;
                }

                if (style == IconStyle.Flat)
                {
                    using var brush = new SolidBrush(bg);
                    using var path = RoundRect(new Rectangle(0, 0, size, size), Math.Max(3, (int)Math.Round(size * 0.20)));
                    g.FillPath(brush, path);
                    using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
                    g.DrawPath(pen, path);
                }
                else
                {
                    FillRoundedBackground(g, size, bg);
                }

                // 16px 里加描边会把 3×5 点阵的笔画糊在一块，所以只在数字作底时才考虑
                DrawGlyphs(g, Normalize(text), size, fg, outline: null);

                if (style == IconStyle.Dark) DrawAccentBar(g, size, accent);
            }
        }

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

    private static char[] Normalize(string text)
    {
        var chars = text.Where(c => Glyphs.ContainsKey(c)).ToArray();
        return chars.Length == 0 ? new[] { '-' } : chars;
    }

    /// <summary>圆角矩形底：竖向渐变 + 顶部 1px 高光 + 底部 1px 压暗。</summary>
    private static void FillRoundedBackground(Graphics g, int size, Color baseColor)
    {
        var rect = new Rectangle(0, 0, size, size);
        int radius = Math.Max(3, (int)Math.Round(size * 0.20));
        using var path = RoundRect(rect, radius);

        Color top = ControlPaint.Light(baseColor, 0.30f);
        Color bottom = ControlPaint.Dark(baseColor, 0.22f);
        using (var grad = new LinearGradientBrush(new Rectangle(0, 0, size, size + 1), top, bottom, 90f))
            g.FillPath(grad, path);

        // 顶部内高光：只留很淡的一层，太亮会让方块显脏
        using (var hi = new Pen(Color.FromArgb(38, 255, 255, 255), 1f))
        {
            var hiRect = new Rectangle(1, 0, size - 2, size - 1);
            using var hiPath = RoundRect(hiRect, Math.Max(2, radius - 1));
            g.DrawPath(hi, hiPath);
        }
    }

    /// <summary>Dark 风格底部的一条功率色带：颜色信息留着，但不刺眼。</summary>
    private static void DrawAccentBar(Graphics g, int size, Color accent)
    {
        int h = Math.Max(2, size / 8);
        int inset = Math.Max(3, (int)Math.Round(size * 0.3));
        using var brush = new SolidBrush(accent);
        using var path = RoundRect(new Rectangle(inset, size - h - 2, size - inset * 2, h), Math.Max(1, h / 2));
        g.FillPath(brush, path);
    }

    /// <summary>白色闪电（功率色底用）。</summary>
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

    /// <summary>3×5 点阵数字：自动挑选能放下的最大倍数，可加 1px 描边。</summary>
    private static void DrawGlyphs(Graphics g, char[] chars, int size, Color color, Color? outline)
    {
        const int cellW = 3, cellH = 5;
        int gapUnits = chars.Length - 1;
        int byWidth = (size - 2) / (cellW * chars.Length + gapUnits);
        int byHeight = (size - 2) / cellH;
        int scale = Math.Max(1, Math.Min(byWidth, byHeight));

        int textW = cellW * scale * chars.Length + scale * gapUnits;
        int textH = cellH * scale;
        int ox = (size - textW) / 2;
        int oy = (size - textH) / 2;

        var saved = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        if (outline is { } oc)
        {
            using var outlineBrush = new SolidBrush(oc);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    FillGlyphPixels(g, chars, ox + dx, oy + dy, scale, cellW, cellH, outlineBrush);
                }
            }
        }

        using var brush = new SolidBrush(color);
        FillGlyphPixels(g, chars, ox, oy, scale, cellW, cellH, brush);

        g.SmoothingMode = saved;
    }

    private static void FillGlyphPixels(Graphics g, char[] chars, int ox, int oy, int scale, int cellW, int cellH, Brush brush)
    {
        for (int i = 0; i < chars.Length; i++)
        {
            var glyph = Glyphs[chars[i]];
            int gx = ox + i * (cellW * scale + scale);
            for (int row = 0; row < cellH; row++)
            {
                for (int col = 0; col < cellW; col++)
                {
                    if (glyph[row][col] != '1') continue;
                    g.FillRectangle(brush, gx + col * scale, oy + row * scale, scale, scale);
                }
            }
        }
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
