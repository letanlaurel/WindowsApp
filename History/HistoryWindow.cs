using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using SnipPin.Core.Configuration;
using SnipPin.Core.Services;

namespace SnipPin.History;

/// <summary>
/// 截图历史主窗口（柔和现代视觉）：
/// - 无边框圆角窗口 + 自定义标题栏（最小化/最大化/关闭）
/// - 半透明毛玻璃质感工具栏：截图/剪贴板/全屏（快捷键胶囊随配置刷新）、多选、设置、清空
/// - 卡片流：圆角缩略图 + 时间/分辨率 + 已钉住徽标，悬停上浮与快捷操作
/// - 多选模式：批量删除所选；清空：全部 / 指定日期之前
/// </summary>
public class HistoryWindow : Window
{
    private readonly HistoryService _history;
    private readonly IPinService _pin;
    private readonly IStorageService _storage;
    private readonly ConfigService _config;

    private TextBlock _countText = null!;
    private WrapPanel _itemsPanel = null!;
    private StackPanel _emptyHint = null!;
    private TextBlock _emptySubText = null!;

    // 多选状态
    private bool _multiSelect;
    private readonly HashSet<string> _selectedIds = new();
    private readonly Dictionary<string, Grid> _cards = new(); // entry.Id -> 卡片

    // 搜索（日期范围）状态
    private DatePicker? _searchStart;
    private DatePicker? _searchEnd;
    private TextBlock _searchInfo = null!;
    private Button _clearSearchBtn = null!;
    private bool _searching;

    private Button _multiSelectBtn = null!;
    private Button _deleteSelectedBtn = null!;
    private TextBlock _selectedInfo = null!;
    private Button _openArchiveBtn = null!;
    private Grid _root = null!;
    private Path _maxIcon = null!;

    // 工具栏快捷键胶囊文字（随配置刷新）
    private TextBlock _kbdCapture = null!;
    private TextBlock _kbdClipboard = null!;
    private TextBlock _kbdFullScreen = null!;

    private const double CardWidth = 216;
    private const double ThumbHeight = 124;

    // ---- 字体 ----
    private const string UiFont = "Segoe UI Variable Text, Segoe UI";
    private const string TitleFont = "Segoe UI Variable Display, Segoe UI";
    private const string KbdFont = "Cascadia Mono, Consolas";

    // ---- 配色（柔和浅色 + 薰衣草紫强调） ----
    private static readonly Brush Accent = Frozen(0x7C, 0x6A, 0xF0);        // 强调紫
    private static readonly Brush PinnedBadgeBg = Frozen(0xEC, 0xE9, 0xFB); // 已钉住徽标底色
    private static readonly Brush PinnedBadgeFg = Frozen(0x6C, 0x5A, 0xE8); // 已钉住徽标文字
    private static readonly Brush SelectedCardBg = Frozen(0xFB, 0xFA, 0xFF);// 多选选中卡片底色
    private static readonly Brush TextPrimary = Frozen(0x3A, 0x41, 0x50);   // 主文字
    private static readonly Brush TextSecondary = Frozen(0x8A, 0x91, 0xA6); // 次文字
    private static readonly Brush IconStroke = Frozen(0x4A, 0x52, 0x65);    // 线条图标默认描边
    private static readonly Brush NormalBorder = Frozen(0xE8, 0xEB, 0xF3);  // 卡片常规边框
    private static readonly Brush HoverBorder = Frozen(0xC9, 0xC1, 0xF6);   // 卡片悬停边框（浅紫）

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>用户点击设置按钮，请求打开设置窗口</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>工具栏截图/剪贴板/全屏按钮触发（参数为动作名，与热键分发一致）</summary>
    public event Action<string>? ActionRequested;

    public HistoryWindow(
        HistoryService history,
        IPinService pin,
        IStorageService storage,
        ConfigService config)
    {
        _history = history;
        _pin = pin;
        _storage = storage;
        _config = config;

        Title = "TLSnipPin 截图历史";
        Icon = Program.LoadAppIcon();
        // 宽度恰好容纳 4 张卡片：4×(卡片216+右距18) + 左边距22 + 右边距14 + 垂直滚动条约18 = 990
        Width = 990;
        Height = 640;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        // 无边框窗口：保留系统缩放边框与 Win11 自动圆角，标题栏改自绘
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        Content = BuildContent();

        // Esc 退出多选模式
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _multiSelect)
            {
                SetMultiSelect(false);
                e.Handled = true;
            }
        };

        _history.Changed += RebuildItems;
        RebuildItems();
        RefreshHotkeyText();
    }

    private UIElement BuildContent()
    {
        // 窗口背景：淡灰蓝对角渐变（非纯白，柔和过渡）
        var bgBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFA, 0xFB, 0xFE), 0));
        bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEC, 0xEF, 0xF7), 1));
        bgBrush.Freeze();
        Background = bgBrush;

        _root = new Grid();
        TextElement.SetFontFamily(_root, new FontFamily(UiFont));
        TextElement.SetForeground(_root, TextPrimary);

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });    // 标题栏
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // 工具栏
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // 搜索栏
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        _root.Children.Add(titleBar);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 1);
        _root.Children.Add(toolbar);

        var searchBar = BuildSearchBar();
        Grid.SetRow(searchBar, 2);
        _root.Children.Add(searchBar);

        // ---- 列表区 ----
        var grid = new Grid();
        _itemsPanel = new WrapPanel
        {
            Margin = new Thickness(22, 18, 14, 14),
            // 靠左排列：卡片从左上角开始依次向右排，不满一行时不会挤到中间
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _emptyHint = BuildEmptyHint();
        grid.Children.Add(_itemsPanel);
        grid.Children.Add(_emptyHint);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Content = grid,
        };
        Grid.SetRow(scroll, 3);
        _root.Children.Add(scroll);

        // 最大化时无边框窗口会溢出屏幕约 7px，加内边距补偿；同时切换还原图标
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            _root.Margin = max ? new Thickness(7) : default;
            _maxIcon.Data = Geometry.Parse(max ? Icons.Restore : Icons.Maximize);
        };

        return _root;
    }

    // ---------------- 标题栏 ----------------
    private UIElement BuildTitleBar()
    {
        var bar = new DockPanel { Margin = new Thickness(16, 0, 6, 0), LastChildFill = false };

        // 左：应用图标 + 名称（其余空白区由 WindowChrome 提供拖动/双击最大化）
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = Program.LoadAppIcon();
        if (icon != null)
            left.Children.Add(new Image
            {
                Source = icon,
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
            });
        left.Children.Add(new TextBlock
        {
            Text = "TLSnipPin 截图历史",
            FontFamily = new FontFamily(TitleFont),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);

        // 右：窗口控制按钮（最小化/最大化/关闭）
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var (minBtn, _) = WindowButton(Icons.Minimize, "最小化");
        minBtn.Click += (_, _) => WindowState = WindowState.Minimized;

        var (maxBtn, maxIcon) = WindowButton(Icons.Maximize, "最大化");
        _maxIcon = maxIcon;
        maxBtn.ToolTip = "最大化 / 还原";
        maxBtn.Click += (_, _) => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        var (closeBtn, closeIcon) = WindowButton(Icons.Close, "关闭", hoverRed: true);
        closeBtn.MouseEnter += (_, _) => closeIcon.Stroke = Brushes.White;
        closeBtn.MouseLeave += (_, _) => closeIcon.Stroke = IconStroke;
        closeBtn.Click += (_, _) => Close();

        right.Children.Add(minBtn);
        right.Children.Add(maxBtn);
        right.Children.Add(closeBtn);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        return bar;
    }

    // ---------------- 工具栏 ----------------
    private UIElement BuildToolbar()
    {
        var toolbar = new Grid();

        // 毛玻璃质感背景层：半透明白 + 底部细分隔线 + 轻阴影（与内容分层，避免文字发虚）
        var bg = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xF5, 0xF7, 0xFC)),
            BorderBrush = Frozen(0xE6, 0xE9, 0xF2),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.045,
                Color = Color.FromRgb(0x64, 0x6E, 0x8F),
                RenderingBias = RenderingBias.Performance,
            },
        };
        toolbar.Children.Add(bg);

        var dock = new DockPanel { Margin = new Thickness(20, 10, 18, 12), LastChildFill = false };

        // ---- 左：标题 + 截图动作按钮 ----
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _countText = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily(TitleFont),
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 16, 0),
        };
        left.Children.Add(_countText);

        // 截图（主按钮，紫色填充）
        var (capKbd, capKbdLabel) = KbdBadge(primary: true);
        _kbdCapture = capKbdLabel;
        var captureBtn = new Button
        {
            Height = 34,
            Padding = new Thickness(20, 0, 18, 0),
            Background = Accent,
            Cursor = Cursors.Hand,
            ToolTip = "框选区域截图",
            Template = ButtonTemplate(10, "#8B7CF8", "#6B59E4"),
            Content = ActionContent(Icons.Camera, Brushes.White, "截图", Brushes.White, capKbd),
        };
        captureBtn.Click += (_, _) => ActionRequested?.Invoke("capture");
        left.Children.Add(captureBtn);

        // 剪贴板（幽灵按钮）
        var (clipKbd, clipKbdLabel) = KbdBadge(primary: false);
        _kbdClipboard = clipKbdLabel;
        var clipboardBtn = new Button
        {
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(20, 0, 18, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "将剪贴板图片钉到桌面",
            Template = ButtonTemplate(10, "#EFEDFB", "#E4E1F7"),
            Content = ActionContent(Icons.Clipboard, IconStroke, "剪贴板", TextPrimary, clipKbd),
        };
        clipboardBtn.Click += (_, _) => ActionRequested?.Invoke("pinClipboard");
        left.Children.Add(clipboardBtn);

        // 全屏（幽灵按钮）
        var (fullKbd, fullKbdLabel) = KbdBadge(primary: false);
        _kbdFullScreen = fullKbdLabel;
        var fullBtn = new Button
        {
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(20, 0, 18, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "全屏截图并钉住",
            Template = ButtonTemplate(10, "#EFEDFB", "#E4E1F7"),
            Content = ActionContent(Icons.Monitor, IconStroke, "全屏", TextPrimary, fullKbd),
        };
        fullBtn.Click += (_, _) => ActionRequested?.Invoke("fullScreen");
        left.Children.Add(fullBtn);

        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        // ---- 右：管理动作 ----
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _openArchiveBtn = new Button
        {
            Height = 32,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(19, 0, 19, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "超过上限的历史已归档到压缩包，打开所在文件夹",
            Template = ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
            Visibility = Visibility.Collapsed,
        };
        _openArchiveBtn.Click += (_, _) => _history.OpenFolder();

        _multiSelectBtn = new Button
        {
            Height = 32,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(19, 0, 19, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "进入多选模式，批量删除（Esc 退出）",
            Template = ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
            Content = ActionContent(Icons.CheckSquare, IconStroke, "多选", TextPrimary),
        };
        _multiSelectBtn.Click += (_, _) => SetMultiSelect(!_multiSelect);

        _selectedInfo = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Accent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };

        _deleteSelectedBtn = new Button
        {
            Height = 32,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(19, 0, 19, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "删除所选的历史记录",
            Template = ButtonTemplate(9, "#FDEBEE", "#F9DDE2"),
            Visibility = Visibility.Collapsed,
        };
        _deleteSelectedBtn.Click += (_, _) => DeleteSelected();

        var settingsBtn = new Button
        {
            Height = 32,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(19, 0, 19, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "打开设置（可修改快捷键）",
            Template = ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
            Content = ActionContent(Icons.Sliders, IconStroke, "设置", TextPrimary),
        };
        settingsBtn.Click += (_, _) => OpenSettingsRequested?.Invoke();

        // 清空历史：图标 + 文字 + 下拉小箭头
        var clearContent = new StackPanel { Orientation = Orientation.Horizontal };
        clearContent.Children.Add(LineIcon(Icons.Trash, 15, IconStroke));
        clearContent.Children.Add(new TextBlock
        {
            Text = "清空历史",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        });
        clearContent.Children.Add(LineIcon(Icons.ChevronDown, 10, TextSecondary));

        var clearBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(18, 0, 15, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "清空历史（可按日期）",
            Template = ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
            Content = clearContent,
        };
        clearBtn.Click += (_, _) => OpenClearMenu(clearBtn);

        right.Children.Add(_openArchiveBtn);
        right.Children.Add(_multiSelectBtn);
        right.Children.Add(_selectedInfo);
        right.Children.Add(_deleteSelectedBtn);
        right.Children.Add(settingsBtn);
        right.Children.Add(clearBtn);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        toolbar.Children.Add(dock);
        return toolbar;
    }

    // ---------------- 搜索栏（日期范围） ----------------
    private UIElement BuildSearchBar()
    {
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0xF5, 0xF7, 0xFC)),
            BorderBrush = Frozen(0xE6, 0xE9, 0xF2),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 8, 22, 8),
        };

        var dock = new DockPanel { LastChildFill = false };

        // 左：起止日期 + 搜索/清除
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        left.Children.Add(new TextBlock
        {
            Text = "日期范围",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });

        _searchStart = SearchDateBox();
        left.Children.Add(_searchStart);
        left.Children.Add(new TextBlock
        {
            Text = "至",
            FontSize = 12,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        });
        _searchEnd = SearchDateBox();
        left.Children.Add(_searchEnd);

        var searchBtn = new Button
        {
            Height = 30,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Background = Accent,
            Cursor = Cursors.Hand,
            ToolTip = "按所选日期范围筛选",
            Template = ButtonTemplate(8, "#8B7CF8", "#6B59E4"),
            Content = new TextBlock
            {
                Text = "搜索",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        searchBtn.Click += (_, _) => ApplySearch();
        left.Children.Add(searchBtn);

        _clearSearchBtn = new Button
        {
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "清除筛选，显示全部",
            Visibility = Visibility.Collapsed,
            Template = ButtonTemplate(8, "#EFEDFB", "#E4E1F7"),
            Content = new TextBlock
            {
                Text = "清除",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _clearSearchBtn.Click += (_, _) => ClearSearch();
        left.Children.Add(_clearSearchBtn);

        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        // 右：筛选结果提示
        _searchInfo = new TextBlock
        {
            FontSize = 12,
            Foreground = Accent,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        DockPanel.SetDock(_searchInfo, Dock.Right);
        dock.Children.Add(_searchInfo);

        bar.Child = dock;
        return bar;
    }

    /// <summary>搜索栏日期框：统一外观，回车即触发搜索</summary>
    private DatePicker SearchDateBox()
    {
        var dp = new DatePicker
        {
            Width = 130,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
        };
        dp.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ApplySearch(); e.Handled = true; }
        };
        return dp;
    }

    /// <summary>应用日期范围筛选</summary>
    private void ApplySearch()
    {
        var s = _searchStart?.SelectedDate;
        var e = _searchEnd?.SelectedDate;
        if (!s.HasValue && !e.HasValue)
        {
            ClearSearch();
            return;
        }
        // 只填一端时，另一端视为当天/最早
        var from = (s ?? DateTime.MinValue).Date;
        var to = (e ?? DateTime.Today).Date;
        if (from > to) (from, to) = (to, from);

        _searching = true;
        _searchRangeFrom = from;
        _searchRangeTo = to;
        RebuildItems();
    }

    /// <summary>清除筛选，恢复显示全部</summary>
    private void ClearSearch()
    {
        _searching = false;
        _searchRangeFrom = default;
        _searchRangeTo = default;
        if (_searchStart != null) _searchStart.SelectedDate = null;
        if (_searchEnd != null) _searchEnd.SelectedDate = null;
        RebuildItems();
    }

    private DateTime _searchRangeFrom;
    private DateTime _searchRangeTo;

    /// <summary>按当前搜索范围过滤后的条目</summary>
    private IEnumerable<HistoryEntry> FilteredEntries()
    {
        var entries = _history.Entries;
        if (!_searching) return entries;
        var from = _searchRangeFrom.Date;
        var to = _searchRangeTo.Date.AddDays(1); // 含结束当天
        return entries.Where(x => x.CreatedAt >= from && x.CreatedAt < to);
    }

    // ---------------- 空状态 ----------------
    private StackPanel BuildEmptyHint()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(LineIcon(Icons.Image, 56, Frozen(0xC7, 0xCD, 0xE3), thickness: 1.4));
        panel.Children.Add(new TextBlock
        {
            Text = "还没有截图记录",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Frozen(0x5A, 0x61, 0x72),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 4),
        });
        _emptySubText = new TextBlock
        {
            FontSize = 12,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(_emptySubText);
        return panel;
    }

    // ---------------- 列表构建 ----------------
    private void RebuildItems()
    {
        _cards.Clear();
        _itemsPanel.Children.Clear();
        _selectedIds.Clear();

        // 应用日期范围筛选
        var entries = FilteredEntries().ToList();

        // 标题计数：截图历史
        _countText.Inlines.Clear();
        _countText.Inlines.Add(new Run("截图历史"));
        if (entries.Count > 0)
        {
            _countText.Inlines.Add(new Run($" ({entries.Count})")
            {
                Foreground = Accent,
                FontSize = 14,
            });
        }

        // 归档入口（仅在存在归档时显示）
        if (_history.ArchivedCount > 0)
        {
            _openArchiveBtn.Content = ActionContent(Icons.Archive, IconStroke, $"已归档 {_history.ArchivedCount} 张", TextSecondary);
            _openArchiveBtn.Visibility = Visibility.Visible;
        }
        else
        {
            _openArchiveBtn.Visibility = Visibility.Collapsed;
        }

        _emptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in entries)
        {
            var card = BuildCard(entry);
            _cards[entry.Id] = card;
            _itemsPanel.Children.Add(card);
        }

        // 搜索状态提示
        bool searching = _searching && _searchInfo != null;
        if (searching)
        {
            _searchInfo.Text = $"找到 {entries.Count} 条 · {_searchRangeFrom:yyyy-MM-dd} ~ {_searchRangeTo:yyyy-MM-dd}";
            _searchInfo.Visibility = Visibility.Visible;
            _clearSearchBtn.Visibility = Visibility.Visible;
        }
        else if (_searchInfo != null)
        {
            _searchInfo.Visibility = Visibility.Collapsed;
            _clearSearchBtn.Visibility = Visibility.Collapsed;
        }

        UpdateSelectedInfo();
    }

    /// <summary>卡片视觉引用：背景层（含边框/阴影）与多选徽标</summary>
    private sealed class CardParts
    {
        public Border Bg = null!;
        public Border Badge = null!;
        public DropShadowEffect Shadow = null!;
    }

    /// <summary>单张历史卡片：圆角缩略图 + 悬停操作层 + 信息条（时间/分辨率/已钉住徽标）+ 多选徽标</summary>
    private Grid BuildCard(HistoryEntry entry)
    {
        var thumb = _history.LoadImage(entry, decodeWidth: (int)(CardWidth * 2));
        var img = new Image
        {
            Source = thumb,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 圆角缩略图：浅色底 + 几何裁剪
        var innerWidth = CardWidth - 20; // 左右内边距各 10
        var clip = new RectangleGeometry(new Rect(0, 0, innerWidth, ThumbHeight), 10, 10);
        clip.Freeze();
        var thumbHost = new Border
        {
            Height = ThumbHeight,
            CornerRadius = new CornerRadius(10),
            Background = Frozen(0xF3, 0xF5, 0xFA),
            Clip = clip,
            Child = img,
        };

        // 悬停操作层：钉住 / 保存 / 单条删除
        var overlay = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Visibility = Visibility.Collapsed,
        };
        var btnPin = MiniIconButton(Icons.Pin, "重新钉住到桌面");
        btnPin.Click += (_, _) => PinEntry(entry);
        var btnSave = MiniIconButton(Icons.Download, "保存到文件");
        btnSave.Click += (_, _) => SaveEntry(entry);
        var btnDel = MiniIconButton(Icons.Trash, "删除这条历史");
        btnDel.Click += (_, _) => _history.Delete(entry);
        overlay.Children.Add(btnPin);
        overlay.Children.Add(btnSave);
        overlay.Children.Add(btnDel);

        // 多选徽标（选中时显示 ✓）
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = Accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new Viewbox
            {
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                Child = new Path
                {
                    Data = Geometry.Parse("M6 12.5l4 4 8-8"),
                    Stroke = Brushes.White,
                    StrokeThickness = 2.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                },
            },
        };

        // 缩略图层 + 悬浮层
        var visual = new Grid();
        visual.Children.Add(thumbHost);
        visual.Children.Add(overlay);
        visual.Children.Add(badge);

        // 信息条：左列时间/分辨率，右下角已钉住徽标
        var infoGrid = new Grid { Margin = new Thickness(2, 9, 2, 0) };
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var infoLeft = new StackPanel();
        infoLeft.Children.Add(new TextBlock
        {
            Text = $"{entry.CreatedAt:MM-dd HH:mm}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Frozen(0x4A, 0x52, 0x65),
        });
        infoLeft.Children.Add(new TextBlock
        {
            Text = $"{entry.Width}×{entry.Height}",
            FontSize = 11,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(infoLeft, 0);
        infoGrid.Children.Add(infoLeft);

        if (entry.Action == "钉住")
        {
            var pinContent = new StackPanel { Orientation = Orientation.Horizontal };
            pinContent.Children.Add(LineIcon(Icons.Pin, 10, PinnedBadgeFg, thickness: 1.8));
            pinContent.Children.Add(new TextBlock
            {
                Text = "已钉住",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = PinnedBadgeFg,
                Margin = new Thickness(3, 0, 0, 0),
            });
            var pinBadge = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = PinnedBadgeBg,
                Padding = new Thickness(7, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = pinContent,
            };
            Grid.SetColumn(pinBadge, 1);
            infoGrid.Children.Add(pinBadge);
        }

        // 卡片内容
        var content = new StackPanel { Margin = new Thickness(10, 10, 10, 11) };
        content.Children.Add(visual);
        content.Children.Add(infoGrid);

        // 背景层（阴影挂在独立层上，避免文字经过位图特效发虚）
        var shadow = new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.09,
            Color = Color.FromRgb(0x5A, 0x64, 0x8C),
            RenderingBias = RenderingBias.Performance,
        };
        var bg = new Border
        {
            Background = Brushes.White,
            BorderBrush = NormalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Effect = shadow,
        };

        var card = new Grid
        {
            Width = CardWidth,
            Margin = new Thickness(0, 0, 18, 16),
            Cursor = Cursors.Hand,
            RenderTransform = new TranslateTransform(),
            Tag = new CardParts { Bg = bg, Badge = badge, Shadow = shadow },
        };
        SetCardToolTip(card, entry);
        card.Children.Add(bg);
        card.Children.Add(content);

        card.MouseEnter += (_, _) =>
        {
            overlay.Visibility = Visibility.Visible;
            if (!_selectedIds.Contains(entry.Id))
                bg.BorderBrush = HoverBorder;
            // 悬停上浮 + 阴影加深
            AnimateDouble((TranslateTransform)card.RenderTransform, TranslateTransform.YProperty, -3);
            AnimateDouble(shadow, DropShadowEffect.OpacityProperty, 0.17);
        };
        card.MouseLeave += (_, _) =>
        {
            overlay.Visibility = Visibility.Collapsed;
            if (!_selectedIds.Contains(entry.Id))
                bg.BorderBrush = NormalBorder;
            AnimateDouble((TranslateTransform)card.RenderTransform, TranslateTransform.YProperty, 0);
            AnimateDouble(shadow, DropShadowEffect.OpacityProperty, 0.09);
        };

        card.MouseLeftButtonDown += (_, e) =>
        {
            // 多选模式：点击切换选中
            if (_multiSelect)
            {
                e.Handled = true;
                ToggleSelect(entry.Id);
                return;
            }
            // 普通模式：双击重新钉住
            if (e.ClickCount == 2)
                PinEntry(entry);
        };

        return card;
    }

    /// <summary>卡片提示：完整时间、来源与保存路径</summary>
    private static void SetCardToolTip(Grid card, HistoryEntry entry)
    {
        var tip = $"双击重新钉住\n{entry.CreatedAt:yyyy-MM-dd HH:mm:ss} · {entry.Width}×{entry.Height} · 来源：{entry.Action}";
        if (!string.IsNullOrEmpty(entry.SavedPath))
            tip += $"\n已保存：{entry.SavedPath}";
        card.ToolTip = tip;
    }

    // ---------------- 多选 ----------------
    private void SetMultiSelect(bool on)
    {
        _multiSelect = on;
        // 激活时按钮切换为紫色填充强调态
        _multiSelectBtn.Background = on ? Accent : Brushes.Transparent;
        _multiSelectBtn.Template = on ? ButtonTemplate(9, "#6B59E4", "#5F4FD6") : ButtonTemplate(9, "#EFEDFB", "#E4E1F7");
        _multiSelectBtn.Content = on
            ? ActionContent(Icons.CheckSquare, Brushes.White, "退出多选", Brushes.White)
            : ActionContent(Icons.CheckSquare, IconStroke, "多选", TextPrimary);
        _deleteSelectedBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _deleteSelectedBtn.Content = ActionContent(Icons.Trash, Frozen(0xD6, 0x45, 0x62), "删除所选", Frozen(0xD6, 0x45, 0x62));

        if (on)
        {
            _selectedIds.Clear();
            RefreshCardSelection();
            UpdateSelectedInfo();
        }
        else
        {
            ClearSelection();
        }
    }

    private void ToggleSelect(string id)
    {
        if (!_selectedIds.Remove(id))
            _selectedIds.Add(id);
        RefreshCardSelection();
        UpdateSelectedInfo();
    }

    private void RefreshCardSelection()
    {
        foreach (var (id, card) in _cards)
        {
            bool sel = _selectedIds.Contains(id);
            if (card.Tag is not CardParts p)
                continue;
            p.Bg.BorderBrush = sel ? Accent : NormalBorder;
            p.Bg.BorderThickness = new Thickness(sel ? 1.5 : 1);
            p.Bg.Background = sel ? SelectedCardBg : Brushes.White;
            p.Badge.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ClearSelection()
    {
        _selectedIds.Clear();
        RefreshCardSelection();
        UpdateSelectedInfo();
    }

    private void UpdateSelectedInfo()
    {
        _selectedInfo.Text = $"已选 {_selectedIds.Count} 项";
        _selectedInfo.Visibility = _multiSelect ? Visibility.Visible : Visibility.Collapsed;
        _deleteSelectedBtn.IsEnabled = _selectedIds.Count > 0;
    }

    private void DeleteSelected()
    {
        if (_selectedIds.Count == 0) return;
        if (MessageBox.Show(this,
                $"确定删除所选的 {_selectedIds.Count} 条历史？",
                "删除所选", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // 逐条删除（Changed 事件触发整体重建，删除后选中集自然清空）
        foreach (var entry in _history.Entries.Where(e => _selectedIds.Contains(e.Id)).ToList())
            _history.Delete(entry);
    }

    // ---------------- 清空 ----------------
    private void OpenClearMenu(Button target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Style = ClearMenuStyle,
        };
        menu.Resources.Add(typeof(MenuItem), ClearMenuItemStyle);

        var all = new MenuItem
        {
            Header = "清空全部历史",
            Icon = LineIcon(Icons.Trash, 15, IconStroke),
        };
        all.Click += (_, _) => OnClearAll();

        var before = new MenuItem
        {
            Header = "按日期范围清空...",
            Icon = LineIcon(Icons.Calendar, 15, IconStroke),
        };
        before.Click += (_, _) => OnClearBeforeDate();

        menu.Items.Add(all);
        menu.Items.Add(before);
        menu.IsOpen = true;
    }

    private void OnClearAll()
    {
        if (_history.Entries.Count == 0) return;
        if (MessageBox.Show(this,
                $"确定清空全部 {_history.Entries.Count} 条截图历史？\n（仅清除历史记录，不影响已保存的文件）",
                "清空历史", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _history.Clear();
    }

    /// <summary>弹出日期范围对话框，清空 [起始, 结束]（均含当天）之间的历史</summary>
    private void OnClearBeforeDate()
    {
        var dpStart = new DatePicker { SelectedDate = DateTime.Today.AddDays(-7) };
        var dpEnd = new DatePicker { SelectedDate = DateTime.Today };

        var win = new Window
        {
            Title = "按日期范围清空",
            Width = 380,
            Height = 270,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.White,
        };
        TextElement.SetFontFamily(win, new FontFamily(UiFont));

        var tip = new TextBlock
        {
            Text = "将删除所选日期范围内的记录（含起止当天）：",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = Frozen(0x4A, 0x52, 0x65),
        };

        // 起始 / 结束两行（标签 + 日期框）
        Grid RangeRow(string label, DatePicker dp)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(dp, 1);
            dp.VerticalContentAlignment = VerticalAlignment.Center;
            dp.Height = 28;
            g.Children.Add(dp);
            return g;
        }

        var ok = new Button
        {
            Content = "清空",
            Width = 88,
            Height = 32,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = Accent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0),
            Template = ButtonTemplate(9, "#8B7CF8", "#6B59E4"),
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Height = 32,
            FontSize = 12.5,
            Foreground = TextPrimary,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Template = ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
        };
        ok.Click += (_, _) =>
        {
            var s = dpStart.SelectedDate;
            var e = dpEnd.SelectedDate;
            if (!s.HasValue || !e.HasValue)
            {
                MessageBox.Show(win, "请选择完整的起止日期。", "按日期范围清空",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (s.Value > e.Value)
                (s, e) = (e, s); // 起止颠倒时自动交换

            int removed = _history.ClearRange(s.Value, e.Value);
            win.Close();
            MessageBox.Show(this, removed > 0 ? $"已删除 {removed} 条记录" : "该日期范围内没有记录",
                "按日期范围清空", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        cancel.Click += (_, _) => win.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(tip);
        panel.Children.Add(RangeRow("起始", dpStart));
        panel.Children.Add(RangeRow("结束", dpEnd));
        panel.Children.Add(buttons);
        win.Content = panel;
        win.ShowDialog();
    }

    // ---------------- 条目操作 ----------------
    private void PinEntry(HistoryEntry entry)
    {
        try
        {
            var img = _history.LoadImage(entry);
            if (img == null)
            {
                MessageBox.Show(this, "该条历史的图片文件已不存在，无法钉住。", "重新钉住",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _pin.Pin(img);
            // 钉窗常落在历史窗后面：先把历史窗激活到前面再让位，便于看到钉住的图
            Activate();
        }
        catch (Exception ex)
        {
            Program.LogError("重新钉住", ex);
            MessageBox.Show(this, $"钉住失败：\n{ex.Message}", "重新钉住",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveEntry(HistoryEntry entry)
    {
        var img = _history.LoadImage(entry);
        if (img == null) return;
        var path = _storage.SaveToFile(img);
        MessageBox.Show(this, $"已保存：\n{path}", "保存成功",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------- 快捷键提示 ----------------
    /// <summary>刷新工具栏快捷键胶囊与空状态文案（窗口激活时也会刷新，覆盖设置修改后的情况）</summary>
    private void RefreshHotkeyText()
    {
        var hk = _config.Config.Hotkeys;
        _kbdCapture.Text = hk.Capture;
        _kbdClipboard.Text = hk.PinClipboard;
        _kbdFullScreen.Text = hk.FullScreen;
        _emptySubText.Text = $"点击上方「截图」按钮，或按 {hk.Capture} 截一张试试";
    }

    protected override void OnActivated(EventArgs e)
    {
        RefreshHotkeyText();
        base.OnActivated(e);
    }

    // ---------------- 控件工厂与样式 ----------------

    /// <summary>线条图标（24 基准几何，统一圆角圆帽描边）</summary>
    private static Viewbox LineIcon(string data, double size, Brush stroke, double thickness = 1.7) => new()
    {
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Child = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
        },
    };

    /// <summary>按钮内容：图标 + 文字（+ 可选快捷键胶囊）</summary>
    private static StackPanel ActionContent(
        string icon, Brush iconStroke, string label, Brush labelBrush, UIElement? kbd = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(LineIcon(icon, 15, iconStroke));
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
    private static (Border Host, TextBlock Label) KbdBadge(bool primary)
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

    /// <summary>标题栏窗口控制按钮</summary>
    private (Button Btn, Path Icon) WindowButton(string iconData, string tooltip, bool hoverRed = false)
    {
        var icon = LineIcon(iconData, 10, IconStroke, thickness: 1.2);
        var btn = new Button
        {
            Width = 42,
            Height = 30,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            ToolTip = tooltip,
            Content = icon,
            Template = hoverRed
                ? ButtonTemplate(7, "#E81123", "#C50F1F")
                : ButtonTemplate(7, "#E9EBF2", "#DDE0EA"),
        };
        WindowChrome.SetIsHitTestVisibleInChrome(btn, true);
        return (btn, (Path)icon.Child);
    }

    /// <summary>卡片悬停操作小按钮：半透明白圆角底</summary>
    private static Button MiniIconButton(string iconData, string tooltip) => new()
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
    private static ControlTemplate ButtonTemplate(int radius, string hover, string pressed)
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

        // 悬停淡入 / 移出淡出
        var enter = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        enter.EnterActions.Add(FadeBgAction("overlay", 1.0));
        enter.ExitActions.Add(FadeBgAction("overlay", 0.0));
        tt.Triggers.Add(enter);

        // 按下时换成更深色；松开（IsPressed=false）时恢复悬停色并随淡出隐藏
        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        press.EnterActions.Add(FadeBgAction("overlay", 1.0, pre));
        press.ExitActions.Add(FadeBgAction("overlay", 1.0, hov)); // 松开后立刻换回悬停色，避免残留深色
        tt.Triggers.Add(press);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        tt.Triggers.Add(disabled);

        return tt;
    }

    private static BeginStoryboard FadeBgAction(string target, double to, Brush? brush = null)
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

    private static Brush ParseBrush(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex);
        brush.Freeze();
        return brush;
    }

    /// <summary>清空菜单：白色圆角浮层 + 轻阴影</summary>
    private static Style ClearMenuStyle => (Style)XamlReader.Parse("""
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

    /// <summary>清空菜单项：悬停浅紫底</summary>
    private static Style ClearMenuItemStyle => (Style)XamlReader.Parse("""
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

    // ---------------- 动画辅助 ----------------
    /// <summary>对可动画属性做缓动过渡（卡片上浮/阴影加深）</summary>
    private static void AnimateDouble(Animatable target, DependencyProperty property, double to, int ms = 150)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        target.BeginAnimation(property, anim);
    }

    /// <summary>24×24 基准的圆角线条图标几何数据</summary>
    private static class Icons
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
    }
}
