using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipPin.Core.Services;

/// <summary>
/// 区域截图框选遮罩窗（单屏版）：覆盖一块显示器，无边框置顶。
/// 结构（自下而上）：底图（清晰）→ 半透明暗化层 → 清晰图层（裁剪出选区/悬停窗口）→ 选区边框 → 尺寸提示。
/// 坐标系：所有交互坐标均为该屏的 DIP（= 物理像素 / 缩放系数），与 WPF 渲染一致，不会放大错位。
/// 交互：悬停自动吸附窗口（智能窗口识别）；点击直接选中该窗口；拖拽超过阈值转为手动框选。
/// </summary>
public class CaptureOverlayWindow : Window
{
    private readonly BitmapSource _screenImage; // 该屏位图（已带真实 DPI）
    private readonly double _scale;             // 该屏缩放系数（Dpi/96）
    private readonly Rect _dipBounds;           // 该屏在虚拟屏中的 DIP 矩形
    private readonly bool _windowSnap;

    private readonly Canvas _canvas;
    private readonly RectangleGeometry _clearClip; // 清晰图层的裁剪区域（"挖洞"）
    private readonly Border _selection;     // 选区高亮框
    private readonly TextBlock _sizeTip;    // 尺寸提示

    // 可选窗口矩形列表（本屏 DIP 坐标）
    private readonly List<Rect>? _windowRects;

    private bool _dragging;
    private Point _dragStart;               // 按下点（本屏 DIP 坐标）
    private Rect _dragStartRect;            // 按下时的选区（吸附窗口时为窗口矩形）
    private bool _snapped;                  // 当前按下是否来自窗口吸附
    private Rect? _hoverRect;               // 当前悬停建议的窗口矩形

    // ---- 确认阶段（松开鼠标后、点 √ 前：可拖动/调整选区） ----
    private bool _confirming;               // 是否处于确认阶段
    private Rect _selectionRect;            // 当前选区（本屏 DIP）
    private Border? _toolbar;               // 确认工具栏（✕ 取消 / √ 截图）
    private bool _adjusting;                // 正在拖动移动/调整选区
    private Point _adjustStart;             // 调整起始点
    private Rect _adjustStartRect;          // 调整起始选区
    private HandleHit _adjustMode;          // 当前调整类型

    // 拖拽转为手动的阈值（DIP）：仅在窗口上按下时用于区分"点击选窗口"与"拖动自定义框选"，
    // 按下后稍一拖动（超过鼠标点击抖动范围）即彻底退出吸附、完全手动
    private const double SnapDragThreshold = 2;
    // 把手命中带宽（DIP）
    private const double CornerTolerance = 10;
    private const double EdgeTolerance = 6;
    // 选区最小尺寸（DIP）
    private const double MinSelection = 4;

    /// <summary>把手/选区命中类型</summary>
    private enum HandleHit
    {
        Outside, Inside,
        CornerNW, CornerNE, CornerSW, CornerSE,
        EdgeN, EdgeS, EdgeW, EdgeE,
    }

    /// <summary>框选完成（参数为虚拟屏 DIP 坐标矩形）</summary>
    public event Action<Rect>? RegionSelected;
    /// <summary>用户取消</summary>
    public event Action? Cancelled;

    /// <summary>本屏缩放系数（Dpi/96），供外层把 DIP 选区换算回物理像素</summary>
    public double Scale => _scale;

    public CaptureOverlayWindow(
        BitmapSource screenImage,
        Rect dipBounds,           // 该屏在虚拟屏中的 DIP 矩形（用于窗口定位与窗口矩形转换）
        double scale,             // 该屏缩放系数（Dpi/96）
        double maskOpacity = 0.4,
        bool windowSnap = true)
    {
        _screenImage = screenImage;
        _dipBounds = dipBounds;
        _scale = scale;
        _windowSnap = windowSnap;

        // ---- 窗口外观：覆盖本屏、无边框、置顶 ----
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;

        // DIP 尺寸 = 物理像素 / 缩放；WPF 按本屏缩放渲染后即填满物理像素，1:1 不放大
        double dipW = screenImage.PixelWidth / scale;
        double dipH = screenImage.PixelHeight / scale;
        Left = dipBounds.X;
        Top = dipBounds.Y;
        Width = dipW;
        Height = dipH;

        // ---- 预先枚举可见窗口矩形（转换为虚拟屏 DIP → 本屏 DIP）----
        if (_windowSnap)
        {
            var origin = new Point(dipBounds.X, dipBounds.Y);
            _windowRects = Native.NativeMethods.GetVisibleWindowRects()
                .Select(r => new Rect(
                    r.X / scale - origin.X,
                    r.Y / scale - origin.Y,
                    r.Width / scale,
                    r.Height / scale))
                .ToList();
        }

        // ---- 内容层（尺寸均为本屏 DIP）----
        _canvas = new Canvas { Width = dipW, Height = dipH };

        // 1) 底图（清晰，最底层）
        var background = new Image
        {
            Source = screenImage,
            Stretch = Stretch.None,
            Width = dipW,
            Height = dipH,
        };
        _canvas.Children.Add(background);

        // 2) 半透明暗化层
        byte alpha = (byte)Math.Clamp(maskOpacity * 255, 0, 255);
        var dim = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
            Width = dipW,
            Height = dipH,
        };
        _canvas.Children.Add(dim);

        // 3) 清晰图层：与底图相同，但只露出裁剪区域（选区/悬停窗口）
        _clearClip = new RectangleGeometry();
        var clear = new Image
        {
            Source = screenImage,
            Stretch = Stretch.None,
            Width = dipW,
            Height = dipH,
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
        if (_dragging) return; // 防触摸板/触笔提升事件重发 Down

        // 确认阶段：点中选区/把手 → 移动或调整；点选区外 → 取消选区回到初始等待状态
        if (_confirming && !_adjusting)
        {
            var hit = HitHandle(pos);
            if (hit != HandleHit.Outside)
            {
                _adjusting = true;
                _adjustMode = hit;
                _adjustStart = pos;
                _adjustStartRect = _selectionRect;
                _canvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            // 只取消选区，不立即开始新框选：本次点击的松手不再被判定为
            // "点击选窗口"（否则点一下外部选区会突变成附近窗口）
            ExitConfirm();
            e.Handled = true;
            return;
        }

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

        // 调整选区中：按把手类型更新矩形
        if (_adjusting)
        {
            ApplyAdjust(pos);
            UpdateSelectionVisual(_selectionRect);
            PlaceToolbar();
            return;
        }

        // 确认阶段悬停：按命中位置切换光标（移动/缩放提示）
        if (_confirming)
        {
            Cursor = CursorFor(HitHandle(pos));
            return;
        }

        if (!_dragging)
        {
            // 悬停：更新窗口吸附建议
            UpdateHover(pos);
            return;
        }

        // 拖动开始：彻底清除吸附状态，本次按下后续一律手动框选
        if (_snapped && (pos - _dragStart).Length > SnapDragThreshold)
        {
            _snapped = false;
            _hoverRect = null;
        }

        var rect = _snapped ? _dragStartRect : Normalize(_dragStart, pos);
        UpdateSelectionVisual(rect);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 结束选区调整：停留在确认阶段（等 √ / ✕ / 继续调整）
        if (_adjusting)
        {
            _adjusting = false;
            _canvas.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            return;
        }

        if (!_dragging) return;
        _dragging = false;

        var pos = e.GetPosition(_canvas);
        bool dragged = (pos - _dragStart).Length > SnapDragThreshold;

        Rect rect;
        if (dragged)
        {
            // 拖动过：只依据按下点→松手点计算矩形，不参考任何窗口（绝不吸附）
            rect = Normalize(_dragStart, pos);
            if (rect.Width < MinSelection || rect.Height < MinSelection)
            {
                _canvas.ReleaseMouseCapture(); // 回到悬停等待状态
                return; // 误触，继续等待
            }
        }
        else
        {
            // 纯点击（无拖动）：实时命中松手位置的窗口才"点击选窗口"
            var hover = HitWindow(pos);
            if (hover == null)
            {
                _canvas.ReleaseMouseCapture();
                return;
            }
            rect = hover.Value;
        }

        // 先进入确认状态，再释放鼠标捕获（顺序至关重要）：
        // ReleaseMouseCapture 会同步合成一次 MouseMove（无捕获状态重路由当前位置），
        // 若此刻仍处于"非确认"状态，该事件会落入悬停分支 UpdateHover，
        // 把蓝框重绘成附近窗口 —— 这正是"松手瞬间选区跳变"的根因
        _selectionRect = rect;
        _confirming = true;
        _canvas.ReleaseMouseCapture();

        // 进入确认阶段：显示 ✕/√ 工具栏，用户确认后才真正截取
        EnterConfirm();
    }

    // ---------------- 确认阶段 ----------------
    /// <summary>松开鼠标后进入确认阶段：选区保持可拖动/调整，点 √ 才截取</summary>
    private void EnterConfirm()
    {
        _confirming = true;
        _toolbar ??= BuildConfirmToolbar();
        if (!_canvas.Children.Contains(_toolbar))
            _canvas.Children.Add(_toolbar);
        PlaceToolbar();
        Cursor = Cursors.Arrow;
    }

    /// <summary>退出确认阶段（点击选区外重新框选 / 确认 / 取消）</summary>
    private void ExitConfirm()
    {
        _confirming = false;
        _adjusting = false;
        _hoverRect = null; // 清掉旧悬停建议，重新框选时重新判定
        if (_toolbar != null)
            _canvas.Children.Remove(_toolbar);
        Cursor = Cursors.Cross;
        // 清除选区视觉（挖洞恢复全暗），重新框选时再显示
        _clearClip.Rect = Rect.Empty;
        _selection.Visibility = Visibility.Collapsed;
        _sizeTip.Visibility = Visibility.Collapsed;
    }

    /// <summary>构建 ✕/√ 确认工具栏</summary>
    private Border BuildConfirmToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var cancel = MakeToolbarButton("✕", "取消 (Esc)");
        cancel.Click += (_, _) => Cancel();

        var confirm = MakeToolbarButton("✓", "确认截图");
        confirm.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        confirm.Click += (_, _) => ConfirmSelection();

        panel.Children.Add(cancel);
        panel.Children.Add(confirm);

        return new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 4, 6, 4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4, Color = Colors.Black
            },
        };
    }

    private static Button MakeToolbarButton(string glyph, string tooltip) => new()
    {
        Content = glyph,
        ToolTip = tooltip,
        Foreground = Brushes.White,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        FontSize = 15,
        MinWidth = 34,
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(1),
        Cursor = Cursors.Hand,
    };

    /// <summary>工具栏跟随选区：默认在选区下方居中，屏幕放不下则放上方，水平方向钳制在屏内</summary>
    private void PlaceToolbar()
    {
        if (_toolbar == null) return;
        const double tbW = 96, tbH = 36, gap = 8;
        double x = _selectionRect.X + (_selectionRect.Width - tbW) / 2;
        double y = _selectionRect.Bottom + gap;
        if (y + tbH > _canvas.Height) y = _selectionRect.Y - tbH - gap; // 放上方
        if (y < 0) y = 0;
        x = Math.Clamp(x, 0, Math.Max(0, _canvas.Width - tbW));
        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    /// <summary>√：以当前选区触发截图（本屏 DIP → 虚拟屏 DIP）</summary>
    private void ConfirmSelection()
    {
        if (!_confirming) return;
        ExitConfirm();
        var virtDip = new Rect(
            _selectionRect.X + _dipBounds.X,
            _selectionRect.Y + _dipBounds.Y,
            _selectionRect.Width,
            _selectionRect.Height);
        RegionSelected?.Invoke(virtDip);
        // 不在此 Close，由外层统一关闭所有屏 overlay，避免闪烁/顺序问题
    }

    // ---------------- 选区调整 ----------------
    /// <summary>命中测试：点在选区的角/边/内部还是外部（本屏 DIP）</summary>
    private HandleHit HitHandle(Point pos)
    {
        var r = _selectionRect;
        // 角（优先，带宽较大）
        if (Math.Abs(pos.X - r.Left) <= CornerTolerance && Math.Abs(pos.Y - r.Top) <= CornerTolerance)
            return HandleHit.CornerNW;
        if (Math.Abs(pos.X - r.Right) <= CornerTolerance && Math.Abs(pos.Y - r.Top) <= CornerTolerance)
            return HandleHit.CornerNE;
        if (Math.Abs(pos.X - r.Left) <= CornerTolerance && Math.Abs(pos.Y - r.Bottom) <= CornerTolerance)
            return HandleHit.CornerSW;
        if (Math.Abs(pos.X - r.Right) <= CornerTolerance && Math.Abs(pos.Y - r.Bottom) <= CornerTolerance)
            return HandleHit.CornerSE;
        // 边
        if (Math.Abs(pos.Y - r.Top) <= EdgeTolerance && pos.X >= r.Left && pos.X <= r.Right)
            return HandleHit.EdgeN;
        if (Math.Abs(pos.Y - r.Bottom) <= EdgeTolerance && pos.X >= r.Left && pos.X <= r.Right)
            return HandleHit.EdgeS;
        if (Math.Abs(pos.X - r.Left) <= EdgeTolerance && pos.Y >= r.Top && pos.Y <= r.Bottom)
            return HandleHit.EdgeW;
        if (Math.Abs(pos.X - r.Right) <= EdgeTolerance && pos.Y >= r.Top && pos.Y <= r.Bottom)
            return HandleHit.EdgeE;
        // 内部
        if (r.Contains(pos)) return HandleHit.Inside;
        return HandleHit.Outside;
    }

    /// <summary>按把手类型把鼠标位置应用到选区（钳制在屏幕内、保持最小尺寸）</summary>
    private void ApplyAdjust(Point pos)
    {
        double l = _adjustStartRect.Left, t = _adjustStartRect.Top;
        double r = _adjustStartRect.Right, b = _adjustStartRect.Bottom;

        switch (_adjustMode)
        {
            case HandleHit.Inside:
                double x = Math.Clamp(
                    _adjustStartRect.X + (pos.X - _adjustStart.X),
                    0, Math.Max(0, _canvas.Width - _adjustStartRect.Width));
                double y = Math.Clamp(
                    _adjustStartRect.Y + (pos.Y - _adjustStart.Y),
                    0, Math.Max(0, _canvas.Height - _adjustStartRect.Height));
                _selectionRect = new Rect(x, y, _adjustStartRect.Width, _adjustStartRect.Height);
                return;
            case HandleHit.CornerNW:
                l = Math.Min(pos.X, r - MinSelection); t = Math.Min(pos.Y, b - MinSelection);
                break;
            case HandleHit.CornerNE:
                r = Math.Max(pos.X, l + MinSelection); t = Math.Min(pos.Y, b - MinSelection);
                break;
            case HandleHit.CornerSW:
                l = Math.Min(pos.X, r - MinSelection); b = Math.Max(pos.Y, t + MinSelection);
                break;
            case HandleHit.CornerSE:
                r = Math.Max(pos.X, l + MinSelection); b = Math.Max(pos.Y, t + MinSelection);
                break;
            case HandleHit.EdgeN:
                t = Math.Clamp(pos.Y, 0, b - MinSelection);
                break;
            case HandleHit.EdgeS:
                b = Math.Clamp(pos.Y, t + MinSelection, _canvas.Height);
                break;
            case HandleHit.EdgeW:
                l = Math.Clamp(pos.X, 0, r - MinSelection);
                break;
            case HandleHit.EdgeE:
                r = Math.Clamp(pos.X, l + MinSelection, _canvas.Width);
                break;
        }

        // 整体钳制在屏幕范围内
        l = Math.Max(0, l); t = Math.Max(0, t);
        r = Math.Min(_canvas.Width, r); b = Math.Min(_canvas.Height, b);
        _selectionRect = new Rect(l, t, r - l, b - t);
    }

    /// <summary>按命中位置返回对应光标</summary>
    private static Cursor CursorFor(HandleHit hit) => hit switch
    {
        HandleHit.CornerNW or HandleHit.CornerSE => Cursors.SizeNWSE,
        HandleHit.CornerNE or HandleHit.CornerSW => Cursors.SizeNESW,
        HandleHit.EdgeN or HandleHit.EdgeS => Cursors.SizeNS,
        HandleHit.EdgeW or HandleHit.EdgeE => Cursors.SizeWE,
        HandleHit.Inside => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };

    // ---------------- 悬停窗口识别 ----------------
    /// <summary>返回包含 pos 的最小窗口矩形（嵌套时命中子窗口）；无则 null</summary>
    private Rect? HitWindow(Point pos)
    {
        if (_windowRects == null) return null;
        Rect? found = null;
        foreach (var r in _windowRects)
        {
            if (r.Contains(pos) && (found == null || r.Width * r.Height < found.Value.Width * found.Value.Height))
                found = r;
        }
        return found;
    }

    private void UpdateHover(Point pos)
    {
        var found = HitWindow(pos);

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

        // 尺寸提示（贴选区上方，超出屏幕则放下方）；显示物理像素尺寸更直观
        _sizeTip.Visibility = Visibility.Visible;
        _sizeTip.Text = $"{(int)(rect.Width * _scale)} × {(int)(rect.Height * _scale)}";
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
        Cancelled?.Invoke();
        // 不在此 Close，由外层统一关闭
    }

    /// <summary>显示遮罩并进入框选</summary>
    public void ShowOverlay() => Show();
}
