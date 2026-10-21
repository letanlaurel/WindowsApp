using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SnipPin.Ocr;

/// <summary>
/// OCR 识别结果浮窗：识别中 / 结果 / 错误三态；文本可编辑，一键复制全部。
/// 深色无边框圆角（与工具条/贴图风格一致），标题栏可拖动，Esc 或 ✕ 关闭。
/// </summary>
public class OcrResultWindow : Window
{
    private readonly TextBox _text;
    private readonly Button _copyButton;

    // 深色配色（与标注工具条同系）
    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush BorderBrushColor = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush FgBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly Brush InputBgBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    public OcrResultWindow()
    {
        // ---- 窗口外观：无边框、置顶、透明圆角、随内容自适应 ----
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // ---- 标题栏（可拖动）----
        var title = new TextBlock
        {
            Text = "🔤 识别结果",
            Foreground = FgBrush,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 22,
            FontSize = 12,
            Foreground = FgBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Close();
        var titleBar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(close, Dock.Right);
        titleBar.Children.Add(close);
        titleBar.Children.Add(title);
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        // ---- 文本区（识别中只读占位，出结果后可编辑）----
        _text = new TextBox
        {
            Width = 520,
            Height = 320,
            FontSize = 13.5,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = FgBrush,
            Background = InputBgBrush,
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8),
            IsReadOnly = true,
            Text = "⏳ 正在识别，请稍候…",
        };

        // ---- 底部按钮条 ----
        _copyButton = new Button
        {
            Content = "📋 复制全部",
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12.5,
            Foreground = FgBrush,
            Background = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x3D)),
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        var hint = new TextBlock
        {
            Text = "文本可编辑，修改后复制",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var bottomBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        bottomBar.Children.Add(_copyButton);
        bottomBar.Children.Add(hint);

        _copyButton.Click += async (_, _) =>
        {
            var content = _text.Text;
            if (content.Length == 0) return; // 空文本 SetText 会抛异常
            try
            {
                Clipboard.SetText(content); // 结果窗此刻为前台，可直接写入
                _copyButton.Content = "✓ 已复制";
                await Task.Delay(1500);
                _copyButton.Content = "📋 复制全部";
            }
            catch (Exception ex)
            {
                Program.LogError("OCR 复制", ex);
            }
        };

        // ---- 组装 ----
        var root = new StackPanel();
        root.Children.Add(titleBar);
        root.Children.Add(_text);
        root.Children.Add(bottomBar);

        Content = new Border
        {
            Background = BgBrush,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = root,
        };
    }

    /// <summary>显示识别结果并允许编辑</summary>
    public void ShowResult(string text)
    {
        _text.IsReadOnly = false;
        _text.Text = string.IsNullOrEmpty(text) ? "（未识别到文字）" : text;
        _text.Focus();
        _text.SelectAll();
    }

    /// <summary>显示错误信息（允许复制错误内容便于反馈）</summary>
    public void ShowError(string message)
    {
        _text.IsReadOnly = false;
        _text.Text = $"❌ 识别失败：{message}";
    }
}
