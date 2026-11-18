using System.Drawing.Imaging;

namespace CuktechTray;

/// <summary>
/// 把托盘图标按 8 倍最近邻放大拼成对照表，用来挑观感方案、检查 16px 下的可读性。
/// </summary>
internal static class IconDump
{
    private static readonly string[] Samples = { "0", "7", "15", "42", "99", "120", "--" };

    private static readonly Color AccentGreen = Color.FromArgb(46, 125, 50);
    private static readonly Color AccentOrange = Color.FromArgb(239, 108, 0);
    private static readonly Color AccentRed = Color.FromArgb(198, 40, 40);
    private static readonly Color AccentOff = Color.FromArgb(94, 99, 106);

    public static void Run()
    {
        var styles = new[]
        {
            IconStyle.Badge, IconStyle.Dark, IconStyle.Flat, IconStyle.Minimal, IconStyle.Bolt,
        };
        var labels = new[]
        {
            "Badge  渐变底+白数字", "Dark  深底+彩色数字", "Flat  纯色+亮边", "Minimal  无底描边", "Bolt  闪电",
        };

        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        const int zoom = 8;
        int cellW = size * zoom + 26;
        int rowH = size * zoom + 34;
        int padLeft = 210;

        using var sheet = new Bitmap(padLeft + cellW * Samples.Length, rowH * styles.Length + 16, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var labelFont = new Font("Microsoft YaHei UI", 11f);
            using var smallFont = new Font("Segoe UI", 10f);
            using var white = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(150, 150, 150));

            for (int r = 0; r < styles.Length; r++)
            {
                g.DrawString(labels[r], labelFont, white, 12, r * rowH + 14);
                for (int c = 0; c < Samples.Length; c++)
                {
                    var accent = c <= 0 ? AccentOff : c <= 2 ? AccentGreen : c == 3 ? AccentOrange : AccentRed;
                    using var icon = WattIcon.Create(Samples[c], accent, styles[r]);
                    using var bmp = icon.ToBitmap();
                    int x = padLeft + c * cellW + 8;
                    int y = r * rowH + 8;
                    g.DrawImage(bmp, new Rectangle(x, y, size * zoom, size * zoom));
                    g.DrawString(Samples[c], smallFont, dim, x + 4, y + size * zoom + 4);
                }
            }
        }

        var exePath = Path.Combine(AppContext.BaseDirectory, "icons.png");
        sheet.Save(exePath, ImageFormat.Png);
        LogSink.Write("[dump] icons -> " + exePath);
        try
        {
            Directory.CreateDirectory(@"D:\dsh projects\cuktech-ultra-tray\docs");
            sheet.Save(@"D:\dsh projects\cuktech-ultra-tray\docs\icons-compare.png", ImageFormat.Png);
            LogSink.Write("[dump] icons -> docs\\icons-compare.png");
        }
        catch { }
    }
}
