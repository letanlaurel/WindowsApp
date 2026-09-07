using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnipPin.Core.Annotations;

namespace SnipPin.Core.Services;

/// <summary>
/// 区域截图框选遮罩窗（单屏版）：覆盖一块显示器，无边框置顶。
/// 结构（自下而上）：底图（清晰）→ 半透明暗化层 → 清晰图层（裁剪出选区/悬停窗口）→ 选区边框 → 尺寸提示
///                   → 标注层 → 标注工具栏。
/// 坐标系：交互坐标为该屏 DIP；标注坐标统一为「选区局部物理像素」（与合成裁剪图 1:1 对齐）。
/// 流程（微信式）：悬停吸附窗口/点击选窗口/拖动框选 → 松手进入标注阶段
/// （完整工具栏 + 可移动选区 + 直接在选区上标注）→ 工具栏输出（完成/钉住/保存/复制）或 ✕ 取消。
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

    // ---- 框选交互 ----
    private bool _dragging;
    private Point _dragStart;               // 按下点（本屏 DIP 坐标）
    private Rect _dragStartRect;            // 按下时的选区（吸附窗口时为窗口矩形）
    private bool _snapped;                  // 当前按下是否来自窗口吸附
    private Rect? _hoverRect;               // 当前悬停建议的窗口矩形

    // ---- 取色放大镜（框选/悬停时跟随鼠标：局部放大 + 坐标 + RGB/HEX） ----
    private Border? _magnifier;             // 放大镜面板
    private MagnifierVisual? _magVisual;    // 放大区渲染
    private TextBlock? _magPos;             // 坐标文本
    private TextBlock? _magColor;           // HEX 文本
    private TextBlock? _magRgb;             // RGB 文本
    private System.Windows.Shapes.Rectangle? _magSwatch; // 色块
    private WriteableBitmap? _magBitmap;    // 放大区位图（复用，零分配更新）
    private byte[] _magBuffer = Array.Empty<byte>();     // 采样缓冲（复用）
    private const int MagCells = 22;        // 放大网格边数（取中心周围 22x22 像素）

    // ---- 标注阶段（松开鼠标后：可移动选区 + 直接标注 + 工具栏输出） ----
    private bool _confirming;               // 是否处于标注阶段
    private Rect _selectionRect;            // 当前选区（本屏 DIP）
    private Canvas? _annotLayer;            // 标注层（选区局部 DIP 显示）
    private AnnotationToolbar? _toolbar;    // 完整标注工具栏
    private BitmapSource? _cropBase;        // 选区物理裁剪图（马赛克采样源，惰性生成）
    private readonly List<Annotation> _annotations = new(); // 标注（选区局部物理像素坐标）
    private readonly UndoRedoStack _history = new();

    // ---- 标注绘制状态 ----
    private AnnotationTool _tool = AnnotationTool.None;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _thickness = 2;
    private bool _drawing;
    private Point _startPoint;
    private Annotation? _active;
    private Annotation? _selected;   // 选中待移动的标注
    private Vector _moveTotal;       // 本次移动累计位移
    private int _numberCounter = 1;
    private TextBox? _textInput;     // 文字输入框（T 工具）
    private Border? _previewRect;    // 马赛克拖拽预览框

    // ---- 选区调整 ----
    private bool _adjusting;                // 正在拖动移动选区
    private Point _adjustStart;             // 调整起始点
    private Rect _adjustStartRect;          // 调整起始选区
    private HandleHit _adjustMode;          // 当前调整类型

    // 拖拽转为手动的阈值（DIP）：仅在窗口上按下时用于区分"点击选窗口"与"拖动自定义框选"
    private const double SnapDragThreshold = 2;
    // 把手命中带宽（DIP）
    private const double CornerTolerance = 10;
    private const double EdgeTolerance = 6;
    // 选区最小尺寸（DIP）
    private const double MinSelection = 4;
    // 标注最小尺寸（局部物理像素）
    private const double MinAnnotation = 4;

    /// <summary>把手/选区命中类型</summary>
    private enum HandleHit
    {
        Outside, Inside,
        CornerNW, CornerNE, CornerSW, CornerSE,
        EdgeN, EdgeS, EdgeW, EdgeE,
    }

    /// <summary>标注阶段的输出动作</summary>
    public enum CaptureOutputAction
    {
        /// <summary>✓ 完成（复制到剪贴板）</summary>
        Confirm,
        /// <summary>📌 钉住</summary>
        Pin,
        /// <summary>💾 保存</summary>
        Save,
        /// <summary>📋 复制</summary>
        Copy,
    }

    /// <summary>用户确认输出（参数：虚拟屏 DIP 选区、标注列表（选区局部物理像素）、输出动作）</summary>
    public event Action<Rect, List<Annotation>, CaptureOutputAction>? Committed;
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
            IsHitTestVisible = false, // 不拦截鼠标，标注/调整事件由上层与窗口处理
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
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_sizeTip);

        // 6) 取色放大镜面板（初始隐藏，框选/悬停时跟随鼠标）
        _magnifier = BuildMagnifier();
        _canvas.Children.Add(_magnifier);

        Content = _canvas;

        // ---- 交互 ----
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_textInput != null) CommitTextInput(); // 输入文字时 Esc 先提交
                else Cancel();
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _history.Undo();
            else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _history.Redo();
            else if (e.Key == Key.Delete) DeleteSelected();
        };
        _history.Changed += RedrawAnnotations; // 撤销/重做后重绘标注层
        Focusable = true;
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    // ---------------- 框选交互 ----------------
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        if (_dragging) return; // 防触摸板/触笔提升事件重发 Down

        // 标注阶段：点中选区/把手 → 移动或调整；无标注时点选区外 → 取消选区回到初始等待状态
        if (_confirming && !_adjusting)
        {
            if (_tool != AnnotationTool.None)
                return; // 绘图工具激活：事件由标注层处理（未命中标注层时忽略）

            var hit = HitHandle(pos);

            // 已有标注时：禁用把手缩放（标注坐标系会错位），把手一律按移动处理；
            // 点选区外忽略（防误触丢标注，取消走 ✕/Esc）
            if (hit == HandleHit.Outside)
            {
                if (_annotations.Count == 0)
                {
                    ExitConfirm();
                    e.Handled = true;
                }
                return;
            }
            if (_annotations.Count > 0 && hit != HandleHit.Inside)
                hit = HandleHit.Inside;

            _adjusting = true;
            _adjustMode = hit;
            _adjustStart = pos;
            _adjustStartRect = _selectionRect;
            _canvas.CaptureMouse();
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

        // 调整选区中：按把手类型更新矩形（标注层跟随选区移动）
        if (_adjusting)
        {
            ApplyAdjust(pos);
            UpdateSelectionVisual(_selectionRect);
            PlaceToolbar();
            HideMagnifier();
            return;
        }

        // 标注阶段悬停：选择工具按命中位置切换光标（移动/缩放提示）
        if (_confirming)
        {
            HideMagnifier();
            if (_tool == AnnotationTool.None)
                Cursor = CursorFor(HitHandle(pos));
            return;
        }

        if (!_dragging)
        {
            // 悬停：更新窗口吸附建议
            UpdateHover(pos);
            UpdateMagnifier(pos);
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
        UpdateMagnifier(pos);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 结束选区调整：停留在标注阶段
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

        // 先进入标注状态，再释放鼠标捕获（顺序至关重要）：
        // ReleaseMouseCapture 会同步合成一次 MouseMove（无捕获状态重路由当前位置），
        // 若此刻仍处于"非标注"状态，该事件会落入悬停分支 UpdateHover，
        // 把蓝框重绘成附近窗口 —— 这正是"松手瞬间选区跳变"的根因
        _selectionRect = rect;
        _confirming = true;
        _canvas.ReleaseMouseCapture();

        EnterConfirm();
    }

    // ---------------- 标注阶段 ----------------
    /// <summary>松开鼠标后进入标注阶段：弹出完整工具栏，可在选区上直接标注</summary>
    private void EnterConfirm()
    {
        _cropBase = null;
        HideMagnifier(); // 标注阶段隐藏取色放大镜

        // 标注层（选区局部 DIP 显示；标注数据为局部物理像素）
        _annotLayer ??= CreateAnnotationLayer();
        if (!_canvas.Children.Contains(_annotLayer))
            _canvas.Children.Add(_annotLayer);
        UpdateSelectionVisual(_selectionRect); // 同步标注层位置尺寸

        // 完整标注工具栏：工具/颜色/粗细/撤销/删除 + ✕取消/✓完成/📌钉住/💾保存/📋复制
        _toolbar ??= BuildToolbar();
        if (!_canvas.Children.Contains(_toolbar))
            _canvas.Children.Add(_toolbar);
        PlaceToolbar();

        Cursor = Cursors.Arrow;
    }

    /// <summary>退出标注阶段（取消选区 / 确认输出）</summary>
    private void ExitConfirm()
    {
        _confirming = false;
        _adjusting = false;
        _drawing = false;
        _selected = null;
        _active = null;
        _hoverRect = null;
        HidePreview();
        if (_textInput != null) CommitTextInput();
        if (_annotLayer != null)
            _canvas.Children.Remove(_annotLayer);
        if (_toolbar != null)
            _canvas.Children.Remove(_toolbar);
        Cursor = Cursors.Cross;
        // 清除选区视觉（挖洞恢复全暗），重新框选时再显示
        _clearClip.Rect = Rect.Empty;
        _selection.Visibility = Visibility.Collapsed;
        _sizeTip.Visibility = Visibility.Collapsed;
    }

    private Canvas CreateAnnotationLayer()
    {
        var layer = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true, // 标注不画到选区外（与合成结果一致）
        };
        layer.MouseLeftButtonDown += OnAnnotDown;
        layer.MouseMove += OnAnnotMove;
        layer.MouseLeftButtonUp += OnAnnotUp;
        return layer;
    }

    private AnnotationToolbar BuildToolbar()
    {
        var tb = new AnnotationToolbar(); // 完整版：含全部输出按钮
        HookToolbar(tb);
        tb.SelectTool(AnnotationTool.None); // 默认选择工具（不选中绘制工具）
        return tb;
    }

    private void HookToolbar(AnnotationToolbar tb)
    {
        tb.ToolChanged += t =>
        {
            _tool = t;
            _selected = null;
            Cursor = t == AnnotationTool.None ? Cursors.Arrow : Cursors.Cross;
        };
        tb.ColorChanged += c => _color = c;
        tb.ThicknessChanged += t => _thickness = t;
        tb.UndoRequested += () => _history.Undo();
        tb.RedoRequested += () => _history.Redo();
        tb.DeleteRequested += DeleteSelected;
        tb.ConfirmRequested += () => CommitAction(CaptureOutputAction.Confirm);
        tb.PinRequested += () => CommitAction(CaptureOutputAction.Pin);
        tb.SaveRequested += () => CommitAction(CaptureOutputAction.Save);
        tb.CopyRequested += () => CommitAction(CaptureOutputAction.Copy);
        tb.CancelRequested += Cancel;
    }

    /// <summary>工具栏跟随选区：默认在选区下方居中，屏幕放不下则放上方，钳制在屏内</summary>
    private void PlaceToolbar()
    {
        if (_toolbar == null) return;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tbW = _toolbar.DesiredSize.Width;
        double tbH = _toolbar.DesiredSize.Height;
        const double gap = 8;
        double x = _selectionRect.X + (_selectionRect.Width - tbW) / 2;
        double y = _selectionRect.Bottom + gap;
        if (y + tbH > _canvas.Height) y = _selectionRect.Y - tbH - gap; // 放上方
        if (y < 0) y = 0;
        x = Math.Clamp(x, 0, Math.Max(0, _canvas.Width - tbW));
        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    /// <summary>按输出动作确认：合成标注并交给外层，随后关闭本会话</summary>
    private void CommitAction(CaptureOutputAction action)
    {
        if (!_confirming) return;
        if (_textInput != null) CommitTextInput();

        var virtDip = new Rect(
            _selectionRect.X + _dipBounds.X,
            _selectionRect.Y + _dipBounds.Y,
            _selectionRect.Width,
            _selectionRect.Height);

        ExitConfirm();
        Committed?.Invoke(virtDip, _annotations.ToList(), action);
        _annotations.Clear();
        _history.Clear();
    }

    // ---------------- 标注绘制（坐标：选区局部物理像素） ----------------
    /// <summary>鼠标位置（标注层 DIP）→ 选区局部物理像素</summary>
    private Point ToLocalPixel(MouseEventArgs e)
    {
        var pos = e.GetPosition(_annotLayer!);
        return new Point(pos.X * _scale, pos.Y * _scale);
    }

    /// <summary>局部物理像素 → 标注层 DIP 显示比例</summary>
    private double DisplayScale() => 1.0 / _scale;

    /// <summary>选区的本屏物理像素矩形（裁剪/马赛克采样用）</summary>
    private Int32Rect SelectionPhysical => new(
        (int)Math.Round(_selectionRect.X * _scale),
        (int)Math.Round(_selectionRect.Y * _scale),
        (int)Math.Round(_selectionRect.Width * _scale),
        (int)Math.Round(_selectionRect.Height * _scale));

    /// <summary>选区物理裁剪图（马赛克采样源；选区移动后不重采样，内容随选区走）</summary>
    private BitmapSource EnsureCropBase() =>
        _cropBase ??= ScreenCapturer.Crop(_screenImage, SelectionPhysical);

    private void OnAnnotDown(object sender, MouseButtonEventArgs e)
    {
        if (!_confirming) return;
        if (_textInput != null) { CommitTextInput(); return; }

        var pos = ToLocalPixel(e);

        // 选择模式：命中标注 → 移动；未命中 → 不拦截（冒泡到窗口级处理选区）
        if (_tool == AnnotationTool.None)
        {
            var hit = HitTestAnnotation(pos);
            if (hit == null) return;
            BeginMove(hit, pos);
            e.Handled = true;
            return;
        }

        // 绘图工具下按住 Ctrl 也可临时移动已有标注
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var hit = HitTestAnnotation(pos);
            if (hit != null)
            {
                BeginMove(hit, pos);
                e.Handled = true;
                return;
            }
        }

        _startPoint = pos;
        _annotLayer!.CaptureMouse();
        _drawing = true;
        _selected = null;

        switch (_tool)
        {
            case AnnotationTool.Rectangle:
                _active = new RectAnnotation { Bounds = new Rect(pos, pos), StrokeColor = _color, Thickness = _thickness };
                break;
            case AnnotationTool.Ellipse:
                _active = new EllipseAnnotation { Bounds = new Rect(pos, pos), StrokeColor = _color, Thickness = _thickness };
                break;
            case AnnotationTool.Arrow:
                _active = new ArrowAnnotation { Start = pos, End = pos, StrokeColor = _color, Thickness = _thickness };
                break;
            case AnnotationTool.Pen:
                var pen = new PenAnnotation { StrokeColor = _color, Thickness = _thickness };
                pen.Points.Add(pos);
                _active = pen;
                break;
            case AnnotationTool.Number:
                _history.Do(new AddAnnotationCommand(_annotations,
                    new NumberAnnotation { Center = pos, Number = _numberCounter++, StrokeColor = _color }));
                _drawing = false;
                break;
            case AnnotationTool.Highlight:
                _active = new HighlightAnnotation { Bounds = new Rect(pos, pos), StrokeColor = _color };
                break;
            case AnnotationTool.Mosaic:
                // 马赛克松手时采样生成
                break;
            case AnnotationTool.Text:
                BeginTextInput(pos);
                _drawing = false;
                break;
            default:
                _drawing = false;
                break;
        }

        if (_drawing && _active != null)
            _annotations.Add(_active);
        RedrawAnnotations();
        e.Handled = true;
    }

    private void BeginMove(Annotation target, Point pos)
    {
        _selected = target;
        _startPoint = pos;
        _moveTotal = new Vector(0, 0);
        _drawing = false;
        Cursor = Cursors.SizeAll;
        _annotLayer!.CaptureMouse();
        RedrawAnnotations();
    }

    private void OnAnnotMove(object sender, MouseEventArgs e)
    {
        // 移动选中标注（增量平移，松手整体包成一个撤销命令；钳制不越出选区）
        if (_selected != null && e.LeftButton == MouseButtonState.Pressed && Cursor == Cursors.SizeAll)
        {
            var intended = ToLocalPixel(e) - _startPoint;
            var delta = ClampDeltaToBounds(_selected, intended - _moveTotal);
            _selected.Translate(delta);
            _moveTotal += delta;
            RedrawAnnotations();
            return;
        }

        if (!_drawing || e.LeftButton != MouseButtonState.Pressed || _active == null)
        {
            // 马赛克拖拽：只更新预览框（松手时才采样生成）
            if (_drawing && _tool == AnnotationTool.Mosaic && e.LeftButton == MouseButtonState.Pressed)
            {
                RedrawAnnotations();
                UpdatePreview(NormalizeRect(_startPoint, ToLocalPixel(e)));
            }
            return;
        }

        var pos = ToLocalPixel(e);
        switch (_active)
        {
            case RectAnnotation r:
                r.Bounds = NormalizeRect(_startPoint, pos);
                break;
            case EllipseAnnotation el:
                el.Bounds = NormalizeRect(_startPoint, pos);
                break;
            case HighlightAnnotation hl:
                hl.Bounds = NormalizeRect(_startPoint, pos);
                break;
            case ArrowAnnotation a:
                a.End = pos;
                break;
            case PenAnnotation p:
                p.Points.Add(pos);
                break;
        }
        RedrawAnnotations();
    }

    private void OnAnnotUp(object sender, MouseButtonEventArgs e)
    {
        _annotLayer?.ReleaseMouseCapture();

        // 结束移动：先还原到起点，再用命令重放整段位移（可撤销）；选中态保持
        if (_selected != null)
        {
            if (_moveTotal.Length > 1)
            {
                var item = _selected;
                var total = _moveTotal;
                item.Translate(-total);
                _history.Do(new MoveAnnotationCommand(item, total));
            }
            Cursor = _tool == AnnotationTool.None ? Cursors.Arrow : Cursors.Cross;
            RedrawAnnotations();
            return;
        }

        // 马赛克：松手时对拖拽区域采样生成（采样源 = 选区物理裁剪图）
        if (_drawing && _tool == AnnotationTool.Mosaic)
        {
            _drawing = false;
            var rect = NormalizeRect(_startPoint, ToLocalPixel(e));
            HidePreview();
            if (rect.Width >= MinAnnotation && rect.Height >= MinAnnotation)
            {
                int block = _thickness switch { <= 1 => 6, >= 3 => 20, _ => 12 };
                _history.Do(new AddAnnotationCommand(_annotations,
                    new MosaicAnnotation(EnsureCropBase(), rect, block)));
            }
            RedrawAnnotations();
            return;
        }

        if (!_drawing || _active == null) { _drawing = false; return; }
        _drawing = false;

        // 拖拽类标注已在 Down 时加入列表，这里包成 Add 命令以便撤销（先移除避免重复）
        _annotations.Remove(_active);
        if (_active is not PenAnnotation && _active.GetBounds().Width < 3 && _active.GetBounds().Height < 3)
        {
            _active = null;
            RedrawAnnotations();
            return;
        }
        _history.Do(new AddAnnotationCommand(_annotations, _active));
        _active = null;
    }

    // ---------------- 标注辅助 ----------------
    private Annotation? HitTestAnnotation(Point pos)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            var b = _annotations[i].GetBounds();
            b.Inflate(6, 6);
            if (b.Contains(pos)) return _annotations[i];
        }
        return null;
    }

    /// <summary>把位移裁剪到标注包围盒不越出选区范围的允许量</summary>
    private Vector ClampDeltaToBounds(Annotation ann, Vector delta)
    {
        var b = ann.GetBounds();
        double w = SelectionPhysical.Width, h = SelectionPhysical.Height;
        double dx = delta.X, dy = delta.Y;
        if (b.X + dx < 0) dx = -b.X;
        if (b.Y + dy < 0) dy = -b.Y;
        if (b.Right + dx > w) dx = w - b.Right;
        if (b.Bottom + dy > h) dy = h - b.Bottom;
        return new Vector(dx, dy);
    }

    /// <summary>删除当前选中的标注（工具栏 🗑 按钮与 Delete 键共用）</summary>
    private void DeleteSelected()
    {
        if (_selected == null) return;
        _history.Do(new DeleteAnnotationCommand(_annotations, _selected));
        _selected = null;
        RedrawAnnotations();
    }

    private static Rect NormalizeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>重绘标注层（标注为局部物理像素，按 1/scale 换算为 DIP 显示）</summary>
    private void RedrawAnnotations()
    {
        if (_annotLayer == null) return;
        _annotLayer.Children.Clear();
        if (_annotations.Count == 0) return;

        _annotLayer.Children.Add(new AnnotationLayerVisual(_annotations, _selected, DisplayScale()));

        // 重新挂上文字输入框（若存在，重绘时不丢失）
        if (_textInput != null && !_annotLayer.Children.Contains(_textInput))
            _annotLayer.Children.Add(_textInput);
    }

    // ---------------- 文字输入（T 工具） ----------------
    private void BeginTextInput(Point pos)
    {
        if (_annotLayer == null) return;
        double disp = DisplayScale();

        _textInput = new TextBox
        {
            MinWidth = 120 * disp,
            FontSize = (16 + _thickness * 2) * disp, // 与 TextAnnotation 字号（物理像素）视觉一致
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(_color),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            AcceptsReturn = false,
        };
        Canvas.SetLeft(_textInput, pos.X * disp);
        Canvas.SetTop(_textInput, pos.Y * disp);
        _annotLayer.Children.Add(_textInput);
        _textInput.Focus();

        _textInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTextInput(); e.Handled = true; }
        };
        _textInput.LostFocus += (_, _) => CommitTextInput();

        _textInput.Tag = pos; // 记录插入位置（局部物理像素）
    }

    /// <summary>提交文字输入，生成 TextAnnotation（空白则丢弃）</summary>
    private void CommitTextInput()
    {
        if (_textInput == null || _annotLayer == null) return;
        var pos = (Point)_textInput.Tag;
        var text = _textInput.Text;
        _annotLayer.Children.Remove(_textInput);
        _textInput = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            _history.Do(new AddAnnotationCommand(_annotations, new TextAnnotation
            {
                Text = text,
                Position = pos,
                StrokeColor = _color,
                FontSize = 16 + _thickness * 2,
            }));
        }
        Keyboard.Focus(this);
    }

    // ---------------- 马赛克拖拽预览 ----------------
    private void UpdatePreview(Rect rect)
    {
        if (_annotLayer == null) return;
        double disp = DisplayScale();
        _previewRect ??= new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
        };
        if (!_annotLayer.Children.Contains(_previewRect))
            _annotLayer.Children.Add(_previewRect);
        Canvas.SetLeft(_previewRect, rect.X * disp);
        Canvas.SetTop(_previewRect, rect.Y * disp);
        _previewRect.Width = rect.Width * disp;
        _previewRect.Height = rect.Height * disp;
    }

    private void HidePreview()
    {
        if (_previewRect != null)
            _annotLayer?.Children.Remove(_previewRect);
    }

    /// <summary>标注绘制层：把所有标注按显示比例画到一个 FrameworkElement 上</summary>
    private class AnnotationLayerVisual : FrameworkElement
    {
        private readonly List<Annotation> _annotations;
        private readonly Annotation? _selected;
        private readonly double _scale;

        public AnnotationLayerVisual(List<Annotation> annotations, Annotation? selected, double scale)
        {
            _annotations = annotations;
            _selected = selected;
            _scale = scale;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            foreach (var ann in _annotations)
            {
                ann.Render(dc, _scale);
                // 选中高亮：画虚线包围盒
                if (ann == _selected)
                {
                    var b = ann.GetBounds();
                    b.Inflate(3, 3);
                    var pen = new Pen(Brushes.DodgerBlue, 1) { DashStyle = DashStyles.Dash };
                    dc.DrawRectangle(null, pen, b);
                }
            }
        }
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

        // 标注层跟随选区（位置尺寸同步；局部坐标系不变，标注随选区平移）
        if (_annotLayer != null)
        {
            Canvas.SetLeft(_annotLayer, rect.X);
            Canvas.SetTop(_annotLayer, rect.Y);
            _annotLayer.Width = rect.Width;
            _annotLayer.Height = rect.Height;
        }
    }

    private static Rect Normalize(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void Cancel()
    {
        _annotations.Clear();
        _history.Clear();
        Cancelled?.Invoke();
        // 不在此 Close，由外层统一关闭
    }

    // ---------------- 取色放大镜 ----------------
    /// <summary>构建放大镜面板：放大网格 + 坐标 + 色块/RGB/HEX</summary>
    private Border BuildMagnifier()
    {
        // 固定文本行宽度（按 3 位数样例 "RGB(255, 255, 255)" 测量），
        // 避免 (1,2,3) 与 (123,243,245) 等内容变化导致面板与放大区宽度抖动
        var protoTypeface = new Typeface(
            new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var proto = new FormattedText(
            "RGB(255, 255, 255)",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, protoTypeface, 11, Brushes.Transparent, 96.0);
        double textWidth = Math.Ceiling(proto.WidthIncludingTrailingWhitespace);

        _magVisual = new MagnifierVisual(() => _magBitmap, MagCells);
        // 宽度水平拉伸填满面板内容宽（与底部文本行同宽）；高度绑定自身实际宽度 → 正方形
        _magVisual.SetBinding(HeightProperty,
            new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = _magVisual });

        _magPos = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 4, 0, 0),
            MinWidth = textWidth,
        };
        _magColor = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 2, 0, 0),
            MinWidth = textWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _magRgb = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 2, 0, 0),
            MinWidth = textWidth,
        };
        _magSwatch = new System.Windows.Shapes.Rectangle
        {
            Width = 11,
            Height = 11,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Margin = new Thickness(0, 2, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal };
        hexRow.Children.Add(_magSwatch);
        hexRow.Children.Add(_magColor);

        var panel = new StackPanel();
        panel.Children.Add(_magVisual);  // 放大网格
        panel.Children.Add(_magPos);     // 第一行：坐标
        panel.Children.Add(hexRow);      // 第二行：色块 + HEX
        panel.Children.Add(_magRgb);     // 第三行：RGB

        return new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2B, 0x2B, 0x2B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false, // 不拦截鼠标
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10, ShadowDepth = 1, Opacity = 0.4, Color = Colors.Black
            },
        };
    }

    /// <summary>更新放大镜：采样鼠标周围像素、刷新文本并跟随鼠标（靠屏幕边缘自动翻转）</summary>
    private void UpdateMagnifier(Point posDip)
    {
        if (_magnifier == null || _magVisual == null) return;

        // 本屏 DIP → 本屏物理像素（全局屏幕坐标 = 屏物理原点 + 局部物理）
        int px = (int)Math.Round(posDip.X * _scale);
        int py = (int)Math.Round(posDip.Y * _scale);
        int gx = (int)Math.Round(_dipBounds.X * _scale) + px;
        int gy = (int)Math.Round(_dipBounds.Y * _scale) + py;

        // 采样区域（9x9，越界钳制到屏内）
        int imgW = _screenImage.PixelWidth, imgH = _screenImage.PixelHeight;
        int x0 = Math.Clamp(px - MagCells / 2, 0, Math.Max(0, imgW - MagCells));
        int y0 = Math.Clamp(py - MagCells / 2, 0, Math.Max(0, imgH - MagCells));

        try
        {
            // 复用位图与缓冲，每帧零分配写入
            _magBitmap ??= new WriteableBitmap(MagCells, MagCells, 96, 96, PixelFormats.Bgra32, null);
            int bytes = MagCells * MagCells * 4;
            if (_magBuffer.Length != bytes) _magBuffer = new byte[bytes];
            _screenImage.CopyPixels(new Int32Rect(x0, y0, MagCells, MagCells), _magBuffer, MagCells * 4, 0);
            _magBitmap.WritePixels(new Int32Rect(0, 0, MagCells, MagCells), _magBuffer, MagCells * 4, 0);
            _magVisual.InvalidateVisual();
        }
        catch
        {
            // 采样失败（理论不发生）忽略
        }

        // 中心像素颜色（钳制后中心，屏幕内鼠标即鼠标位置）
        var c = GetPixelColor(x0 + MagCells / 2, y0 + MagCells / 2);
        _magPos!.Text = $"{gx}, {gy}";
        _magColor!.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _magRgb!.Text = $"RGB({c.R}, {c.G}, {c.B})";
        _magSwatch!.Fill = new SolidColorBrush(c);

        // 面板跟随鼠标：默认右下偏移，靠屏幕边缘翻转到对侧
        _magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _magnifier.DesiredSize.Width;
        double h = _magnifier.DesiredSize.Height;
        const double offset = 18;
        double x = posDip.X + offset;
        double y = posDip.Y + offset;
        if (x + w > _canvas.Width) x = posDip.X - w - offset;
        if (y + h > _canvas.Height) y = posDip.Y - h - offset;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        Canvas.SetLeft(_magnifier, x);
        Canvas.SetTop(_magnifier, y);
        _magnifier.Visibility = Visibility.Visible;
    }

    private void HideMagnifier()
    {
        if (_magnifier != null)
            _magnifier.Visibility = Visibility.Collapsed;
    }

    /// <summary>读取本屏位图指定物理像素颜色</summary>
    private Color GetPixelColor(int px, int py)
    {
        if (px < 0 || py < 0 || px >= _screenImage.PixelWidth || py >= _screenImage.PixelHeight)
            return Colors.Black;
        try
        {
            var b = new byte[4];
            _screenImage.CopyPixels(new Int32Rect(px, py, 1, 1), b, 4, 0);
            return Color.FromArgb(b[3], b[2], b[1], b[0]); // BGRA → Color
        }
        catch
        {
            return Colors.Black;
        }
    }

    /// <summary>放大区渲染：位图放大 + 网格线 + 中心像素高亮框</summary>
    private sealed class MagnifierVisual : FrameworkElement
    {
        private readonly Func<WriteableBitmap?> _source;
        private readonly int _cells;

        public MagnifierVisual(Func<WriteableBitmap?> source, int cells)
        {
            _source = source;
            _cells = cells;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var bmp = _source();
            if (bmp == null) return;

            // 长方形填充：位图拉伸到实际渲染尺寸，网格按宽/高分别分格
            double w = RenderSize.Width, h = RenderSize.Height;
            if (w <= 0 || h <= 0) return;
            double cw = w / _cells, ch = h / _cells;

            // 放大的像素区（拉伸填满）
            dc.DrawImage(bmp, new Rect(0, 0, w, h));

            // 网格线（半透明白）
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
            gridPen.Freeze();
            for (int i = 1; i < _cells; i++)
            {
                dc.DrawLine(gridPen, new Point(i * cw, 0), new Point(i * cw, h));
                dc.DrawLine(gridPen, new Point(0, i * ch), new Point(w, i * ch));
            }

            // 中心像素高亮框（白+黑双描边，任意背景下可见）
            int m = _cells / 2;
            var center = new Rect(m * cw, m * ch, cw, ch);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 3), center);
            dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), center);
        }
    }

    /// <summary>显示遮罩并进入框选</summary>
    public void ShowOverlay() => Show();
}
