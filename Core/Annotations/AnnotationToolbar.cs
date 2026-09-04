using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnipPin.Core.Annotations;

/// <summary>标注工具类型</summary>
public enum AnnotationTool
{
    /// <summary>无（选择/查看）</summary>
    None,
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Text,
    Number,
    Mosaic,
    Highlight,
}

/// <summary>
/// 标注工具条：工具选择 + 颜色/粗细 + 撤销重做 + 输出动作。
/// 纯代码构建，悬浮于标注编辑窗内。
/// </summary>
public class AnnotationToolbar : Border
{
    /// <summary>当前选中工具变化</summary>
    public event Action<AnnotationTool>? ToolChanged;
    /// <summary>颜色变化</summary>
    public event Action<Color>? ColorChanged;
    /// <summary>粗细变化（1/2/3）</summary>
    public event Action<double>? ThicknessChanged;

    public event Action? UndoRequested;
    public event Action? RedoRequested;
    /// <summary>删除当前选中的标注元素</summary>
    public event Action? DeleteRequested;
    public event Action? PinRequested;
    public event Action? SaveRequested;
    public event Action? CopyRequested;
    public event Action? CancelRequested;
    public event Action? ConfirmRequested;

    private AnnotationTool _currentTool = AnnotationTool.Rectangle;
    private Color _currentColor = Color.FromRgb(0xE5, 0x39, 0x35); // 默认红
    private double _currentThickness = 2;

    // 输出按钮显隐：贴图内嵌标注时隐藏"钉住/保存/复制"（由贴图右键菜单负责），
    // 但保留 ✕（作废）/✓（写入）作为标注会话的明确出口。
    private readonly bool _showOutputActions;
    private readonly bool _showConfirmCancel;

    // 预设颜色
    private static readonly Color[] PresetColors =
    {
        Color.FromRgb(0xE5, 0x39, 0x35), // 红
        Color.FromRgb(0xFB, 0x8C, 0x00), // 橙
        Color.FromRgb(0xFD, 0xD8, 0x35), // 黄
        Color.FromRgb(0x43, 0xA0, 0x47), // 绿
        Color.FromRgb(0x1E, 0x88, 0xE5), // 蓝
        Color.FromRgb(0x8E, 0x24, 0xAA), // 紫
        Colors.Black,
        Colors.White,
    };

    public AnnotationToolbar(bool showOutputActions = true, bool showConfirmCancel = true)
    {
        _showOutputActions = showOutputActions;
        _showConfirmCancel = showConfirmCancel;

        // 外观：圆角深色底 + 阴影
        Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(8, 6, 8, 6);
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4, Color = Colors.Black
        };

        Child = BuildPanel();
    }

    private UIElement BuildPanel()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        // ---- 工具组 ----
        panel.Children.Add(ToolButton("⬉", "选择/移动", AnnotationTool.None));
        panel.Children.Add(ToolButton("▭", "矩形", AnnotationTool.Rectangle));
        panel.Children.Add(ToolButton("◯", "椭圆", AnnotationTool.Ellipse));
        panel.Children.Add(ToolButton("➔", "箭头", AnnotationTool.Arrow));
        panel.Children.Add(ToolButton("✎", "画笔", AnnotationTool.Pen));
        panel.Children.Add(ToolButton("T", "文字", AnnotationTool.Text));
        panel.Children.Add(ToolButton("①", "序号", AnnotationTool.Number));
        panel.Children.Add(ToolButton("▦", "马赛克", AnnotationTool.Mosaic));
        panel.Children.Add(ToolButton("▬", "高亮", AnnotationTool.Highlight));

        panel.Children.Add(Divider());

        // ---- 颜色组 ----
        foreach (var c in PresetColors)
            panel.Children.Add(ColorSwatch(c));

        panel.Children.Add(Divider());

        // ---- 粗细组 ----
        panel.Children.Add(ThicknessButton("细", 1));
        panel.Children.Add(ThicknessButton("中", 2));
        panel.Children.Add(ThicknessButton("粗", 3.5));

        panel.Children.Add(Divider());

        // ---- 撤销/重做/删除选中 ----
        panel.Children.Add(ActionButton("↶", "撤销", () => UndoRequested?.Invoke()));
        panel.Children.Add(ActionButton("↷", "重做", () => RedoRequested?.Invoke()));
        panel.Children.Add(ActionButton("🗑", "删除选中元素", () => DeleteRequested?.Invoke()));

        // ---- 输出动作 ----
        if (_showOutputActions)
        {
            panel.Children.Add(Divider());
            panel.Children.Add(ActionButton("📌", "钉住", () => PinRequested?.Invoke()));
            panel.Children.Add(ActionButton("💾", "保存", () => SaveRequested?.Invoke()));
            panel.Children.Add(ActionButton("📋", "复制", () => CopyRequested?.Invoke()));
        }

        // ---- ✕ 作废 / ✓ 写入 ----
        if (_showConfirmCancel)
        {
            panel.Children.Add(Divider());
            panel.Children.Add(ActionButton("✕", "作废本次标注", () => CancelRequested?.Invoke()));
            var confirm = ActionButton("✓", "写入图片", () => ConfirmRequested?.Invoke());
            confirm.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
            panel.Children.Add(confirm);
        }

        return panel;
    }

    private Button ToolButton(string glyph, string tooltip, AnnotationTool tool)
    {
        var btn = MakeButton(glyph, tooltip);
        btn.Tag = tool;
        btn.Click += (_, _) => SetTool(tool);
        if (tool == _currentTool) HighlightTool(btn);
        return btn;
    }

    /// <summary>程序化选中工具（初始化默认工具用）</summary>
    public void SelectTool(AnnotationTool tool) => SetTool(tool);

    private void SetTool(AnnotationTool tool)
    {
        _currentTool = tool;
        // 简单处理：更新所有工具按钮高亮
        if (Child is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Button b && b.Tag is AnnotationTool)
                {
                    bool active = (AnnotationTool)b.Tag == tool;
                    b.Background = active
                        ? new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5))
                        : Brushes.Transparent;
                }
            }
        }
        ToolChanged?.Invoke(tool);
    }

    private void HighlightTool(Button btn) =>
        btn.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));

    private UIElement ColorSwatch(Color color)
    {
        var btn = new Button
        {
            Width = 20, Height = 20,
            Margin = new Thickness(2),
            ToolTip = "颜色",
            Background = new SolidColorBrush(color),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.Click += (_, _) =>
        {
            _currentColor = color;
            ColorChanged?.Invoke(color);
        };
        return btn;
    }

    private Button ThicknessButton(string label, double thickness)
    {
        var btn = MakeButton(label, $"粗细：{label}");
        btn.Click += (_, _) =>
        {
            _currentThickness = thickness;
            ThicknessChanged?.Invoke(thickness);
        };
        return btn;
    }

    private Button ActionButton(string glyph, string tooltip, Action action)
    {
        var btn = MakeButton(glyph, tooltip);
        btn.Click += (_, _) => action();
        return btn;
    }

    private static Button MakeButton(string content, string tooltip)
    {
        return new Button
        {
            Content = content,
            ToolTip = tooltip,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 15,
            MinWidth = 32,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    private static UIElement Divider() => new System.Windows.Shapes.Rectangle
    {
        Width = 1,
        Height = 18,
        Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        Margin = new Thickness(6, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
