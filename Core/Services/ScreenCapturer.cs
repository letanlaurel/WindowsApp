using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>
/// 屏幕捕获：抓取整个虚拟屏幕（跨显示器）为 BitmapSource。
/// 使用 GDI BitBlt；后续可替换为 Graphics Capture API 以支持单窗口/无闪烁。
/// </summary>
public static class ScreenCapturer
{
    /// <summary>捕获整个虚拟屏幕（物理像素）</summary>
    public static BitmapSource CaptureVirtualScreen()
    {
        var (x, y, width, height) = NativeMethods.GetVirtualScreen();
        return CaptureRegion(x, y, width, height);
    }

    /// <summary>捕获指定物理像素矩形区域</summary>
    public static BitmapSource CaptureRegion(int x, int y, int width, int height)
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

            // 转为 WPF BitmapSource（DPI 96 由 manifest 的 PerMonitorV2 保证坐标一致）
            var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
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
}
