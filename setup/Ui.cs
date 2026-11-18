using System.Drawing;
using System.Windows.Forms;

namespace CuktechTraySetup;

/// <summary>界面上的统一配色/字体，避免每个窗体各写一套。</summary>
internal static class Ui
{
    public static readonly Color Accent = Color.FromArgb(0, 114, 198);
    public static readonly Color Danger = Color.FromArgb(196, 43, 28);
    public static readonly Color Warning = Color.FromArgb(157, 90, 0);
    public static readonly Color Muted = Color.FromArgb(100, 100, 100);
    public static readonly Color FooterBack = Color.FromArgb(243, 243, 243);
    public static readonly Color PageBack = Color.White;

    private static readonly Font BaseFont =
        SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont ?? new Font(FontFamily.GenericSansSerif, 9F);

    public static readonly Font TitleFont = new(BaseFont.FontFamily, 15F, FontStyle.Bold);
    public static readonly Font SubtitleFont = new(BaseFont.FontFamily, 9F, FontStyle.Regular);
    public static readonly Font BodyFont = new(BaseFont.FontFamily, 9F, FontStyle.Regular);
    public static readonly Font BodyBoldFont = new(BaseFont.FontFamily, 9F, FontStyle.Bold);
    public static readonly Font MonoFont = CreateMonoFont();

    private static Font CreateMonoFont()
    {
        try
        {
            return new Font("Consolas", 8.5F, FontStyle.Regular);
        }
        catch
        {
            return new Font(BaseFont.FontFamily, 8.5F, FontStyle.Regular);
        }
    }

    public static Button PrimaryButton(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Size = new Size(96, 30),
        FlatStyle = FlatStyle.System,
    };

    public static Button SecondaryButton(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Size = new Size(96, 30),
        FlatStyle = FlatStyle.System,
    };

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? BodyFont,
        ForeColor = color ?? Color.Black,
        BackColor = Color.Transparent,
    };
}
