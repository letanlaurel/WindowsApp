using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipPin.Core.Annotations;

/// <summary>
/// 标注基类：所有标注为矢量对象，可序列化、可二次编辑。
/// 标注在"图像像素坐标系"中定义，渲染时按缩放系数换算。
/// </summary>
public abstract class Annotation
{
    public Guid Id { get; } = Guid.NewGuid();
    public Color StrokeColor { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 2.0;
    public bool HasShadow { get; set; } = false;

    /// <summary>在给定缩放系数下渲染到 DrawingContext</summary>
    public abstract void Render(DrawingContext dc, double scale);

    /// <summary>标注包围盒（图像坐标）</summary>
    public abstract Rect GetBounds();

    /// <summary>平移标注（用于选中后拖动）</summary>
    public abstract void Translate(Vector delta);
}

/// <summary>文字标注</summary>
public class TextAnnotation : Annotation
{
    public string Text { get; set; } = string.Empty;
    public Point Position { get; set; }
    public double FontSize { get; set; } = 16;
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public bool Bold { get; set; } = false;

    public override void Render(DrawingContext dc, double scale)
    {
        var typeface = new Typeface(
            new FontFamily(FontFamily),
            FontStyles.Normal,
            Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        // 文本渲染固定使用 DPI 96，避免依赖 Application 主窗体
        var text = new FormattedText(
            Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize * scale,
            new SolidColorBrush(StrokeColor),
            96.0);

        dc.DrawText(text, new Point(Position.X * scale, Position.Y * scale));
    }

    public override Rect GetBounds() =>
        new(Position, new Size(FontSize * Text.Length * 0.6, FontSize * 1.4)); // 粗略估计

    public override void Translate(Vector delta) => Position += delta;
}

/// <summary>箭头标注</summary>
public class ArrowAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public override void Render(DrawingContext dc, double scale)
    {
        var pen = new Pen(new SolidColorBrush(StrokeColor), Thickness * scale);
        var s = new Point(Start.X * scale, Start.Y * scale);
        var e = new Point(End.X * scale, End.Y * scale);

        dc.DrawLine(pen, s, e);

        // 箭头头部：沿 End 方向绘制两条短斜线
        var dir = e - s;
        if (dir.Length < 0.001) return;
        dir.Normalize();
        double headLen = 12 * scale;
        const double angle = 25 * Math.PI / 180; // 张角

        var left = Rotate(dir, Math.PI - angle) * headLen;
        var right = Rotate(dir, Math.PI + angle) * headLen;
        dc.DrawLine(pen, e, e + left);
        dc.DrawLine(pen, e, e + right);
    }

    private static Vector Rotate(Vector v, double radians)
    {
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    public override Rect GetBounds()
    {
        var r = new Rect(Start, End);
        r.Inflate(Thickness, Thickness);
        return r;
    }

    public override void Translate(Vector delta) { Start += delta; End += delta; }
}

/// <summary>矩形标注</summary>
public class RectAnnotation : Annotation
{
    public Rect Bounds { get; set; }
    public Color? Fill { get; set; }

    public override void Render(DrawingContext dc, double scale)
    {
        var pen = new Pen(new SolidColorBrush(StrokeColor), Thickness * scale);
        Brush? fill = Fill.HasValue ? new SolidColorBrush(Fill.Value) : null;
        var r = new Rect(Bounds.X * scale, Bounds.Y * scale,
                         Bounds.Width * scale, Bounds.Height * scale);
        dc.DrawRectangle(fill, pen, r);
    }

    public override Rect GetBounds() { var r = Bounds; r.Inflate(Thickness, Thickness); return r; }
    public override void Translate(Vector delta) => Bounds = new Rect(Bounds.Location + delta, Bounds.Size);
}

/// <summary>椭圆标注</summary>
public class EllipseAnnotation : Annotation
{
    public Rect Bounds { get; set; }
    public Color? Fill { get; set; }

    public override void Render(DrawingContext dc, double scale)
    {
        var pen = new Pen(new SolidColorBrush(StrokeColor), Thickness * scale);
        Brush? fill = Fill.HasValue ? new SolidColorBrush(Fill.Value) : null;
        var center = new Point(
            (Bounds.X + Bounds.Width / 2) * scale,
            (Bounds.Y + Bounds.Height / 2) * scale);
        dc.DrawEllipse(fill, pen, center, Bounds.Width / 2 * scale, Bounds.Height / 2 * scale);
    }

    public override Rect GetBounds() { var r = Bounds; r.Inflate(Thickness, Thickness); return r; }
    public override void Translate(Vector delta) => Bounds = new Rect(Bounds.Location + delta, Bounds.Size);
}

/// <summary>画笔（自由绘制）标注</summary>
public class PenAnnotation : Annotation
{
    public List<Point> Points { get; } = new();

    public override void Render(DrawingContext dc, double scale)
    {
        if (Points.Count < 2) return;
        var pen = new Pen(new SolidColorBrush(StrokeColor), Thickness * scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        for (int i = 1; i < Points.Count; i++)
        {
            dc.DrawLine(pen,
                new Point(Points[i - 1].X * scale, Points[i - 1].Y * scale),
                new Point(Points[i].X * scale, Points[i].Y * scale));
        }
    }

    public override Rect GetBounds()
    {
        if (Points.Count == 0) return Rect.Empty;
        double minX = Points.Min(p => p.X), minY = Points.Min(p => p.Y);
        double maxX = Points.Max(p => p.X), maxY = Points.Max(p => p.Y);
        var r = new Rect(minX, minY, maxX - minX, maxY - minY);
        r.Inflate(Thickness, Thickness);
        return r;
    }

    public override void Translate(Vector delta)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += delta;
    }
}

/// <summary>序号标记（自动递增步骤编号）</summary>
public class NumberAnnotation : Annotation
{
    public int Number { get; set; } = 1;
    public Point Center { get; set; }
    public double Radius { get; set; } = 14;

    public override void Render(DrawingContext dc, double scale)
    {
        var center = new Point(Center.X * scale, Center.Y * scale);
        double r = Radius * scale;

        // 圆形底 + 数字
        dc.DrawEllipse(new SolidColorBrush(StrokeColor), null, center, r, r);
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var text = new FormattedText(
            Number.ToString(),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            Radius * 1.2 * scale,
            Brushes.White,
            96.0);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    public override Rect GetBounds()
    {
        var r = new Rect(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
        return r;
    }

    public override void Translate(Vector delta) => Center += delta;
}

/// <summary>
/// 马赛克标注：像素级处理——构造时对底图指定区域做块平均并缓存，
/// 渲染时把缓存块画回（可被移动）。块大小由工具条粗细档位映射。
/// </summary>
public class MosaicAnnotation : Annotation
{
    /// <summary>原始区域（图像坐标，构造后不变）</summary>
    public Rect Bounds { get; }
    public int BlockSize { get; }

    private readonly BitmapSource? _patch; // 像素化后的块缓存
    private Vector _offset;                // 移动累计偏移

    public MosaicAnnotation(BitmapSource baseImage, Rect bounds, int blockSize)
    {
        Bounds = bounds;
        BlockSize = blockSize;
        _patch = Pixelator.Pixelate(baseImage, bounds, blockSize);
    }

    public override void Render(DrawingContext dc, double scale)
    {
        if (_patch == null) return;
        var origin = Bounds.Location + _offset;
        dc.DrawImage(_patch, new Rect(
            origin.X * scale, origin.Y * scale,
            Bounds.Width * scale, Bounds.Height * scale));
    }

    public override Rect GetBounds() => new(Bounds.Location + _offset, Bounds.Size);

    public override void Translate(Vector delta) => _offset += delta;
}

/// <summary>高亮标注：半透明色块覆盖（荧光笔效果）</summary>
public class HighlightAnnotation : Annotation
{
    public Rect Bounds { get; set; }

    public override void Render(DrawingContext dc, double scale)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            0x66, StrokeColor.R, StrokeColor.G, StrokeColor.B));
        dc.DrawRectangle(brush, null, new Rect(
            Bounds.X * scale, Bounds.Y * scale,
            Bounds.Width * scale, Bounds.Height * scale));
    }

    public override Rect GetBounds() => Bounds;

    public override void Translate(Vector delta) => Bounds = new Rect(Bounds.Location + delta, Bounds.Size);
}
