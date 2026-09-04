using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>单屏快照：该屏位图（含真实 DPI）、屏幕物理坐标原点、缩放系数</summary>
public readonly record struct ScreenSnapshot(
    BitmapSource Bitmap, System.Windows.Point Origin, double Dpi, double Scale);

/// <summary>
/// 屏幕捕获：逐屏抓取为各自 DPI 的 BitmapSource（Per-Monitor 适配）。
/// 每屏单独 BitBlt 物理像素并按该屏 DPI 打包，使 WPF 以 1:1 渲染，不再放大。
/// 使用 GDI BitBlt；后续可替换为 Graphics Capture API 以支持单窗口/无闪烁。
/// </summary>
public static class ScreenCapturer
{
    /// <summary>逐屏捕获所有显示器，返回每屏独立快照（跨屏内容重复，由各屏坐标区分）</summary>
    public static List<ScreenSnapshot> CapturePerScreen()
    {
        var list = new List<ScreenSnapshot>();
        foreach (var m in NativeMethods.GetMonitors())
        {
            int w = (int)m.Bounds.Width;
            int h = (int)m.Bounds.Height;
            if (w <= 0 || h <= 0) continue;

            var bmp = CaptureRegion((int)m.Bounds.X, (int)m.Bounds.Y, w, h, m.Dpi);
            list.Add(new ScreenSnapshot(bmp, new System.Windows.Point(m.Bounds.X, m.Bounds.Y), m.Dpi, m.Dpi / 96.0));
        }
        return list;
    }

    /// <summary>捕获整个虚拟屏幕为一张图（物理像素，96 DPI；仅用于不显示、直接裁剪输出的场景）</summary>
    public static BitmapSource CaptureVirtualScreen()
    {
        var (x, y, width, height) = NativeMethods.GetVirtualScreen();
        return CaptureRegion(x, y, width, height, 96.0);
    }

    /// <summary>捕获指定物理像素矩形区域，并按指定 DPI 打包位图（1 物理像素 = 1 DIP）</summary>
    public static BitmapSource CaptureRegion(int x, int y, int width, int height, double dpi = 96.0)
    {
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new InvalidOperationException("无法获取屏幕 DC");

            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            oldObj = NativeMethods.SelectObject(memDc, hBitmap);

            bool ok = NativeMethods.BitBlt(memDc, 0, 0, width, height,
                screenDc, x, y, NativeMethods.SRCCOPY);
            if (!ok)
                throw new InvalidOperationException("BitBlt 截图失败");

            // CreateBitmapSourceFromHBitmap 默认按 96 DPI 生成；高缩放屏会被 WPF 放大显示。
            // 这里按该屏真实 DPI 重新打包，使 1 物理像素 == 1 DIP，1:1 渲染不错位。
            var raw = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            BitmapSource bmp = Math.Abs(dpi - 96.0) < 0.01
                ? raw
                : CreateWithDpi(raw, dpi);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (oldObj != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldObj);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>从 BitmapSource 中裁剪指定矩形（物理像素）</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// 由逐屏快照合成一张覆盖整个虚拟屏幕的物理像素位图（96 DPI，仅用于裁剪输出，不直接显示）。
    /// 用 WriteableBitmap.WritePixels 逐屏写入像素，保证编辑/钉图清晰不缩放。
    /// </summary>
    public static BitmapSource ComposeFullPhysical(IReadOnlyList<ScreenSnapshot> snaps)
    {
        var (vx, vy, vw, vh) = NativeMethods.GetVirtualScreen();
        var wb = new WriteableBitmap(vw, vh, 96, 96, PixelFormats.Bgra32, null);

        foreach (var snap in snaps)
        {
            var src = snap.Bitmap;
            int w = src.PixelWidth, h = src.PixelHeight;
            int stride = w * ((src.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[h * stride];
            src.CopyPixels(pixels, stride, 0);

            int dx = (int)(snap.Origin.X - vx);
            int dy = (int)(snap.Origin.Y - vy);
            wb.WritePixels(new Int32Rect(dx, dy, w, h), pixels, stride, 0);
        }

        wb.Freeze();
        return wb;
    }

    /// <summary>用指定 DPI 重新包装位图（像素数据不变，仅改 DPI 元数据）</summary>
    private static BitmapSource CreateWithDpi(BitmapSource source, double dpi)
    {
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        int stride = w * ((source.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[h * stride];
        source.CopyPixels(pixels, stride, 0);
        return BitmapSource.Create(
            w, h, dpi, dpi, source.Format, null, pixels, stride);
    }
}
