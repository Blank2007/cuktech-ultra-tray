using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CuktechTray;

/// <summary>
/// 屏幕兼容的双缓冲表面：CreateCompatibleDC(屏幕) + CreateCompatibleBitmap(屏幕)。
///
/// 为什么不用 Bitmap + Graphics.FromImage：GDI 只有在「显示设备」DC 上才按系统
/// 那套 ClearType 对比度/gamma 做子像素渲染；画到独立 DIB（Bitmap）上会明显变灰、
/// 笔画变细，看着比 Windows 自己画的字虚一档。旧 WinForms 版是直接在窗口 DC
/// （也是屏幕设备）上画的，所以双缓冲的中间表面也必须是屏幕兼容 DDB，才能让
/// 新版在真机上和它一个观感。
/// </summary>
internal sealed class Surface : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public Graphics G { get; }

    private IntPtr _dc;
    private IntPtr _bmp;
    private IntPtr _old;

    private Surface(int w, int h, IntPtr dc, IntPtr bmp, IntPtr old)
    {
        Width = w;
        Height = h;
        _dc = dc;
        _bmp = bmp;
        _old = old;
        G = Graphics.FromHdc(dc);
        // 这两个必须是默认值：CompositingQuality.HighQuality 会让 GDI+ 在半透明填充时
        // 走 gamma 校正（线性光）混合，白 34 的页签胶囊底叠出来是 (107,108,109)，
        // 而旧版 WinForms 只设了 SmoothingMode，混合在 sRGB 空间里，同一处是 (62,65,71)
        // —— 看着就是「整块界面比旧版亮、发灰」。（实测：--alpha-probe）
        G.SmoothingMode = SmoothingMode.AntiAlias;
        G.PixelOffsetMode = PixelOffsetMode.Default;
        G.CompositingQuality = CompositingQuality.Default;
        G.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    /// <summary>建不出屏幕兼容 DDB 时返回 null（调用方退化成不画，不抛异常）。</summary>
    public static Surface? Create(int w, int h)
    {
        if (w < 1 || h < 1) return null;
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            IntPtr dc = Native.CreateCompatibleDC(screen);
            if (dc == IntPtr.Zero) return null;
            IntPtr bmp = Native.CreateCompatibleBitmap(screen, w, h);
            if (bmp == IntPtr.Zero)
            {
                Native.DeleteDC(dc);
                return null;
            }
            IntPtr old = Native.SelectObject(dc, bmp);
            return new Surface(w, h, dc, bmp, old);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>把整块表面贴到目标 DC（窗口 DC）上。</summary>
    public void Blit(IntPtr destDc)
    {
        G.Flush();
        Native.BitBlt(destDc, 0, 0, Width, Height, _dc, 0, 0, Native.SRCCOPY);
    }

    /// <summary>导出一份 GDI+ 位图：只有 --render 存 PNG 用，真机绘制不需要。</summary>
    public Bitmap ToBitmap()
    {
        G.Flush();
        return Image.FromHbitmap(_bmp);
    }

    public void Dispose()
    {
        G.Dispose();
        if (_dc != IntPtr.Zero)
        {
            if (_old != IntPtr.Zero) Native.SelectObject(_dc, _old);
            if (_bmp != IntPtr.Zero) Native.DeleteObject(_bmp);
            Native.DeleteDC(_dc);
        }
        _dc = IntPtr.Zero;
        _bmp = IntPtr.Zero;
        _old = IntPtr.Zero;
    }
}
