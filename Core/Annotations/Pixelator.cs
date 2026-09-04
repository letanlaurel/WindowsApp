using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipPin.Core.Annotations;

/// <summary>
/// 马赛克（像素化）处理：对底图指定区域做块平均，输出与原区域同尺寸的位图。
/// 直接在字节层面回填块颜色，等价于最近邻放大，避免缩放插值模式问题。
/// </summary>
internal static class Pixelator
{
    /// <summary>
    /// 像素化底图指定区域。
    /// </summary>
    /// <param name="source">底图</param>
    /// <param name="region">区域（图像坐标）</param>
    /// <param name="blockSize">马赛克块大小（像素）</param>
    public static BitmapSource? Pixelate(BitmapSource source, Rect region, int blockSize)
    {
        // 裁剪并钳制到图像范围内
        int x = Math.Max(0, (int)Math.Floor(region.X));
        int y = Math.Max(0, (int)Math.Floor(region.Y));
        int w = Math.Min(source.PixelWidth - x, (int)Math.Ceiling(region.Width));
        int h = Math.Min(source.PixelHeight - y, (int)Math.Ceiling(region.Height));
        if (w <= 0 || h <= 0) return null;

        // 统一转为 BGRA32 四字节格式，便于字节处理
        var cropped = new FormatConvertedBitmap(
            new CroppedBitmap(source, new Int32Rect(x, y, w, h)),
            PixelFormats.Bgra32, null, 0);

        if (blockSize <= 1)
        {
            cropped.Freeze();
            return cropped;
        }

        var pixels = new byte[w * h * 4];
        cropped.CopyPixels(pixels, w * 4, 0);

        // 块平均后整块回填
        var result = new byte[pixels.Length];
        for (int by = 0; by < h; by += blockSize)
        {
            int bh = Math.Min(blockSize, h - by);
            for (int bx = 0; bx < w; bx += blockSize)
            {
                int bw = Math.Min(blockSize, w - bx);
                long b = 0, g = 0, r = 0, a = 0;
                for (int py = by; py < by + bh; py++)
                for (int px = bx; px < bx + bw; px++)
                {
                    int i = (py * w + px) * 4;
                    b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; a += pixels[i + 3];
                }
                int count = bw * bh;
                byte bb = (byte)(b / count), bg = (byte)(g / count),
                     br = (byte)(r / count), ba = (byte)(a / count);
                for (int py = by; py < by + bh; py++)
                for (int px = bx; px < bx + bw; px++)
                {
                    int i = (py * w + px) * 4;
                    result[i] = bb; result[i + 1] = bg; result[i + 2] = br; result[i + 3] = ba;
                }
            }
        }

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), result, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }
}
