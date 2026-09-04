using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnipPin.Core.Annotations;
using SnipPin.Core.Configuration;

namespace SnipPin.Core.Services;

/// <summary>
/// 贴图悬浮窗：无边框、置顶、可拖动/缩放/旋转/调透明度的图片窗。
/// 纯代码构建（无需 XAML），便于动态创建多张。
/// 支持右键"显示工具栏"在图片下方展开标注工具条，直接在贴图上绘制标注；
/// 标注以矢量形式保留，另存为/复制时才合成进最终图片。
/// </summary>
public class PinWindow : Window
{
    private readonly Image _image;
    private readonly PinConfig _config;
    private readonly Border _imageBorder;
    private readonly Grid _root;

    // ---- 标注（矢量层，叠加在图片上，不改变原图） ----
    private BitmapSource _baseImage;
    private readonly List<Annotation> _annotations = new();
    private readonly UndoRedoStack _history = new();
    private readonly HistoryService _historyService; // ✓ 写入时生成截图历史记录
    private Canvas? _annotCanvas;
    private AnnotationToolbar? _toolbar;
    private bool _toolbarVisible;
    private int _sessionStartIndex; // 本次标注会话开始前已有的标注数（✕ 作废时回退到此）

    private double _scale = 1.0;
    private double _rotation = 0;
    private bool _alwaysOnTop;

    private const double MinScale = 0.1;
    private const double MaxScale = 5.0;
    private const double ToolbarGap = 6; // 工具条与图片之间的间距

    // ---------------- 对外状态（会话持久化用） ----------------
    /// <summary>贴图原始图片（未缩放，未含标注）</summary>
    public BitmapSource ImageSource => _baseImage;
    /// <summary>当前缩放系数</summary>
    public double CurrentScale => _scale;
    /// <summary>当前旋转角度（度）</summary>
    public double CurrentRotation => _rotation;
    /// <summary>是否有未合成的标注</summary>
    public bool HasAnnotations => _annotations.Count > 0;

    /// <summary>合成后的最终图片（含标注）；无标注时返回原图</summary>
    public BitmapSource GetCompositedImage() =>
        _annotations.Count == 0
            ? _baseImage
            : AnnotationCompositor.Render(_baseImage, _annotations);

    /// <summary>恢复会话状态（缩放/旋转/透明度/置顶）</summary>
    public void RestoreState(PinState state)
    {
        SetScale(state.Scale);
        if (Math.Abs(state.Rotation) > 0.01)
        {
            _rotation = state.Rotation;
            _imageBorder.LayoutTransform = new RotateTransform(_rotation);
        }
        Opacity = state.Opacity;
        _alwaysOnTop = state.AlwaysOnTop;
        Topmost = _alwaysOnTop;
    }

    public PinWindow(BitmapSource image, PinConfig config, HistoryService historyService)
    {
        _config = config;
        _baseImage = image;
        _historyService = historyService;
        _alwaysOnTop = config.AlwaysOnTop;

        // ---- 窗口外观：无边框、置顶、透明背景、可调整 ----
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = _alwaysOnTop;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = config.DefaultOpacity;

        // ---- 图片内容 ----
        // 尺寸显式设为像素原大（Stretch.Uniform 在 SizeToContent 受屏幕约束时会把小图放大
        // 填满屏幕，导致贴图"超级大"；改为 SetScale(1.0) 固定宽高，显示原始大小）
        _image = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
        };
        // 缩放时保持清晰（RenderOptions 为附加属性，需静态设置）
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        // 布局变换用于旋转，避免影响拖动
        _imageBorder = new Border
        {
            LayoutTransform = new RotateTransform(0),
        };

        // 图片 + 标注画布叠层（Grid 同格叠加，标注层置于图片之上）
        var imageStack = new Grid();
        imageStack.Children.Add(_image);
        _annotCanvas = CreateAnnotationCanvas();
        HookAnnotationCanvas(_annotCanvas);
        imageStack.Children.Add(_annotCanvas);
        _imageBorder.Child = imageStack;

        // 初始即按原图大小布局（_scale = 1.0）
        SetScale(1.0);

        // ---- 根布局：图片在上，工具条在下 ----
        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_imageBorder, 0);
        _root.Children.Add(_imageBorder);

        Content = _root;

        // 悬停显示细边框提示可交互
        MouseEnter += (_, _) => _imageBorder.BorderThickness = new Thickness(1);
        MouseLeave += (_, _) => _imageBorder.BorderThickness = new Thickness(0);
        _imageBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));
        _imageBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Opacity = 0.4,
            Color = Colors.Black
        };

        // ---- 交互 ----
        // 左键拖动。注意：事件先于画布 OnAnnotDown 触发（冒泡自内向外），
        // 而标注模式下画布 IsHitTestVisible=false，故 DragMove 只在非标注模式生效。
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        // 双击不再关闭贴图（曾导致误关）；关闭走右键菜单或 Esc
        // Esc：正在输入文字先提交；标注模式下作废本次标注并收起；否则关闭
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_textInput != null) CommitTextInput();
                else if (_toolbarVisible) DiscardSession();
                else Close();
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _history.Undo();
            else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _history.Redo();
            else if (e.Key == Key.Delete) DeleteSelected();
        };
        // 滚轮：缩放（Ctrl）/ 旋转（Shift）/ 透明度（Alt）
        PreviewMouseWheel += OnMouseWheel;

        // 右键菜单
        ContextMenu = BuildContextMenu();

        // 撤销/重做后重绘标注层
        _history.Changed += RedrawAnnotations;

        // 让窗口能接收键盘事件
        Focusable = true;
        Loaded += (_, _) =>
        {
            Keyboard.Focus(this);
            // 窗口加载后 DPI 已确定，重算一次以保证物理像素 1:1
            ApplySize();
        };
        // 跨显示器拖动/系统缩放变化时保持物理像素尺寸
        DpiChanged += (_, _) => ApplySize();
    }

    /// <summary>按当前 _scale 计算图片 DIP 尺寸（scale=1 时等于物理像素原大）</summary>
    private void ApplySize()
    {
        // 图片像素为基准：scale=1 → 物理像素 1:1。
        // 窗口 DIP 尺寸 = 像素 / 当前显示器 DPI 缩放；窗口未加载时 DpiScale=1（DPI 96）。
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpiScale <= 0) dpiScale = 1.0;
        _image.Width = _baseImage.PixelWidth * _scale / dpiScale;
        _image.Height = _baseImage.PixelHeight * _scale / dpiScale;
        // 标注画布与图片同尺寸（叠层对齐）
        if (_annotCanvas != null)
        {
            _annotCanvas.Width = _image.Width;
            _annotCanvas.Height = _image.Height;
        }
    }

    private void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, MinScale, MaxScale);
        ApplySize();
        RedrawAnnotations(); // 缩放变化后按新比例重绘标注
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            // 缩放
            double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            SetScale(_scale * factor);
            e.Handled = true;
        }
        else if (mods.HasFlag(ModifierKeys.Shift))
        {
            // 旋转（90° 步进）
            _rotation = (_rotation + (e.Delta > 0 ? 90 : -90)) % 360;
            _imageBorder.LayoutTransform = new RotateTransform(_rotation);
            e.Handled = true;
        }
        else if (mods.HasFlag(ModifierKeys.Alt))
        {
            // 透明度
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.05 : -0.05), 0.1, 1.0);
            e.Handled = true;
        }
    }

    // ==================== 标注工具栏 ====================

    /// <summary>创建标注画布（叠加在图片上，与图片同尺寸，用于矢量标注显示与绘制）</summary>
    private Canvas CreateAnnotationCanvas()
    {
        var canvas = new Canvas
        {
            // 初始透明，命中测试在标注模式下由窗口级事件处理
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            // 视觉裁剪到图片范围：绘制超出边缘的标注不画到贴图外（与合成结果一致）
            ClipToBounds = true,
        };
        // 尺寸跟随图片（在 ApplySize 后由 imageStack 布局撑开）
        return canvas;
    }

    /// <summary>切换工具栏显示状态（收起视为 ✓ 写入，避免用户主动收起时丢失标注）</summary>
    private void ToggleToolbar()
    {
        if (_toolbarVisible) CommitSession();
        else ShowToolbar();
    }

    private void ShowToolbar()
    {
        if (_toolbarVisible) return;
        _toolbarVisible = true;
        _sessionStartIndex = _annotations.Count;

        // 工具条：隐藏钉住/保存/复制，保留 ✕ 作废 / ✓ 写入
        _toolbar = new AnnotationToolbar(showOutputActions: false)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, ToolbarGap, 0, 0),
        };
        Grid.SetRow(_toolbar, 1);
        _root.Children.Add(_toolbar);
        HookToolbar(_toolbar);

        // 默认不选中任何绘制工具（选择模式：点击图片 = 拖动窗口）
        _toolbar.SelectTool(AnnotationTool.None);

        // 旋转会改变图像坐标系与屏幕坐标系的对应关系，导致标注错位。
        // 进入标注模式前先把旋转归零，保证坐标 1:1 可绘制。
        if (Math.Abs(_rotation) > 0.01)
        {
            _rotation = 0;
            _imageBorder.LayoutTransform = new RotateTransform(0);
        }

        // 进入标注模式：允许画布命中
        if (_annotCanvas != null) _annotCanvas.IsHitTestVisible = true;

        RedrawAnnotations();
    }

    /// <summary>仅收起工具栏 UI（不清标注数据）</summary>
    private void HideToolbar()
    {
        if (!_toolbarVisible) return;
        _toolbarVisible = false;

        if (_toolbar != null)
        {
            _root.Children.Remove(_toolbar);
            _toolbar = null;
        }
        if (_annotCanvas != null) _annotCanvas.IsHitTestVisible = false;
        Cursor = Cursors.Arrow;
        Keyboard.Focus(this);
    }

    /// <summary>✓ 写入：把本次标注烘焙进贴图位图并收起工具栏，同时生成一条截图历史</summary>
    private void CommitSession()
    {
        if (_annotations.Count > _sessionStartIndex)
        {
            // 标注合成后替换底图（原标注坐标与新底图像素对齐，可继续叠加标注）
            var composited = AnnotationCompositor.Render(_baseImage, _annotations);
            _baseImage = composited;
            _image.Source = composited;

            // 标注完成的成品图生成一条新的截图记录（失败不影响主流程）
            _historyService.Add(composited, "贴图标注");
        }
        _annotations.Clear();
        _history.Clear();
        _sessionStartIndex = 0;
        RedrawAnnotations();
        HideToolbar();
    }

    /// <summary>✕ 作废：丢弃本次会话新增的标注并收起工具栏</summary>
    private void DiscardSession()
    {
        if (_sessionStartIndex < _annotations.Count)
            _annotations.RemoveRange(_sessionStartIndex, _annotations.Count - _sessionStartIndex);
        _history.Clear();
        _sessionStartIndex = _annotations.Count;
        RedrawAnnotations();
        HideToolbar();
    }

    // ---------------- 标注绘制状态 ----------------
    private AnnotationTool _tool = AnnotationTool.Rectangle;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _thickness = 2;
    private bool _drawing;
    private Point _startPoint;
    private Annotation? _active;
    private Annotation? _selected;   // 选中待移动的标注
    private Vector _moveTotal;       // 本次移动累计位移（松手生成一个撤销命令）
    private int _numberCounter = 1;
    private TextBox? _textInput;     // 文字输入框（T 工具）
    private Border? _previewRect;    // 马赛克拖拽预览框

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
        tb.CancelRequested += DiscardSession;
        tb.ConfirmRequested += CommitSession;
    }

    // 标注画布鼠标事件在构造时统一挂接
    private void HookAnnotationCanvas(Canvas canvas)
    {
        canvas.MouseLeftButtonDown += OnAnnotDown;
        canvas.MouseMove += OnAnnotMove;
        canvas.MouseLeftButtonUp += OnAnnotUp;
    }

    /// <summary>把窗口 DIP 坐标换算为图像物理像素坐标</summary>
    private Point ToImagePoint(MouseEventArgs e)
    {
        var pos = e.GetPosition(_image);
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpiScale <= 0) dpiScale = 1.0;
        // DIP → 物理像素，再除以缩放系数还原到图像原坐标
        return new Point(
            pos.X * dpiScale / _scale,
            pos.Y * dpiScale / _scale);
    }

    private void OnAnnotDown(object sender, MouseButtonEventArgs e)
    {
        if (!_toolbarVisible) return;

        // 若正在输入文字，先提交（第二次点击不再开新输入框）
        if (_textInput != null) { CommitTextInput(); return; }

        var pos = ToImagePoint(e);

        // 选择模式（默认未选工具）：命中已有标注则进入移动模式；未命中则取消选中并冒泡拖动窗口
        if (_tool == AnnotationTool.None)
        {
            var hit = HitTest(pos);
            if (hit == null)
            {
                if (_selected != null) { _selected = null; RedrawAnnotations(); }
                return; // 冒泡到窗口 → DragMove
            }
            _selected = hit;
            _startPoint = pos;
            _moveTotal = new Vector(0, 0);
            _drawing = false;
            Cursor = Cursors.SizeAll;
            _annotCanvas?.CaptureMouse();
            RedrawAnnotations();
            e.Handled = true;
            return;
        }

        // 绘图工具下按住 Ctrl 也可临时移动已有标注（与截图编辑器一致）
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var hit = HitTest(pos);
            if (hit != null)
            {
                _selected = hit;
                _startPoint = pos;
                _moveTotal = new Vector(0, 0);
                _drawing = false;
                Cursor = Cursors.SizeAll;
                _annotCanvas?.CaptureMouse();
                RedrawAnnotations();
                e.Handled = true;
                return;
            }
        }

        _startPoint = pos;
        _annotCanvas?.CaptureMouse();
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

    private void OnAnnotMove(object sender, MouseEventArgs e)
    {
        // 移动选中标注（增量平移，松手整体包成一个撤销命令）
        if (_selected != null && e.LeftButton == MouseButtonState.Pressed && Cursor == Cursors.SizeAll)
        {
            // 意向位移 = 鼠标位移；实际位移限制标注包围盒不越出图像（防止移出图片看不见）
            var intended = ToImagePoint(e) - _startPoint;
            var delta = ClampDeltaToBounds(_selected, intended - _moveTotal);
            _selected.Translate(delta);
            _moveTotal += delta; // 记录已应用的累计位移
            RedrawAnnotations();
            return;
        }

        if (!_drawing || e.LeftButton != MouseButtonState.Pressed || _active == null)
        {
            // 马赛克拖拽：无活动标注，只更新预览框（松手时才采样生成）
            if (_drawing && _tool == AnnotationTool.Mosaic && e.LeftButton == MouseButtonState.Pressed)
            {
                RedrawAnnotations();
                UpdatePreview(NormalizeRect(_startPoint, ToImagePoint(e)));
            }
            return;
        }
        var pos = ToImagePoint(e);

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
        _annotCanvas?.ReleaseMouseCapture();

        // 结束移动：先还原到起点，再用命令重放整段位移（使移动可撤销）。
        // 选中态保留到点击空白/切换工具/删除，方便松手后继续 Delete 或再次拖动
        if (_selected != null)
        {
            if (_moveTotal.Length > 1)
            {
                var item = _selected;
                var total = _moveTotal;
                item.Translate(-total); // 还原
                _history.Do(new MoveAnnotationCommand(item, total));
            }
            Cursor = _tool == AnnotationTool.None ? Cursors.Arrow : Cursors.Cross;
            RedrawAnnotations();
            return;
        }

        // 马赛克：松手时对拖拽区域采样生成
        if (_drawing && _tool == AnnotationTool.Mosaic)
        {
            _drawing = false;
            var rect = NormalizeRect(_startPoint, ToImagePoint(e));
            HidePreview();
            if (rect.Width >= 4 && rect.Height >= 4)
            {
                int block = _thickness switch { <= 1 => 6, >= 3 => 20, _ => 12 };
                _history.Do(new AddAnnotationCommand(_annotations,
                    new MosaicAnnotation(_baseImage, rect, block)));
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

    private static Rect NormalizeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    // ---------------- 命中测试 ----------------
    /// <summary>从最上层开始找包含点的标注（包围盒 + 阈值，图像坐标）</summary>
    private Annotation? HitTest(Point pos)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            var b = _annotations[i].GetBounds();
            b.Inflate(6, 6);
            if (b.Contains(pos)) return _annotations[i];
        }
        return null;
    }

    /// <summary>把位移裁剪到标注包围盒不越出图像范围的允许量（图像坐标）</summary>
    private Vector ClampDeltaToBounds(Annotation ann, Vector delta)
    {
        var b = ann.GetBounds();
        double imgW = _baseImage.PixelWidth, imgH = _baseImage.PixelHeight;
        double dx = delta.X, dy = delta.Y;
        if (b.X + dx < 0) dx = -b.X;
        if (b.Y + dy < 0) dy = -b.Y;
        if (b.Right + dx > imgW) dx = imgW - b.Right;
        if (b.Bottom + dy > imgH) dy = imgH - b.Bottom;
        return new Vector(dx, dy);
    }

    // ---------------- 删除选中 ----------------
    /// <summary>删除当前选中的标注（工具栏 🗑 按钮与 Delete 键共用）</summary>
    private void DeleteSelected()
    {
        if (_selected == null) return;
        _history.Do(new DeleteAnnotationCommand(_annotations, _selected));
        _selected = null;
        RedrawAnnotations();
    }

    /// <summary>重绘标注层（在图片上以显示缩放比例渲染矢量标注）</summary>
    private void RedrawAnnotations()
    {
        if (_annotCanvas == null) return;
        _annotCanvas.Children.Clear();
        if (_annotations.Count == 0) return;

        // 标注坐标为图像物理像素，画布以 DIP 显示：渲染系数 = 显示 DIP / 物理像素
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpiScale <= 0) dpiScale = 1.0;
        double renderScale = _scale / dpiScale;

        _annotCanvas.Children.Add(new AnnotationLayerVisual(_annotations, _selected, renderScale));

        // 重新挂上文字输入框（若存在，重绘时不丢失）
        if (_textInput != null && !_annotCanvas.Children.Contains(_textInput))
            _annotCanvas.Children.Add(_textInput);
    }

    // ---------------- 文字输入（T 工具） ----------------
    /// <summary>在图像坐标 pos 处打开文字输入框</summary>
    private void BeginTextInput(Point pos)
    {
        if (_annotCanvas == null) return;
        double dispScale = DisplayScale();

        _textInput = new TextBox
        {
            MinWidth = 120 * dispScale,
            FontSize = (16 + _thickness * 2) * dispScale, // 与 TextAnnotation 字号（图像像素）视觉一致
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(_color),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            AcceptsReturn = false,
        };
        Canvas.SetLeft(_textInput, pos.X * dispScale);
        Canvas.SetTop(_textInput, pos.Y * dispScale);
        _annotCanvas.Children.Add(_textInput);
        _textInput.Focus();

        _textInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTextInput(); e.Handled = true; }
        };
        _textInput.LostFocus += (_, _) => CommitTextInput();

        _textInput.Tag = pos; // 记录插入位置（图像坐标）
    }

    /// <summary>提交文字输入，生成 TextAnnotation（空白则丢弃）</summary>
    private void CommitTextInput()
    {
        if (_textInput == null || _annotCanvas == null) return;
        var pos = (Point)_textInput.Tag;
        var text = _textInput.Text;
        _annotCanvas.Children.Remove(_textInput);
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
    /// <summary>显示/更新马赛克拖拽范围预览框（rect 为图像坐标）</summary>
    private void UpdatePreview(Rect rect)
    {
        if (_annotCanvas == null) return;
        double dispScale = DisplayScale();
        _previewRect ??= new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
        };
        if (!_annotCanvas.Children.Contains(_previewRect))
            _annotCanvas.Children.Add(_previewRect);
        Canvas.SetLeft(_previewRect, rect.X * dispScale);
        Canvas.SetTop(_previewRect, rect.Y * dispScale);
        _previewRect.Width = rect.Width * dispScale;
        _previewRect.Height = rect.Height * dispScale;
    }

    private void HidePreview()
    {
        if (_previewRect != null)
            _annotCanvas?.Children.Remove(_previewRect);
    }

    /// <summary>图像物理像素 → 画布 DIP 的显示比例</summary>
    private double DisplayScale()
    {
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpiScale <= 0) dpiScale = 1.0;
        return _scale / dpiScale;
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

    // ==================== 右键菜单 ====================

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var toolbarItem = new MenuItem { Header = "显示工具栏", IsCheckable = true, IsChecked = _toolbarVisible };
        toolbarItem.Click += (_, _) =>
        {
            ToggleToolbar();
            toolbarItem.IsChecked = _toolbarVisible;
        };

        var topmost = new MenuItem { Header = "始终置顶", IsCheckable = true, IsChecked = _alwaysOnTop };
        topmost.Click += (_, _) => { _alwaysOnTop = topmost.IsChecked; Topmost = _alwaysOnTop; };

        // 透明度滑块：双向绑定窗口 Opacity，拖动实时生效（与 Alt+滚轮自动同步）
        // 下限 0.05 而非 0：完全透明会导致贴图无法点中、无法恢复
        var opacitySlider = new Slider
        {
            Minimum = 0.05,
            Maximum = 1.0,
            Width = 140,
            SmallChange = 0.05,
            LargeChange = 0.1,
            IsMoveToPointEnabled = true,
        };
        opacitySlider.SetBinding(Slider.ValueProperty,
            new Binding(nameof(Opacity)) { Source = this, Mode = BindingMode.TwoWay });
        var opacityValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 36 };
        opacityValue.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(Slider.Value)) { Source = opacitySlider, StringFormat = "{0:P0}" });
        var opacityPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(28, 6, 10, 6), // 左侧留出菜单勾选列宽度对齐文字
        };
        opacityPanel.Children.Add(new TextBlock { Text = "透明度", VerticalAlignment = VerticalAlignment.Center });
        opacityPanel.Children.Add(opacitySlider);
        opacityPanel.Children.Add(opacityValue);

        var resetScale = new MenuItem { Header = "还原大小" };
        resetScale.Click += (_, _) => SetScale(1.0);

        var copy = new MenuItem { Header = "复制到剪贴板" };
        copy.Click += (_, _) => Clipboard.SetImage(GetCompositedImage());

        var save = new MenuItem { Header = "另存为..." };
        save.Click += (_, _) => SaveAs();

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();

        menu.Items.Add(toolbarItem);
        menu.Items.Add(topmost);
        menu.Items.Add(opacityPanel);
        menu.Items.Add(resetScale);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(save);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        return menu;
    }

    private void SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPG 图片|*.jpg|BMP 图片|*.bmp",
            FileName = "贴图.png"
        };
        if (dlg.ShowDialog() != true) return;

        var src = GetCompositedImage();
        BitmapEncoder encoder = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 90 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var fs = new System.IO.FileStream(dlg.FileName, System.IO.FileMode.Create);
        encoder.Save(fs);
    }
}
