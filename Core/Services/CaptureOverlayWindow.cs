using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>
/// 区域截图框选遮罩窗：全屏无边框置顶。
/// 结构（自下而上）：底图（清晰）→ 半透明暗化层 → 清晰图层（裁剪出选区/悬停窗口）→ 选区边框 → 尺寸提示。
/// 交互：悬停自动吸附窗口（智能窗口识别）；点击直接选中该窗口；拖拽超过阈值转为手动框选。
/// </summary>
public class CaptureOverlayWindow : Window
{
    private readonly BitmapSource _screenImage;
    private readonly Point _screenOrigin;   // 虚拟屏幕左上角物理坐标（可能为负）
    private readonly bool _windowSnap;

    private readonly Canvas _canvas;
    private readonly RectangleGeometry _clearClip; // 清晰图层的裁剪区域（"挖洞"）
    private readonly Border _selection;     // 选区高亮框
    private readonly TextBlock _sizeTip;    // 尺寸提示

    // 可选窗口矩形列表（画布坐标）
    private readonly List<Rect>? _windowRects;

    private bool _dragging;
    private Point _dragStart;               // 按下点（画布坐标）
    private Rect _dragStartRect;            // 按下时的选区（吸附窗口时为窗口矩形）
    private bool _snapped;                  // 当前按下是否来自窗口吸附
    private Rect? _hoverRect;               // 当前悬停建议的窗口矩形

    // 拖拽转为手动的阈值（像素）
    private const double SnapDragThreshold = 8;

    /// <summary>框选完成（参数为屏幕物理坐标矩形）</summary>
    public event Action<Rect>? RegionSelected;
    /// <summary>用户取消</summary>
    public event Action? Cancelled;

    public CaptureOverlayWindow(
        BitmapSource screenImage,
        Point screenOrigin,
        double maskOpacity = 0.4,
        bool windowSnap = true)
    {
        _screenImage = screenImage;
        _screenOrigin = screenOrigin;
        _windowSnap = windowSnap;

        // ---- 窗口外观：覆盖整个虚拟屏幕、无边框、置顶 ----
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;

        Left = screenOrigin.X;
        Top = screenOrigin.Y;
        Width = screenImage.PixelWidth;
        Height = screenImage.PixelHeight;

        // ---- 预先枚举可见窗口矩形（转换为画布坐标）----
        if (_windowSnap)
        {
            _windowRects = NativeMethods.GetVisibleWindowRects()
                .Select(r => new Rect(r.X - screenOrigin.X, r.Y - screenOrigin.Y, r.Width, r.Height))
                .ToList();
        }

        // ---- 内容层 ----
        _canvas = new Canvas();

        // 1) 底图（清晰，最底层）
        var background = new Image
        {
            Source = screenImage,
            Stretch = Stretch.None,
            Width = screenImage.PixelWidth,
            Height = screenImage.PixelHeight,
        };
        _canvas.Children.Add(background);

        // 2) 半透明暗化层
        byte alpha = (byte)Math.Clamp(maskOpacity * 255, 0, 255);
        var dim = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
            Width = screenImage.PixelWidth,
            Height = screenImage.PixelHeight,
        };
        _canvas.Children.Add(dim);

        // 3) 清晰图层：与底图相同，但只露出裁剪区域（选区/悬停窗口）
        _clearClip = new RectangleGeometry();
        var clear = new Image
        {
            Source = screenImage,
            Stretch = Stretch.None,
            Width = screenImage.PixelWidth,
            Height = screenImage.PixelHeight,
            Clip = _clearClip,
        };
        _canvas.Children.Add(clear);

        // 4) 选区边框
        _selection = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            BorderThickness = new Thickness(1.5),
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_selection);

        // 5) 尺寸提示
        _sizeTip = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x00, 0x00)),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_sizeTip);

        Content = _canvas;

        // ---- 交互 ----
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancel();
        };
        Focusable = true;
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    // ---------------- 交互 ----------------
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        _dragging = true;
        _dragStart = pos;
        _dragStartRect = _hoverRect ?? new Rect(pos, pos);
        _snapped = _hoverRect.HasValue;
        _canvas.CaptureMouse();

        if (_snapped)
            UpdateSelectionVisual(_dragStartRect); // 按下即显示窗口建议
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);

        if (!_dragging)
        {
            // 悬停：更新窗口吸附建议
            UpdateHover(pos);
            return;
        }

        Rect rect;
        if (_snapped)
        {
            // 吸附模式下拖远则转为手动框选
            if ((pos - _dragStart).Length > SnapDragThreshold)
            {
                _snapped = false;
                rect = Normalize(_dragStart, pos);
            }
            else
            {
                rect = _dragStartRect;
            }
        }
        else
        {
            rect = Normalize(_dragStart, pos);
        }
        UpdateSelectionVisual(rect);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleaseMouseCapture();

        var pos = e.GetPosition(_canvas);
        var rect = _snapped ? _dragStartRect : Normalize(_dragStart, pos);

        // 手动框选过小视为误触，继续等待
        if (!_snapped && (rect.Width < 4 || rect.Height < 4))
            return;

        // 换算为屏幕物理坐标
        var screenRect = new Rect(
            rect.X + _screenOrigin.X,
            rect.Y + _screenOrigin.Y,
            rect.Width,
            rect.Height);

        Close();
        RegionSelected?.Invoke(screenRect);
    }

    // ---------------- 悬停窗口识别 ----------------
    private void UpdateHover(Point pos)
    {
        Rect? found = null;
        if (_windowRects != null)
        {
            // 取包含光标的最小窗口（嵌套时命中子窗口）
            foreach (var r in _windowRects)
            {
                if (r.Contains(pos) && (found == null || r.Width * r.Height < found.Value.Width * found.Value.Height))
                    found = r;
            }
        }

        if (found != _hoverRect)
        {
            _hoverRect = found;
            if (found.HasValue)
                UpdateSelectionVisual(found.Value); // 悬停即预览窗口选区
        }
    }

    // ---------------- 视觉更新 ----------------
    private void UpdateSelectionVisual(Rect rect)
    {
        // 清晰图层"挖洞"
        _clearClip.Rect = rect;

        // 选区边框
        _selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selection, rect.X);
        Canvas.SetTop(_selection, rect.Y);
        _selection.Width = rect.Width;
        _selection.Height = rect.Height;

        // 尺寸提示（贴选区上方，超出屏幕则放下方）
        _sizeTip.Visibility = Visibility.Visible;
        _sizeTip.Text = $"{(int)rect.Width} × {(int)rect.Height}";
        Canvas.SetLeft(_sizeTip, rect.X);
        double tipY = rect.Y - 26 < 0 ? rect.Y + rect.Height + 6 : rect.Y - 26;
        Canvas.SetTop(_sizeTip, tipY);
    }

    private static Rect Normalize(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void Cancel()
    {
        Close();
        Cancelled?.Invoke();
    }

    /// <summary>显示遮罩并进入框选</summary>
    public void ShowOverlay() => Show();
}
