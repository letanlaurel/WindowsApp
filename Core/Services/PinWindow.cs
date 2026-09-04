using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnipPin.Core.Configuration;

namespace SnipPin.Core.Services;

/// <summary>
/// 贴图悬浮窗：无边框、置顶、可拖动/缩放/旋转/调透明度的图片窗。
/// 纯代码构建（无需 XAML），便于动态创建多张。
/// </summary>
public class PinWindow : Window
{
    private readonly Image _image;
    private readonly PinConfig _config;

    private double _scale = 1.0;
    private double _rotation = 0;
    private bool _alwaysOnTop;

    private const double MinScale = 0.1;
    private const double MaxScale = 5.0;

    // ---------------- 对外状态（会话持久化用） ----------------
    /// <summary>贴图原始图片（未缩放）</summary>
    public BitmapSource ImageSource => (BitmapSource)_image.Source;
    /// <summary>当前缩放系数</summary>
    public double CurrentScale => _scale;
    /// <summary>当前旋转角度（度）</summary>
    public double CurrentRotation => _rotation;

    /// <summary>恢复会话状态（缩放/旋转/透明度/置顶）</summary>
    public void RestoreState(PinState state)
    {
        SetScale(state.Scale);
        if (Math.Abs(state.Rotation) > 0.01)
        {
            _rotation = state.Rotation;
            _image.LayoutTransform = new RotateTransform(_rotation);
        }
        Opacity = state.Opacity;
        _alwaysOnTop = state.AlwaysOnTop;
        Topmost = _alwaysOnTop;
    }

    public PinWindow(BitmapSource image, PinConfig config)
    {
        _config = config;
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
        // 填满屏幕，导致贴图“超级大”；改为 SetScale(1.0) 固定宽高，显示原始大小）
        _image = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
        };
        // 缩放时保持清晰（RenderOptions 为附加属性，需静态设置）
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        // 布局变换用于旋转，避免影响拖动
        _image.LayoutTransform = new RotateTransform(0);

        // 初始即按原图大小布局（_scale = 1.0）
        SetScale(1.0);

        // 容器：阴影 + 圆角边框（悬停时显示）
        var border = new Border
        {
            Child = _image,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.4,
                Color = Colors.Black
            }
        };
        Content = border;

        // 悬停显示细边框提示可交互
        MouseEnter += (_, _) => border.BorderThickness = new Thickness(1);
        MouseLeave += (_, _) => border.BorderThickness = new Thickness(0);

        // ---- 交互 ----
        // 左键拖动
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        // 双击关闭
        MouseDoubleClick += (_, _) => Close();
        // Esc 关闭
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        // 滚轮：缩放（Ctrl）/ 旋转（Shift）/ 透明度（Alt）
        PreviewMouseWheel += OnMouseWheel;

        // 右键菜单
        ContextMenu = BuildContextMenu();

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
        var src = (BitmapSource)_image.Source;
        _image.Width = src.PixelWidth * _scale / dpiScale;
        _image.Height = src.PixelHeight * _scale / dpiScale;
    }

    private void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, MinScale, MaxScale);
        ApplySize();
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
            _image.LayoutTransform = new RotateTransform(_rotation);
            e.Handled = true;
        }
        else if (mods.HasFlag(ModifierKeys.Alt))
        {
            // 透明度
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.05 : -0.05), 0.1, 1.0);
            e.Handled = true;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var topmost = new MenuItem { Header = "始终置顶", IsCheckable = true, IsChecked = _alwaysOnTop };
        topmost.Click += (_, _) => { _alwaysOnTop = topmost.IsChecked; Topmost = _alwaysOnTop; };

        var resetScale = new MenuItem { Header = "还原大小" };
        resetScale.Click += (_, _) => SetScale(1.0);

        var copy = new MenuItem { Header = "复制到剪贴板" };
        copy.Click += (_, _) => Clipboard.SetImage((BitmapSource)_image.Source);

        var save = new MenuItem { Header = "另存为..." };
        save.Click += (_, _) => SaveAs();

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();

        menu.Items.Add(topmost);
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

        var src = (BitmapSource)_image.Source;
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
