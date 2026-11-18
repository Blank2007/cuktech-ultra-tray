using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CuktechTray;

/// <summary>
/// 菜单的深色外观。Windows 默认右键菜单是浅色的，跟深色面板摆在一起很跳；
/// 这里把背景、边框、hover、勾号、箭头、分隔线全部换成面板那一套颜色。
/// 只作用于显式调用 <see cref="Skin"/> 的菜单，不改 ToolStripManager 全局设置。
/// </summary>
internal static class UiTheme
{
    internal static readonly Color MenuBg = Color.FromArgb(30, 34, 41);
    internal static readonly Color MenuLine = Color.FromArgb(58, 64, 74);
    internal static readonly Color MenuHover = Color.FromArgb(52, 58, 70);
    internal static readonly Color MenuFg = Color.FromArgb(232, 234, 237);
    internal static readonly Color MenuFgDim = Color.FromArgb(118, 125, 135);
    internal static readonly Color MenuGreen = Color.FromArgb(52, 211, 153);

    private static readonly ToolStripRenderer Shared = new DarkMenuRenderer();

    /// <summary>给一个右键菜单套上深色皮肤（字体也顺手统一成微软雅黑 UI）。</summary>
    internal static void Skin(ContextMenuStrip menu)
    {
        menu.Renderer = Shared;
        menu.BackColor = MenuBg;
        menu.ForeColor = MenuFg;
        menu.Font = new Font("Microsoft YaHei UI", 9.5f);
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    internal DarkMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(UiTheme.MenuBg);
        e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(UiTheme.MenuBg);
        e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(UiTheme.MenuLine);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 1, Math.Max(1, e.Item.Width - 4), Math.Max(1, e.Item.Height - 2));
        using var path = Round(r, 4);
        using var b = new SolidBrush(UiTheme.MenuHover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? UiTheme.MenuFg : UiTheme.MenuFgDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // 默认勾号是跟着系统主题来的位图，深色底上几乎看不见，所以自己画一道绿勾
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var ib = e.Item.Bounds;
        float cx = ib.Left + 10;
        float cy = ib.Top + ib.Height / 2f;
        using var pen = new Pen(UiTheme.MenuGreen, Math.Max(1.4f, e.Graphics.DpiX / 96f * 1.4f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(cx - 4.5f, cy),
            new PointF(cx - 1.2f, cy + 3.4f),
            new PointF(cx + 5f, cy - 4.2f),
        });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var r = e.Item.Bounds;
        using var pen = new Pen(Color.FromArgb(50, 56, 66));
        int y = r.Top + r.Height / 2;
        e.Graphics.DrawLine(pen, r.Left + 10, y, r.Right - 10, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Item is null) return;
        e.ArrowColor = e.Item.Enabled ? UiTheme.MenuFg : UiTheme.MenuFgDim;
        base.OnRenderArrow(e);
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => UiTheme.MenuBg;
        public override Color MenuBorder => UiTheme.MenuLine;
        public override Color MenuItemBorder => UiTheme.MenuHover;
        public override Color MenuItemSelected => UiTheme.MenuHover;
        public override Color MenuItemSelectedGradientBegin => UiTheme.MenuHover;
        public override Color MenuItemSelectedGradientEnd => UiTheme.MenuHover;
        public override Color MenuItemPressedGradientBegin => UiTheme.MenuHover;
        public override Color MenuItemPressedGradientMiddle => UiTheme.MenuHover;
        public override Color MenuItemPressedGradientEnd => UiTheme.MenuHover;
        public override Color ImageMarginGradientBegin => UiTheme.MenuBg;
        public override Color ImageMarginGradientMiddle => UiTheme.MenuBg;
        public override Color ImageMarginGradientEnd => UiTheme.MenuBg;
        public override Color SeparatorDark => Color.FromArgb(50, 56, 66);
        public override Color SeparatorLight => Color.FromArgb(50, 56, 66);
        public override Color CheckBackground => UiTheme.MenuBg;
        public override Color CheckSelectedBackground => UiTheme.MenuHover;
        public override Color CheckPressedBackground => UiTheme.MenuHover;
        public override Color ButtonSelectedHighlight => UiTheme.MenuHover;
        public override Color ButtonSelectedBorder => UiTheme.MenuLine;
        public override Color ToolStripBorder => UiTheme.MenuLine;
        public override Color GripDark => UiTheme.MenuBg;
        public override Color GripLight => UiTheme.MenuLine;
    }
}
