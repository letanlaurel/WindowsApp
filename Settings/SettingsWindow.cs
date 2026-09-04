using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SnipPin.Core.Configuration;

namespace SnipPin.Settings;

/// <summary>
/// 设置窗体：常规 / 热键 / 保存 / 截图 / 贴图 五组配置的编辑。
/// 保存后写回 ConfigService 并触发 SettingsSaved（由 MainWindow 重新注册热键等）。
/// </summary>
public class SettingsWindow : Window
{
    private readonly ConfigService _config;

    /// <summary>用户点击保存并已写回配置</summary>
    public event Action? SettingsSaved;

    // ---- 控件字段 ----
    private CheckBox _autoStart = null!;
    private CheckBox _restoreSession = null!;
    private TextBox _hkCapture = null!;
    private TextBox _hkPinClipboard = null!;
    private TextBox _hkFullScreen = null!;
    private TextBox _hkTogglePins = null!;
    private TextBox _saveDir = null!;
    private TextBox _nameTemplate = null!;
    private ComboBox _format = null!;
    private Slider _jpgQuality = null!;
    private CheckBox _windowSnap = null!;
    private CheckBox _magnifier = null!;
    private Slider _maskOpacity = null!;
    private CheckBox _pinTopmost = null!;
    private Slider _pinOpacity = null!;
    private TextBox _historyDir = null!;
    private TextBox _historyLimit = null!;

    private const string AppRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public SettingsWindow(ConfigService config)
    {
        _config = config;

        Title = "TLSnipPin 设置";
        Icon = Program.LoadAppIcon();
        Width = 520;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;

        Content = BuildContent();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 滚动区承载所有配置组
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

        panel.Children.Add(BuildGeneralGroup());
        panel.Children.Add(BuildHotkeyGroup());
        panel.Children.Add(BuildSaveGroup());
        panel.Children.Add(BuildHistoryGroup());
        panel.Children.Add(BuildCaptureGroup());
        panel.Children.Add(BuildPinGroup());

        scroll.Content = panel;
        Grid.SetRow(scroll, 0);
        root.Children.Add(scroll);

        // 底部按钮
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var ok = new Button { Content = "保存", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => OnSave();
        var cancel = new Button { Content = "取消", Width = 90 };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        return root;
    }

    // ---------------- 常规 ----------------
    private UIElement BuildGeneralGroup()
    {
        _autoStart = new CheckBox { Content = "开机自动启动", IsChecked = _config.Config.General.AutoStart };
        _restoreSession = new CheckBox
        {
            Content = "退出时保留贴图会话（下次启动恢复）",
            IsChecked = _config.Config.Pin.RestoreSession,
        };
        return Group("常规", _autoStart, _restoreSession);
    }

    // ---------------- 热键 ----------------
    private UIElement BuildHotkeyGroup()
    {
        var hk = _config.Config.Hotkeys;
        _hkCapture = HotkeyBox(hk.Capture);
        _hkPinClipboard = HotkeyBox(hk.PinClipboard);
        _hkFullScreen = HotkeyBox(hk.FullScreen);
        _hkTogglePins = HotkeyBox(hk.TogglePins);

        return Group("热键（点击输入框后按下新快捷键，Esc 清空）",
            Row("区域截图", _hkCapture),
            Row("钉住剪贴板图片", _hkPinClipboard),
            Row("全屏截图", _hkFullScreen),
            Row("显示/隐藏贴图", _hkTogglePins));
    }

    // ---------------- 保存 ----------------
    private UIElement BuildSaveGroup()
    {
        var save = _config.Config.Save;
        _saveDir = new TextBox { Text = save.Directory, Width = 260 };
        var browse = new Button { Content = "浏览...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "选择保存目录" };
            if (dlg.ShowDialog(this) == true)
                _saveDir.Text = dlg.FolderName;
        };
        var dirPanel = new StackPanel { Orientation = Orientation.Horizontal };
        dirPanel.Children.Add(_saveDir);
        dirPanel.Children.Add(browse);

        _nameTemplate = new TextBox { Text = save.NameTemplate, Width = 260 };

        _format = new ComboBox { Width = 120 };
        _format.Items.Add("png");
        _format.Items.Add("jpg");
        _format.Items.Add("bmp");
        _format.SelectedItem = save.Format.ToLowerInvariant();

        _jpgQuality = new Slider
        {
            Minimum = 50, Maximum = 100, Value = save.JpgQuality,
            Width = 160, IsSnapToTickEnabled = true, TickFrequency = 1,
        };
        var qualityPanel = new StackPanel { Orientation = Orientation.Horizontal };
        qualityPanel.Children.Add(_jpgQuality);
        var qualityLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        qualityLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = _jpgQuality });
        qualityPanel.Children.Add(qualityLabel);

        return Group("保存",
            Row("保存目录", dirPanel),
            Row("命名模板", _nameTemplate),
            Row("默认格式", _format),
            Row("JPG 质量", qualityPanel));
    }

    // ---------------- 历史 ----------------
    private UIElement BuildHistoryGroup()
    {
        var his = _config.Config.History;
        _historyDir = new TextBox { Text = his.Directory, Width = 240 };
        var browse = new Button { Content = "浏览...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "选择历史数据目录" };
            if (dlg.ShowDialog(this) == true)
                _historyDir.Text = dlg.FolderName;
        };
        var dirPanel = new StackPanel { Orientation = Orientation.Horizontal };
        dirPanel.Children.Add(_historyDir);
        dirPanel.Children.Add(browse);

        _historyLimit = new TextBox { Text = his.ActiveLimit.ToString(), Width = 80, ToolTip = "活跃列表条数上限，超过后最旧的自动归档到压缩包（0 = 永不归档）" };
        var limitPanel = new StackPanel { Orientation = Orientation.Horizontal };
        limitPanel.Children.Add(_historyLimit);
        limitPanel.Children.Add(new TextBlock
        {
            Text = "条，超过后归档到 archive.zip",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        });

        return Group("截图历史",
            Row("数据目录", dirPanel),
            Row("活跃上限", limitPanel));
    }

    // ---------------- 截图 ----------------
    private UIElement BuildCaptureGroup()
    {
        var cap = _config.Config.Capture;
        _windowSnap = new CheckBox { Content = "框选时智能识别窗口（悬停吸附）", IsChecked = cap.WindowSnap };
        _magnifier = new CheckBox { Content = "显示放大镜（开发中，暂未生效）", IsChecked = cap.Magnifier, IsEnabled = false };
        _maskOpacity = new Slider
        {
            Minimum = 0.1, Maximum = 0.8, Value = cap.MaskOpacity,
            Width = 160, IsSnapToTickEnabled = true, TickFrequency = 0.05,
        };
        var opPanel = new StackPanel { Orientation = Orientation.Horizontal };
        opPanel.Children.Add(_maskOpacity);
        var opLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        opLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = _maskOpacity, StringFormat = "F2" });
        opPanel.Children.Add(opLabel);

        return Group("截图", _windowSnap, _magnifier, Row("遮罩暗度", opPanel));
    }

    // ---------------- 贴图 ----------------
    private UIElement BuildPinGroup()
    {
        var pin = _config.Config.Pin;
        _pinTopmost = new CheckBox { Content = "贴图默认始终置顶", IsChecked = pin.AlwaysOnTop };
        _pinOpacity = new Slider
        {
            Minimum = 0.2, Maximum = 1.0, Value = pin.DefaultOpacity,
            Width = 160, IsSnapToTickEnabled = true, TickFrequency = 0.05,
        };
        var opPanel = new StackPanel { Orientation = Orientation.Horizontal };
        opPanel.Children.Add(_pinOpacity);
        var opLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        opLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = _pinOpacity, StringFormat = "F2" });
        opPanel.Children.Add(opLabel);

        return Group("贴图", _pinTopmost, Row("默认透明度", opPanel));
    }

    // ---------------- 保存逻辑 ----------------
    private void OnSave()
    {
        var c = _config.Config;

        c.General.AutoStart = _autoStart.IsChecked == true;
        c.Pin.RestoreSession = _restoreSession.IsChecked == true;

        c.Hotkeys.Capture = _hkCapture.Text.Trim();
        c.Hotkeys.PinClipboard = _hkPinClipboard.Text.Trim();
        c.Hotkeys.FullScreen = _hkFullScreen.Text.Trim();
        c.Hotkeys.TogglePins = _hkTogglePins.Text.Trim();

        c.Save.Directory = _saveDir.Text.Trim();
        c.Save.NameTemplate = _nameTemplate.Text.Trim();
        c.Save.Format = (_format.SelectedItem as string ?? "png").ToLowerInvariant();
        c.Save.JpgQuality = (int)_jpgQuality.Value;

        c.Capture.WindowSnap = _windowSnap.IsChecked == true;
        c.Capture.Magnifier = _magnifier.IsChecked == true;
        c.Capture.MaskOpacity = _maskOpacity.Value;

        c.Pin.AlwaysOnTop = _pinTopmost.IsChecked == true;
        c.Pin.DefaultOpacity = _pinOpacity.Value;

        // 历史：目录 + 活跃上限（解析失败回退 200）
        c.History.Directory = _historyDir.Text.Trim();
        if (!int.TryParse(_historyLimit.Text.Trim(), out int limit))
            limit = 200;
        c.History.ActiveLimit = Math.Clamp(limit, 0, 1_000_000);

        SetAutoStart(c.General.AutoStart);
        _config.Save();
        SettingsSaved?.Invoke();
        Close();
    }

    /// <summary>写入/删除 HKCU Run 注册表项实现开机自启</summary>
    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AppRunKey);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        if (enable)
            key.SetValue("TLSnipPin", $"\"{exe}\"");
        else
            key.DeleteValue("TLSnipPin", throwOnMissingValue: false);
    }

    // ---------------- 控件辅助 ----------------
    /// <summary>带标题的配置分组</summary>
    private static UIElement Group(string title, params FrameworkElement[] items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var header = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(header);
        foreach (var item in items)
        {
            item.Margin = new Thickness(0, 3, 0, 3);
            panel.Children.Add(item);
        }
        return new GroupBox { Header = header.Text, Content = panel, Margin = new Thickness(0, 0, 0, 8) };
    }

    /// <summary>标签 + 控件的行</summary>
    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>热键录入框：按下组合键即显示（如 Ctrl+Shift+F3），Esc 清空</summary>
    private static TextBox HotkeyBox(string initial)
    {
        var tb = new TextBox { Width = 180, IsReadOnly = true, Text = initial };
        tb.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Escape)
            {
                tb.Text = string.Empty; // Esc 清空表示禁用该热键
                return;
            }
            // 纯修饰键按下时不更新
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Tab)
                return;

            // 仅支持 F1-F24 / 字母 / 数字作为热键主键，其余按键给出提示
            var keyName = key.ToString();
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    keyName, @"^(F([1-9]|1[0-9]|2[0-4])|[A-Z]|D[0-9])$"))
            {
                tb.ToolTip = $"不支持的按键：{keyName}（支持 F1-F24、字母、数字）";
                return;
            }

            var parts = new List<string>();
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            parts.Add(keyName);
            tb.Text = string.Join("+", parts);
        };
        return tb;
    }
}
