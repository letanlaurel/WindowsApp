using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipPin.Core.Annotations;

/// <summary>
/// 标注编辑窗：显示截图选区，用户可在其上绘制/编辑矢量标注。
/// 坐标系与图像物理像素 1:1（DPI 96 下 DIP == 物理像素）。
/// </summary>
public class AnnotationEditorWindow : Window
{
    private readonly BitmapSource _baseImage;
    private readonly List<Annotation> _annotations = new();
    private readonly UndoRedoStack _history = new();

    private readonly Canvas _layer;      // 标注画布（底图 + 标注）
    private readonly AnnotationToolbar _toolbar;
    private Border? _frame;               // 选区边框
    private TextBox? _textInput;          // 文字输入框
    private Border? _previewRect;         // 马赛克拖拽预览框

    // 当前绘图状态
    private AnnotationTool _tool = AnnotationTool.Rectangle;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _thickness = 2;

    private bool _drawing;
    private Point _startPoint;
    private Annotation? _active;         // 正在绘制的标注
    private Annotation? _selected;       // 选中的标注
    private Vector _moveTotal;           // 本次移动累计位移（用于生成撤销命令）
    private int _numberCounter = 1;

    // 小选区时工具条移到画面下方的扩展区
    private bool _toolbarDocked;         // 工具条是否停靠在画面下方
    private double _imgW;                // 图像宽度（物理像素）
    private double _imgH;                // 图像高度
    private double _measuredTbHeight;    // 探测到的工具条自然高度
    private const double ToolbarGap = 8; // 工具条与画面之间的间距

    /// <summary>用户确认完成（参数为合成后的最终图片）</summary>
    public event Action<BitmapSource>? Confirmed;
    /// <summary>用户请求钉住</summary>
    public event Action<BitmapSource>? PinRequested;
    /// <summary>用户请求保存</summary>
    public event Action<BitmapSource>? SaveRequested;
    /// <summary>用户请求复制</summary>
    public event Action<BitmapSource>? CopyRequested;
    /// <summary>用户取消</summary>
    public event Action? Cancelled;

    public AnnotationEditorWindow(BitmapSource baseImage)
    {
        _baseImage = baseImage;

        // ---- 窗口外观：无边框、置顶、透明背景，大小 = 图像 ----
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        _imgW = baseImage.PixelWidth;
        _imgH = baseImage.PixelHeight;
        Width = _imgW;
        Height = _imgH;

        // ---- 布局：Grid 承载画布 + 悬浮工具条 ----
        var root = new Grid();

        _layer = new Canvas
        {
            Width = baseImage.PixelWidth,
            Height = baseImage.PixelHeight,
            ClipToBounds = true,
        };
        // 底图作为画布背景
        _layer.Background = new ImageBrush(baseImage) { Stretch = Stretch.None };
        root.Children.Add(_layer);

        // 选区边框：截图常与背景（白网页/文档）融为一体看不清范围，加紫色描边 + 轻阴影突出边界
        _frame = new Border
        {
            Width = baseImage.PixelWidth,
            Height = baseImage.PixelHeight,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x6A, 0xF0)),
            BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,   // 不拦截鼠标，标注照常绘制
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.35,
                Color = Color.FromRgb(0x4A, 0x55, 0x78),
            },
        };
        root.Children.Add(_frame);

        // 工具条：默认悬浮在画面底部居中；若选区太窄放不下，则停靠到画面下方扩展区
        _toolbar = new AnnotationToolbar
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
        };
        root.Children.Add(_toolbar);

        Content = root;

        // 布局完成后测量工具条宽度，放不下则改用停靠布局
        Loaded += (_, _) => ProbeToolbarHeight(root);

        // ---- 事件订阅 ----
        HookToolbar();
        HookMouse();
        HookKeys();

        _history.Changed += Redraw;

        Focusable = true;
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    // ---------------- 工具条布局 ----------------
    /// <summary>
    /// 第一次 Loaded 时窗口高 = 图像高，工具条被 VerticalAlignment=Bottom 约束，
    /// 测不到自然高度。这里先用无限高度探测其真实高度，再交给 <see cref="ArrangeToolbar"/>。
    /// </summary>
    private void ProbeToolbarHeight(Grid root)
    {
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _measuredTbHeight = _toolbar.DesiredSize.Height;
        ArrangeToolbar(root);
    }

    /// <summary>
    /// 决定工具条摆放：默认悬浮在画面底部居中；当选区宽度放不下整条工具条时，
    /// 改为停靠在画面下方扩展区（窗口随之加高），避免被窗口裁掉。
    /// 再根据屏幕边界微调窗口位置，防止工具条/画面超出可视区。
    /// </summary>
    private void ArrangeToolbar(Grid root)
    {
        // 探测自然宽度（高度已在 ProbeToolbarHeight 中测好）
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tbW = _toolbar.DesiredSize.Width;
        double tbH = _measuredTbHeight;

        // 留一点余量：选区宽度不足时认为放不下
        _toolbarDocked = _imgW < tbW + 8;

        if (_toolbarDocked)
        {
            // 扩展窗口：宽取画面与工具条的较大者，高 = 画面 + 间距 + 工具条
            Width = Math.Max(_imgW, tbW);
            Height = _imgH + ToolbarGap + tbH;

            // 画面与边框整体移到顶部居中（高度仍是图像尺寸，下方留白给工具条）
            _layer.HorizontalAlignment = HorizontalAlignment.Center;
            _layer.VerticalAlignment = VerticalAlignment.Top;
            if (_frame != null)
            {
                _frame.HorizontalAlignment = HorizontalAlignment.Center;
                _frame.VerticalAlignment = VerticalAlignment.Top;
            }

            // 工具条改为停靠在窗口底部居中
            _toolbar.HorizontalAlignment = HorizontalAlignment.Center;
            _toolbar.VerticalAlignment = VerticalAlignment.Bottom;
            _toolbar.Margin = new Thickness(0);
        }

        // ---- 屏幕边界微调：防止扩展/悬浮后超出可视区 ----
        var wa = SystemParameters.WorkArea;
        // 左/右：水平方向若超出工作区，平移回来
        if (Left < wa.Left) Left = wa.Left;
        if (Left + Width > wa.Right) Left = Math.Max(wa.Left, wa.Right - Width);
        // 下方：若底部超出，优先上移窗口；仍超出（图像本身较高）则贴工作区顶部
        if (Top + Height > wa.Bottom)
            Top = Math.Max(wa.Top, wa.Bottom - Height);
        if (Top < wa.Top) Top = wa.Top;
    }

    // ---------------- 工具条事件 ----------------
    private void HookToolbar()
    {
        _toolbar.ToolChanged += t =>
        {
            _tool = t;
            _selected = null;
            Cursor = t == AnnotationTool.None ? Cursors.Arrow : Cursors.Cross;
        };
        _toolbar.ColorChanged += c => _color = c;
        _toolbar.ThicknessChanged += t => _thickness = t;

        _toolbar.UndoRequested += () => _history.Undo();
        _toolbar.RedoRequested += () => _history.Redo();
        _toolbar.DeleteRequested += DeleteSelected;

        _toolbar.ConfirmRequested += () => Finish(Confirmed);
        _toolbar.PinRequested += () => Finish(PinRequested, keepOpen: false);
        _toolbar.SaveRequested += () => SaveRequested?.Invoke(Compose());
        _toolbar.CopyRequested += () => CopyRequested?.Invoke(Compose());
        _toolbar.CancelRequested += () => { Close(); Cancelled?.Invoke(); };
    }

    // ---------------- 键盘 ----------------
    private void HookKeys()
    {
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_textInput != null) { CommitTextInput(); }
                else { Close(); Cancelled?.Invoke(); }
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _history.Undo();
            else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _history.Redo();
            else if (e.Key == Key.Delete) DeleteSelected();
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                Finish(Confirmed);
        };
    }

    // ---------------- 鼠标绘制 ----------------
    private void HookMouse()
    {
        _layer.MouseLeftButtonDown += OnDown;
        _layer.MouseMove += OnMove;
        _layer.MouseLeftButtonUp += OnUp;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        // 若正在输入文字，先提交
        if (_textInput != null) { CommitTextInput(); return; }

        var pos = e.GetPosition(_layer);
        _startPoint = pos;
        _layer.CaptureMouse();

        // 选择工具（或按住 Ctrl）：优先命中已有标注进入移动模式
        var hit = HitTest(pos);
        if (hit != null && (_tool == AnnotationTool.None || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            _selected = hit;
            _moveTotal = new Vector(0, 0);
            _drawing = false;
            Cursor = Cursors.SizeAll;
            Redraw();
            return;
        }

        // 开始绘制新标注
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
                _active = new NumberAnnotation { Center = pos, Number = _numberCounter, StrokeColor = _color };
                _history.Do(new AddAnnotationCommand(_annotations, _active));
                _numberCounter++;
                _active = null;
                _drawing = false;
                break;
            case AnnotationTool.Mosaic:
                // 马赛克需采样底图像素：拖拽时只显示预览框，松手时生成
                break;
            case AnnotationTool.Highlight:
                _active = new HighlightAnnotation { Bounds = new Rect(pos, pos), StrokeColor = _color };
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
        {
            // 拖拽类标注：先加入列表，拖动过程中实时更新
            _annotations.Add(_active);
            Redraw();
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_layer);

        // 移动选中标注（累计位移，松手时整体包成一个撤销命令）
        if (_selected != null && e.LeftButton == MouseButtonState.Pressed && Cursor == Cursors.SizeAll)
        {
            var delta = pos - _startPoint;
            _selected.Translate(delta - _moveTotal); // 增量平移
            _moveTotal = delta;
            Redraw();
            return;
        }

        if (!_drawing || e.LeftButton != MouseButtonState.Pressed) return;

        // 马赛克拖拽：仅更新预览框（松手时才采样生成）
        if (_tool == AnnotationTool.Mosaic)
        {
            Redraw();
            UpdatePreview(NormalizeRect(_startPoint, pos, e, out _));
            return;
        }

        if (_active == null) return;

        switch (_active)
        {
            case RectAnnotation r:
                r.Bounds = NormalizeRect(_startPoint, pos, e, out _);
                break;
            case EllipseAnnotation el:
                el.Bounds = NormalizeRect(_startPoint, pos, e, out _);
                break;
            case HighlightAnnotation hl:
                hl.Bounds = NormalizeRect(_startPoint, pos, e, out _);
                break;
            case ArrowAnnotation a:
                a.End = SnapIfShift(_startPoint, pos, e);
                break;
            case PenAnnotation p:
                p.Points.Add(pos);
                break;
        }
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _layer.ReleaseMouseCapture();
        Cursor = Cursors.Cross;

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
            Redraw();
            return;
        }

        // 马赛克：松手时对拖拽区域采样生成
        if (_drawing && _tool == AnnotationTool.Mosaic)
        {
            _drawing = false;
            var rect = NormalizeRect(_startPoint, e.GetPosition(_layer), e, out _);
            HidePreview();
            Redraw();
            if (rect.Width >= 4 && rect.Height >= 4)
            {
                // 粗细档位映射为马赛克块大小
                int block = _thickness switch { <= 1 => 6, >= 3 => 20, _ => 12 };
                _history.Do(new AddAnnotationCommand(_annotations,
                    new MosaicAnnotation(_baseImage, rect, block)));
            }
            return;
        }

        if (!_drawing || _active == null) return;
        _drawing = false;

        // 拖拽类标注已在 Down 时加入 _annotations，这里需把它包成 Add 命令以便撤销。
        // 为避免重复，先从列表移除再用命令添加。
        _annotations.Remove(_active);
        // 过滤掉过小的误触（画笔除外）
        if (_active is not PenAnnotation && _active.GetBounds().Width < 3 && _active.GetBounds().Height < 3)
        {
            _active = null;
            Redraw();
            return;
        }
        _history.Do(new AddAnnotationCommand(_annotations, _active));
        _active = null;
    }

    // ---------------- 文字输入 ----------------
    private void BeginTextInput(Point pos)
    {
        _textInput = new TextBox
        {
            MinWidth = 120,
            FontSize = 16,
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(_color),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            AcceptsReturn = false,
        };
        Canvas.SetLeft(_textInput, pos.X);
        Canvas.SetTop(_textInput, pos.Y);
        _layer.Children.Add(_textInput);
        _textInput.Focus();

        _textInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTextInput(); e.Handled = true; }
        };
        _textInput.LostFocus += (_, _) => CommitTextInput();

        _textInput.Tag = pos; // 记录插入位置
    }

    private void CommitTextInput()
    {
        if (_textInput == null) return;
        var pos = (Point)_textInput.Tag;
        var text = _textInput.Text;
        _layer.Children.Remove(_textInput);
        _textInput = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            var ann = new TextAnnotation
            {
                Text = text,
                Position = pos,
                StrokeColor = _color,
                FontSize = 16 + _thickness * 2,
            };
            _history.Do(new AddAnnotationCommand(_annotations, ann));
        }
        Keyboard.Focus(this);
    }

    // ---------------- 马赛克拖拽预览 ----------------
    private void UpdatePreview(Rect rect)
    {
        _previewRect ??= new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
        };
        if (!_layer.Children.Contains(_previewRect))
            _layer.Children.Add(_previewRect);
        Canvas.SetLeft(_previewRect, rect.X);
        Canvas.SetTop(_previewRect, rect.Y);
        _previewRect.Width = rect.Width;
        _previewRect.Height = rect.Height;
    }

    private void HidePreview()
    {
        if (_previewRect != null)
            _layer.Children.Remove(_previewRect);
    }

    // ---------------- 命中测试 ----------------
    private Annotation? HitTest(Point pos)
    {
        // 从最上层开始找包含点的标注（包围盒 + 阈值）
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            var b = _annotations[i].GetBounds();
            b.Inflate(6, 6);
            if (b.Contains(pos)) return _annotations[i];
        }
        return null;
    }

    // ---------------- 删除选中 ----------------
    /// <summary>删除当前选中的标注（工具栏 🗑 按钮与 Delete 键共用）</summary>
    private void DeleteSelected()
    {
        if (_selected == null) return;
        _history.Do(new DeleteAnnotationCommand(_annotations, _selected));
        _selected = null;
        Redraw();
    }

    // ---------------- 重绘 ----------------
    private void Redraw()
    {
        // 清除所有可视化（保留底图背景由 ImageBrush 提供）
        _layer.Children.Clear();

        var visual = new AnnotationLayerVisual(_annotations, _selected);
        _layer.Children.Add(visual);

        // 重新挂上文字输入框（若存在）
        if (_textInput != null && !_layer.Children.Contains(_textInput))
            _layer.Children.Add(_textInput);
    }

    // ---------------- 合成与完成 ----------------
    private BitmapSource Compose() => AnnotationCompositor.Render(_baseImage, _annotations);

    private void Finish(Action<BitmapSource>? handler, bool keepOpen = false)
    {
        if (_textInput != null) CommitTextInput();
        var result = Compose();
        if (!keepOpen) Close();
        handler?.Invoke(result);
    }

    // ---------------- 辅助 ----------------
    private static Rect NormalizeRect(Point a, Point b, MouseEventArgs e, out bool square)
    {
        // 按住 Shift 画正方形/正圆
        square = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        double w = b.X - a.X, h = b.Y - a.Y;
        if (square)
        {
            double side = Math.Max(Math.Abs(w), Math.Abs(h));
            w = side * Math.Sign(w == 0 ? 1 : w);
            h = side * Math.Sign(h == 0 ? 1 : h);
        }
        return new Rect(
            Math.Min(a.X, a.X + w),
            Math.Min(a.Y, a.Y + h),
            Math.Abs(w), Math.Abs(h));
    }

    private static Point SnapIfShift(Point start, Point current, MouseEventArgs e)
    {
        // 按住 Shift 箭头吸附到 45° 步进
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return current;
        var d = current - start;
        double angle = Math.Atan2(d.Y, d.X);
        double snap = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        double len = d.Length;
        return new Point(start.X + Math.Cos(snap) * len, start.Y + Math.Sin(snap) * len);
    }

    /// <summary>标注绘制层：把所有标注画到一个 FrameworkElement 上</summary>
    private class AnnotationLayerVisual : FrameworkElement
    {
        private readonly List<Annotation> _annotations;
        private readonly Annotation? _selected;

        public AnnotationLayerVisual(List<Annotation> annotations, Annotation? selected)
        {
            _annotations = annotations;
            _selected = selected;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            foreach (var ann in _annotations)
            {
                ann.Render(dc, 1.0);
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
}
