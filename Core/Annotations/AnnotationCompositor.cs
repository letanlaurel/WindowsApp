using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipPin.Core.Annotations;

/// <summary>
/// 标注合成渲染：把底图与一组矢量标注烘焙成最终的位图（物理像素 1:1）。
/// </summary>
public static class AnnotationCompositor
{
    /// <summary>
    /// 合成最终图片。
    /// </summary>
    /// <param name="baseImage">底图（截图），物理像素</param>
    /// <param name="annotations">标注列表（在图像坐标系，与底图像素对齐）</param>
    /// <param name="dpi">输出 DPI（默认 96，与截图坐标系一致）</param>
    public static BitmapSource Render(BitmapSource baseImage, IEnumerable<Annotation> annotations, double dpi = 96)
    {
        int width = baseImage.PixelWidth;
        int height = baseImage.PixelHeight;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 1. 底图
            dc.DrawImage(baseImage, new Rect(0, 0, width, height));

            // 2. 逐个渲染标注（scale = 1，因为坐标系已与像素对齐）
            foreach (var ann in annotations)
                ann.Render(dc, 1.0);
        }

        var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
