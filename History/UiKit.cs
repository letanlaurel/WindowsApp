using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SnipPin.Core.Native;

namespace SnipPin.History;

/// <summary>
/// 共享 UI 工具：字体、配色画布、线条图标、圆角按钮模板等。
/// 历史窗 / 录屏面板 / 录屏相关弹窗统一使用（各视图 using static 引入）。
/// </summary>
internal static class UiKit
{
    // ---- 字体 ----
    internal const string UiFont = "Segoe UI Variable Text, Segoe UI";
    internal const string TitleFont = "Segoe UI Variable Display, Segoe UI";
    internal const string KbdFont = "Cascadia Mono, Consolas";

    // ---- 卡片尺寸 ----
    internal const double CardWidth = 216;
    internal const double ThumbHeight = 124;

    /// <summary>创建并冻结纯色画刷</summary>
    internal static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>线条图标（24 基准几何，统一圆角圆帽描边）</summary>
    /// <remarks>Path 几何带 offset（如 Stop 在 (7,7)-(17,17)），直接放进 Viewbox 会按 bounds
    /// 而非 24×24 设计基准缩放，导致图标整体偏移（实测停止按钮图标比文字低 2.5px）。
    /// 必须套一层 24×24 的 Grid 让几何按设计基准参与布局。</remarks>
    internal static Viewbox LineIcon(string data, double size, Brush stroke, double thickness = 1.7) => new()
    {
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Child = new Grid
        {
            Width = 24,
            Height = 24,
            Children =
            {
                new Path
                {
                    Data = Geometry.Parse(data),
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Fill = Brushes.Transparent,
                },
            },
        },
    };

    /// <summary>取线条图标内部的 Path（用于悬停换描边色等）</summary>
    /// <remarks>LineIcon 的 Viewbox 内是 24×24 Grid 包 Path，不能直接强转 Child。</remarks>
    internal static Path IconPath(Viewbox icon) => (Path)((Grid)icon.Child).Children[0];

    /// <summary>按钮内容：图标 + 文字（+ 可选快捷键胶囊）</summary>
    internal static StackPanel ActionContent(
        string icon, Brush iconStroke, string label, Brush labelBrush, UIElement? kbd = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var iconEl = LineIcon(icon, 15, iconStroke);
        // 横排 StackPanel 中 Viewbox 默认顶对齐会被拉伸，导致图标比文字轻微下沉，显式垂直居中
        iconEl.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(iconEl);
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = labelBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), // 图标与文字间距
        });
        if (kbd != null)
            sp.Children.Add(kbd);
        return sp;
    }

    /// <summary>快捷键胶囊：小号等宽字，深浅两套配色</summary>
    internal static (Border Host, TextBlock Label) KbdBadge(bool primary)
    {
        var label = new TextBlock
        {
            FontSize = 9.5,
            FontFamily = new FontFamily(KbdFont),
        };
        var host = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(7, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        if (primary)
        {
            host.Background = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
            label.Foreground = Frozen(0xEC, 0xE9, 0xFD);
        }
        else
        {
            host.Background = Frozen(0xEC, 0xEE, 0xF6);
            label.Foreground = Frozen(0x82, 0x89, 0x9E);
        }
        return (host, label);
    }

    /// <summary>卡片悬停操作小按钮：半透明白圆角底</summary>
    internal static Button MiniIconButton(string iconData, string tooltip) => new()
    {
        Width = 28,
        Height = 26,
        Padding = new Thickness(0),
        Margin = new Thickness(2, 0, 0, 0),
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        Cursor = Cursors.Hand,
        ToolTip = tooltip,
        Template = ButtonTemplate(8, "#F2FFFFFF", "#FFFFFF"),
        Content = LineIcon(iconData, 13, Frozen(0x4A, 0x52, 0x65)),
    };

    /// <summary>
    /// 圆角按钮模板：常态仅显示内容（透明）；悬停/按下时覆盖层淡入着色，松开后淡出恢复透明。
    /// 关键：ContentPresenter 绑定 Padding，否则内边距被模板吞掉、文字紧贴边框。
    /// </summary>
    internal static ControlTemplate ButtonTemplate(int radius, string hover, string pressed)
    {
        var hov = ParseBrush(hover);
        var pre = ParseBrush(pressed);
        var tt = new ControlTemplate(typeof(Button));

        // 边框层（背景/描边）。描边固定为 0（去边框），仅保留背景与圆角
        var bg = new FrameworkElementFactory(typeof(Border));
        bg.Name = "bg";
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        bg.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        bg.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        bg.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        // Grid 根：叠加 边框层 + 覆盖层 + 内容（Border 仅单子元素，需用 Grid 承载多层）
        var rootGrid = new FrameworkElementFactory(typeof(Grid));
        rootGrid.AppendChild(bg);

        // 悬停/按下覆盖层（透明渐变淡入，边框色圆角与外层一致）
        var overlay = new FrameworkElementFactory(typeof(Border));
        overlay.Name = "overlay";
        overlay.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        overlay.SetValue(Border.BackgroundProperty, hov);
        overlay.SetValue(UIElement.OpacityProperty, 0.0);
        rootGrid.AppendChild(overlay);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, false);
        // 绑定按钮 Padding —— 否则模板根撑满、内容居中，内边距完全不生效
        cp.SetBinding(FrameworkElement.MarginProperty,
            new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        rootGrid.AppendChild(cp);

        tt.VisualTree = rootGrid;

        // 悬停显示 / 移开隐藏：声明式 Setter（条件离开时属性系统自动还原本地值 0）。
        // 此前用 EnterActions/ExitActions 动画，点击（弹窗/菜单）后动画时钟可能残留，
        // 导致鼠标移开后背景色不消失；Setter 不经过动画时钟，绝无残留。
        var enter = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        enter.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "overlay"));
        tt.Triggers.Add(enter);

        // 按下时换成更深色（按住期间鼠标被捕获，覆盖层保持显示；松开自动还原悬停色）
        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "overlay"));
        press.Setters.Add(new Setter(Border.BackgroundProperty, pre, "overlay"));
        tt.Triggers.Add(press);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        tt.Triggers.Add(disabled);

        return tt;
    }

    internal static BeginStoryboard FadeBgAction(string target, double to, Brush? brush = null)
    {
        var board = new Storyboard();
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTargetName(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath("Opacity"));
        board.Children.Add(anim);
        if (brush != null)
        {
            // 按下瞬间切换到更深色（Duration=0 立即生效）
            var colorAnim = new ObjectAnimationUsingKeyFrames
            {
                KeyFrames = { new DiscreteObjectKeyFrame(brush, KeyTime.FromTimeSpan(TimeSpan.Zero)) },
            };
            Storyboard.SetTargetName(colorAnim, target);
            Storyboard.SetTargetProperty(colorAnim, new PropertyPath("Background"));
            board.Children.Add(colorAnim);
        }
        return new BeginStoryboard { Storyboard = board };
    }

    /// <summary>刷新全局鼠标悬停状态：原地重设光标坐标触发一次 WM_MOUSEMOVE，
    /// 让 WPF 重算 IsMouseOver。用于窗口重新激活时——此前若主窗口失活期间鼠标已移走，
    /// 按钮的 IsMouseOver 会冻结在 true，悬停背景一直显示。</summary>
    internal static void RefreshMouseHover()
    {
        try
        {
            if (NativeMethods.GetCursorPos(out var p))
                NativeMethods.SetCursorPos(p.X, p.Y);
        }
        catch { /* 刷新失败不影响主流程 */ }
    }

    /// <summary>自绘上下文菜单样式（圆角白底 + 阴影，历史窗清空菜单/录屏右键菜单共用）</summary>
    internal static Style MenuStyle => (Style)XamlReader.Parse("""
        <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContextMenu'>
            <Setter Property='OverridesDefaultStyle' Value='True'/>
            <Setter Property='Foreground' Value='#3A4150'/>
            <Setter Property='FontSize' Value='12.5'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='ContextMenu'>
                        <Border Margin='8' Background='#FFFFFF' CornerRadius='12'
                                BorderBrush='#E4E7F2' BorderThickness='1' Padding='6'>
                            <Border.Effect>
                                <DropShadowEffect BlurRadius='14' ShadowDepth='0' Opacity='0.14' Color='#4A5578'/>
                            </Border.Effect>
                            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """);

    /// <summary>自绘菜单项样式（悬停淡紫底，需配合 MenuStyle 使用）</summary>
    internal static Style MenuItemStyle => (Style)XamlReader.Parse("""
        <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='MenuItem'>
            <Setter Property='OverridesDefaultStyle' Value='True'/>
            <Setter Property='Foreground' Value='#3A4150'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='MenuItem'>
                        <Border Name='bg' CornerRadius='8' Padding='9,7,14,7' Background='Transparent' SnapsToDevicePixels='True'>
                            <StackPanel Orientation='Horizontal'>
                                <ContentPresenter ContentSource='Icon' VerticalAlignment='Center' Margin='0,0,8,0'/>
                                <ContentPresenter ContentSource='Header' VerticalAlignment='Center'/>
                            </StackPanel>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsHighlighted' Value='True'>
                                <Setter TargetName='bg' Property='Background' Value='#F1EFFD'/>
                            </Trigger>
                            <Trigger Property='IsEnabled' Value='False'>
                                <Setter Property='Opacity' Value='0.45'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """);

    internal static Brush ParseBrush(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    /// <summary>对可动画属性做缓动过渡（卡片上浮/阴影加深）</summary>
    internal static void AnimateDouble(Animatable target, DependencyProperty property, double to, int ms = 150)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        target.BeginAnimation(property, anim);
    }

    /// <summary>24×24 基准的圆角线条图标几何数据</summary>
    internal static class Icons
    {
        public const string Camera =
            "M7.2 6l1.3-1.9h6.9L16.7 6h.8A2.5 2.5 0 0 1 20 8.5v7A2.5 2.5 0 0 1 17.5 18h-11A2.5 2.5 0 0 1 4 15.5v-7A2.5 2.5 0 0 1 6.5 6z M12 14.8a2.8 2.8 0 1 0 0-5.6 2.8 2.8 0 0 0 0 5.6z";
        public const string Clipboard =
            "M9 4h6v2.2H9z M16.3 6h1.2A2.5 2.5 0 0 1 20 8.5v9A2.5 2.5 0 0 1 17.5 20h-11A2.5 2.5 0 0 1 4 17.5v-9A2.5 2.5 0 0 1 6.5 6h1.2 M9 11.5h6 M9 15h4";
        public const string Monitor =
            "M5 7A2.5 2.5 0 0 1 7.5 4.5h9A2.5 2.5 0 0 1 19 7v6.5a2.5 2.5 0 0 1-2.5 2.5h-9A2.5 2.5 0 0 1 5 13.5z M9.5 20h5 M12 16v4";
        public const string CheckSquare =
            "M7.5 4.5h9A3 3 0 0 1 19.5 7.5v9a3 3 0 0 1-3 3h-9a3 3 0 0 1-3-3v-9a3 3 0 0 1 3-3z M8.7 12.2l2.3 2.3 4.5-5";
        public const string Sliders =
            "M5 4.5v5 M5 13.5v6 M12 4.5v9 M12 17.5v2 M19 4.5v2 M19 10.5v9 M3 9.5h4 M10 13.5h4 M17 6.5h4";
        public const string Trash =
            "M4.5 7h15 M9.5 7V5.5A1.5 1.5 0 0 1 11 4h2a1.5 1.5 0 0 1 1.5 1.5V7 M7.5 7l.7 11.1A2 2 0 0 0 10.2 20h3.6a2 2 0 0 0 2-1.9L16.5 7 M10 10.5v6 M14 10.5v6";
        public const string Archive =
            "M4.5 4.5h15V8h-15z M6 8h12v9.5A2.5 2.5 0 0 1 15.5 20h-7A2.5 2.5 0 0 1 6 17.5z M10 12h4";
        public const string Pin =
            "M9.2 4.2h5.6l-.6 5.9 2.9 3H6.9l2.9-3z M12 13.1V20";
        public const string Download =
            "M12 4v10.5 M7.8 10.3 12 14.5l4.2-4.2 M5 19.5h14";
        public const string Image =
            "M6 5.5h12A1.5 1.5 0 0 1 19.5 7v10a1.5 1.5 0 0 1-1.5 1.5H6A1.5 1.5 0 0 1 4.5 17V7A1.5 1.5 0 0 1 6 5.5z M4.5 15.5l4.2-4.2 3.3 3.3 2.6-2.6 4.9 4.9 M15.6 9.5h.01";
        public const string Calendar =
            "M7.5 3.5v3 M16.5 3.5v3 M4.5 9h15 M6 5.5h12A1.5 1.5 0 0 1 19.5 7v11a1.5 1.5 0 0 1-1.5 1.5H6A1.5 1.5 0 0 1 4.5 18V7A1.5 1.5 0 0 1 6 5.5z";
        public const string ChevronDown = "M6.5 9.5l5 5 5-5";
        public const string Minimize = "M5.5 12h13";
        public const string Maximize = "M6.5 6.5h11v11h-11z";
        public const string Restore =
            "M8.5 8.5h9v9h-9z M15.5 8.5V6.2a1.7 1.7 0 0 0-1.7-1.7H6.2a1.7 1.7 0 0 0-1.7 1.7v7.6a1.7 1.7 0 0 0 1.7 1.7h2.3";
        public const string Close = "M6.5 6.5l11 11 M17.5 6.5l-11 11";
        // ---- 录屏相关 ----
        public const string Video =
            "M4.5 7.5A2.5 2.5 0 0 1 7 5h7a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5H7a2.5 2.5 0 0 1-2.5-2.5z M16.5 10.2l3-2v7.6l-3-2";
        public const string Play =
            "M8.5 5.5l10 6.5-10 6.5z";
        public const string Stop =
            "M7 7h10v10H7z";
        public const string Folder =
            "M4.5 6.5A2 2 0 0 1 6.5 4.5h3.2l2 2.3h6a2 2 0 0 1 2 2v8.2a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2z";
        public const string Search =
            "M10.5 4.5a6 6 0 1 0 0 12 6 6 0 0 0 0-12z M15 15l4.5 4.5";
        public const string Pause =
            "M9 6.5v11 M15 6.5v11";
    }
}
