using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SnipPin.Core.Native;
using SnipPin.Core.Services;

namespace SnipPin.ClipboardHistory;

/// <summary>
/// 剪贴板历史弹窗（Ctrl+Alt+V 在鼠标附近弹出，复刻 Win+V 机制，目标窗口全程不失焦）：
/// - 顶部：模糊搜索框 + 日期筛选（DatePicker）
/// - 列表：文本摘要 / 图片缩略图 / 文件列表，时间精确到秒，悬停显示删除按钮
/// - 非激活显示（WS_EX_NOACTIVATE + WM_MOUSEACTIVATE→MA_NOACTIVATE）：目标窗口保持前台与焦点
/// - 键盘：WH_KEYBOARD_LL 低级钩子把按键喂给搜索框（面板不取 Win32 焦点）
/// - 点击搜索框只移动光标，不激活弹窗（与日期筛选相同，目标窗口保持前台）
/// - 中文输入法无法挂到无焦点窗口：再次按下热键才进入 IME 模式，粘贴/Esc 前恢复目标前台
/// - 点击条目：卸钩子 → 写回剪贴板 → 模拟 Ctrl+V（前台从未离开目标，直接命中）
/// - Esc / 点击面板外（WH_MOUSE_LL 检测）/ 切换窗口（哨兵轮询）自动关闭
/// </summary>
public class ClipboardWindow : Window
{
    private readonly ClipboardService _clipboard;
    /// <summary>弹窗打开前的前台窗口（粘贴目标）</summary>
    private readonly IntPtr _targetHwnd;
    /// <summary>弹窗打开前目标窗口内的焦点子窗口（通常即输入框），粘贴时精确归还</summary>
    private readonly IntPtr _focusHwnd;

    private TextBox _searchBox = null!;
    private TextBlock _placeholder = null!;
    private DatePicker _datePicker = null!;
    private TextBlock _infoText = null!;
    private StackPanel _listPanel = null!;
    private StackPanel _emptyHint = null!;
    private TextBlock _emptyText = null!;

    // ---- 批量清除（多选模式） ----
    /// <summary>是否处于多选模式：条目左侧显示复选框，列表顶部显示全选行</summary>
    private bool _selectMode;
    /// <summary>勾选中的条目（始终 ⊆ 当前过滤结果，切换筛选后不可见项自动剔除）</summary>
    private readonly HashSet<ClipboardEntry> _selected = new();
    /// <summary>当前列表可见的条目（RebuildList 时更新）</summary>
    private List<ClipboardEntry> _visibleItems = new();
    /// <summary>条目 -> 复选框映射（全选联动用，随列表重建）</summary>
    private readonly Dictionary<ClipboardEntry, CheckBox> _itemChecks = new();
    /// <summary>批量清除入口按钮（多选模式下变为"取消"）</summary>
    private Button _batchButton = null!;
    /// <summary>全选复选框（列表顶行，空列表时未构建）</summary>
    private CheckBox? _selectAllCheck;
    /// <summary>删除所选按钮（二次点击确认）</summary>
    private Button? _deleteSelBtn;
    /// <summary>删除按钮已进入"再点一次确认"状态</summary>
    private bool _deleteArmed;

    /// <summary>搜索防抖：停止输入 150ms 后再重建列表</summary>
    private readonly DispatcherTimer _searchDebounce;

    /// <summary>图片缩略图懒加载队列（占位符 -> 条目），后台逐张填充避免打开卡顿</summary>
    private readonly List<(Border Placeholder, ClipboardEntry Entry)> _thumbQueue = new();
    private bool _thumbPumping;
    private bool _closed;
    /// <summary>关闭哨兵：非激活窗口收不到 Deactivated，用轮询检测用户切换到其他窗口后自动关闭</summary>
    private DispatcherTimer? _closeGuard;
    /// <summary>本弹窗句柄（哨兵豁免自身用）</summary>
    private IntPtr _selfHwnd;
    /// <summary>粘贴目标线程 ID（键盘布局翻译用）</summary>
    private uint _targetThreadId;
    /// <summary>是否与目标线程保持 AttachThreadInput 挂接（仅 IME 激活模式需要）</summary>
    private bool _attached;

    // ---- 非激活输入（同 Win+V：面板不夺焦点，低级钩子把按键喂进搜索框） ----
    /// <summary>低级键盘钩子句柄</summary>
    private IntPtr _keyHook;
    /// <summary>低级鼠标钩子句柄（点击面板外关闭）</summary>
    private IntPtr _mouseHook;
    /// <summary>钩子回调委托（须持引用防被 GC 回收）</summary>
    private NativeMethods.LowLevelHookProc? _keyHookProc, _mouseHookProc;
    /// <summary>IME 模式：再次按下热键后弹窗真正激活（输入法需要真实焦点），此时键盘钩子已卸</summary>
    private bool _imeMode;
    /// <summary>钩子模式下的搜索光标（TextBox 无 Win32 焦点时系统光标不显示）</summary>
    private Border? _searchCaret;
    private DispatcherTimer? _caretBlink;
    private bool _caretPhase = true;
    /// <summary>修饰键物理状态（钩子吞键后 GetAsyncKeyState 不再更新，在钩子里自行维护）</summary>
    private bool _modCtrl, _modShift, _modAlt;
    /// <summary>键盘高亮的列表条目索引（同 Win+V 上下键导航，-1 无）</summary>
    private int _selIndex = -1;
    /// <summary>列表条目容器（按显示顺序，供高亮/滚动定位）</summary>
    private readonly List<Border> _itemBorders = new();

    // ---- 字体 / 配色（与 HistoryWindow 保持一致的柔和浅色 + 薰衣草紫） ----
    private const string UiFont = "Segoe UI Variable Text, Segoe UI";
    private static readonly Brush Accent = Frozen(0x7C, 0x6A, 0xF0);
    private static readonly Brush WindowBg = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush ToolbarBg = Frozen(0xF7, 0xF8, 0xFC);
    private static readonly Brush ItemHoverBg = Frozen(0xF4, 0xF1, 0xFE);
    private static readonly Brush HoverBorder = Frozen(0xC9, 0xC1, 0xF6);
    private static readonly Brush NormalBorder = Frozen(0xE8, 0xEB, 0xF3);
    private static readonly Brush TextPrimary = Frozen(0x3A, 0x41, 0x50);
    private static readonly Brush TextSecondary = Frozen(0x8A, 0x91, 0xA6);
    private static readonly Brush BadgeBg = Frozen(0xEC, 0xE9, 0xFB);
    private static readonly Brush BadgeFg = Frozen(0x6C, 0x5A, 0xE8);
    private static readonly Brush Danger = Frozen(0xE5, 0x48, 0x4D);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public ClipboardWindow(ClipboardService clipboard, IntPtr targetHwnd, IntPtr focusHwnd)
    {
        _clipboard = clipboard;
        _targetHwnd = targetHwnd;
        _focusHwnd = focusHwnd != IntPtr.Zero ? focusHwnd : targetHwnd;

        Title = "剪贴板历史";
        Icon = Program.LoadAppIcon();
        Width = 450;
        Height = 600;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        // 无边框透明窗口：自绘圆角 + 外发光阴影
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        // 有有效粘贴目标时非激活显示（不夺目标焦点）；无目标（前台是本应用）时正常激活便于搜索
        ShowActivated = targetHwnd == IntPtr.Zero;

        // DatePicker 日历随系统语言（中文环境显示中文日历）
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(
            CultureInfo.CurrentCulture.IetfLanguageTag);

        FontFamily = new FontFamily(UiFont);
        FontSize = 13;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildList();
        };

        Content = BuildUi();

        // 条目在弹窗打开期间变化（后台继续记录）时刷新列表
        _clipboard.Changed += OnEntriesChanged;

        // 打开淡入
        Opacity = 0;
        Loaded += (_, _) =>
        {
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            BeginAnimation(OpacityProperty, anim);
            Dispatcher.BeginInvoke(UpdateSearchCaret, DispatcherPriority.Loaded);
        };
    }

    // ---------------- UI 构建 ----------------

    private UIElement BuildUi()
    {
        var outer = new Grid { Margin = new Thickness(14) };
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = WindowBg,
            BorderBrush = NormalBorder,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.18,
                Color = Color.FromRgb(0x33, 0x38, 0x4C),
            },
        };
        outer.Children.Add(card);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 工具栏
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 信息行
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
        card.Child = root;

        root.Children.Add(BuildToolbar());
        root.Children.Add(BuildListArea());

        // 信息行
        _infoText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = TextSecondary,
            Margin = new Thickness(16, 4, 16, 2),
        };
        Grid.SetRow(_infoText, 1);
        root.Children.Add(_infoText);

        return outer;
    }

    private UIElement BuildToolbar()
    {
        var bar = new Border
        {
            Background = ToolbarBg,
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Padding = new Thickness(12, 10, 12, 8),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Child = row;
        Grid.SetRow(bar, 0);

        // 搜索框（圆角 + 占位提示文字）
        var searchBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            BorderBrush = NormalBorder,
            BorderThickness = new Thickness(1),
            Width = 176,
            Height = 30,
            ClipToBounds = true,
        };
        var searchGrid = new Grid();
        _placeholder = new TextBlock
        {
            Text = "搜索…",
            Foreground = TextSecondary,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 9, 0),
            IsHitTestVisible = false,
        };
        _searchBox = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // 有粘贴目标时不可获焦：TextBox 一旦 Focus 会把弹窗激活，目标窗口标题栏失焦。
            // 无目标时仍走原生焦点。再次按热键进入 IME 模式前会把 Focusable 打开。
            Focusable = _targetHwnd == IntPtr.Zero,
            IsInactiveSelectionHighlightEnabled = true,
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = string.IsNullOrEmpty(_searchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        // 回车：粘贴当前键盘高亮条目（多选模式下禁用）
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !_selectMode)
                PasteFromKeyboard();
        };
        _searchCaret = new Border
        {
            Width = 1,
            Background = TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true,
        };
        searchGrid.Children.Add(_placeholder);
        searchGrid.Children.Add(_searchBox);
        searchGrid.Children.Add(_searchCaret);
        searchBorder.Child = searchGrid;
        _searchBox.SelectionChanged += (_, _) => UpdateSearchCaret();
        _searchBox.SizeChanged += (_, _) => UpdateSearchCaret();
        // 点击只落光标，不激活弹窗。激活（IME）会 SetForegroundWindow，目标窗口因此失焦；
        // 日期筛选不走这条路，所以点日历不会失焦。字符仍由低级键盘钩子喂入。
        searchBorder.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_targetHwnd == IntPtr.Zero || _imeMode)
                return; // 无目标或已在 IME：交给 TextBox 原生处理
            e.Handled = true;
            PlaceSearchCaret(e.GetPosition(_searchBox));
        };
        row.Children.Add(searchBorder);

        // 日期筛选
        _datePicker = new DatePicker
        {
            Width = 104,
            Height = 30,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        _datePicker.SelectedDateChanged += (_, _) => RebuildList();
        row.Children.Add(_datePicker);

        // 清除日期按钮
        var clearDate = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 26,
            FontSize = 11,
            Foreground = TextSecondary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = "清除日期筛选",
            Cursor = Cursors.Hand,
        };
        clearDate.Click += (_, _) =>
        {
            if (_datePicker.SelectedDate != null)
            {
                _datePicker.SelectedDate = null;
                RebuildList();
            }
        };
        row.Children.Add(clearDate);

        // 批量清除：进入多选模式（多选模式下变为"取消"退出）
        _batchButton = new Button
        {
            Content = "批量清除",
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Foreground = BadgeFg,
            Background = Brushes.White,
            BorderBrush = NormalBorder,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "多选删除剪贴板记录",
        };
        _batchButton.Click += (_, _) =>
        {
            if (_selectMode) ExitSelectMode();
            else EnterSelectMode();
        };
        row.Children.Add(_batchButton);

        return bar;
    }

    private UIElement BuildListArea()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 4, 0, 8),
        };
        Grid.SetRow(scroll, 2);

        var host = new Grid();
        _listPanel = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        host.Children.Add(_listPanel);

        _emptyText = new TextBlock
        {
            Text = "暂无剪贴板记录",
            FontSize = 12,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _emptyHint = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var hintIcon = new TextBlock
        {
            Text = "📋",
            FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var hintText = new TextBlock
        {
            Text = "复制的内容会自动记录在这里\n点击条目即可粘贴到鼠标所在窗口",
            FontSize = 12,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _emptyHint.Children.Add(hintIcon);
        _emptyHint.Children.Add(hintText);
        host.Children.Add(_emptyHint);

        scroll.Content = host;
        return scroll;
    }

    // ---------------- 列表构建 ----------------

    /// <summary>当前过滤结果：关键词模糊匹配（不区分大小写）+ 日期筛选</summary>
    private IEnumerable<ClipboardEntry> Filtered()
    {
        IEnumerable<ClipboardEntry> seq = _clipboard.Entries;
        var kw = _searchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(kw))
            seq = seq.Where(e => e.Text != null &&
                                 e.Text.Contains(kw, StringComparison.OrdinalIgnoreCase));
        if (_datePicker?.SelectedDate is { } date)
            seq = seq.Where(e => e.CopiedAt.Date == date.Date);
        return seq;
    }

    private void RebuildList()
    {
        if (_closed) return;
        _thumbQueue.Clear(); // 旧占位符随重建作废
        _itemChecks.Clear();
        _listPanel.Children.Clear();
        _itemBorders.Clear();

        var items = Filtered().ToList();
        _visibleItems = items;

        if (_selectMode)
        {
            // 切换筛选后只保留仍可见的选中项，避免误删看不见的数据
            if (_selected.Count > 0)
                _selected.IntersectWith(items);
            _listPanel.Children.Add(BuildSelectAllRow());
        }

        foreach (var entry in items)
        {
            var item = BuildItem(entry);
            _itemBorders.Add(item);
            _listPanel.Children.Add(item);
        }

        // 键盘高亮复位到第一条（多选模式无键盘导航）
        _selIndex = !_selectMode && items.Count > 0 ? 0 : -1;
        ApplySelHighlight();

        bool empty = items.Count == 0;
        _emptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _listPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _emptyText.Text = empty
            ? (string.IsNullOrEmpty(_searchBox.Text) && _datePicker.SelectedDate == null
                ? "暂无剪贴板记录"
                : "没有匹配的记录")
            : "";

        var archived = _clipboard.ArchivedCount;
        _infoText.Text = $"共 {items.Count} 条" +
                         (items.Count != _clipboard.Entries.Count
                             ? $" / {_clipboard.Entries.Count}"
                             : "") +
                         (archived > 0 ? $" · 已归档 {archived} 条" : "");

        if (_selectMode) RefreshSelectUi();

        // 启动缩略图懒加载泵
        if (!_thumbPumping) PumpThumbs();
    }

    private Border BuildItem(ClipboardEntry entry)
    {
        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Brushes.White,
            BorderBrush = NormalBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 6),
            Margin = new Thickness(4, 3, 4, 3),
            Cursor = Cursors.Hand,
            Tag = entry,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 内容区
        if (entry.Type == ClipboardEntryType.Image)
        {
            var placeholder = new Border
            {
                MinHeight = 60,
                MinWidth = 60,
                MaxWidth = 170,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(6),
                Background = ToolbarBg,
                Child = new TextBlock
                {
                    Text = "加载中…",
                    FontSize = 11,
                    Foreground = TextSecondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(18, 20, 18, 20),
                },
            };
            grid.Children.Add(placeholder);
            QueueThumb(placeholder, entry);
        }
        else if (entry.Type == ClipboardEntryType.Files)
        {
            var files = entry.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var first = files.Length > 0 ? files[0] : "";
            var fileName = System.IO.Path.GetFileName(first);
            var filesPanel = new StackPanel();
            var head = new TextBlock
            {
                Text = "📁 " + (string.IsNullOrEmpty(fileName) ? first : fileName),
                FontSize = 13,
                Foreground = TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 2),
            };
            filesPanel.Children.Add(head);
            // 源目录（粘贴时直接引用源文件，不额外拷贝）
            var dir = System.IO.Path.GetDirectoryName(first);
            if (!string.IsNullOrEmpty(dir))
            {
                filesPanel.Children.Add(new TextBlock
                {
                    Text = "来源：" + dir,
                    FontSize = 11,
                    Foreground = TextSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            if (files.Length > 1)
            {
                filesPanel.Children.Add(new TextBlock
                {
                    Text = $"共 {files.Length} 个文件",
                    FontSize = 11.5,
                    Foreground = TextSecondary,
                });
            }
            grid.Children.Add(filesPanel);
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = entry.Text,
                FontSize = 13,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 54, // 约 3 行，超出截断
                Margin = new Thickness(0, 2, 24, 2),
            });
        }
        Grid.SetRow(grid.Children[^1], 0);

        // 底部：时间（精确到秒）+ 类型徽标
        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var badge = new TextBlock
        {
            Text = entry.Type switch
            {
                ClipboardEntryType.Image => "图片",
                ClipboardEntryType.Files => "文件",
                _ => string.IsNullOrEmpty(entry.TextFileName) ? "文本" : "长文本",
            },
            FontSize = 10.5,
            Foreground = BadgeFg,
            Background = BadgeBg,
            Padding = new Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(badge, Dock.Right);
        footer.Children.Add(badge);
        var time = new TextBlock
        {
            Text = entry.CopiedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            FontSize = 11,
            Foreground = TextSecondary,
        };
        footer.Children.Add(time);
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);

        // 右上角删除按钮（悬停时可见）
        var deleteBtn = new Button
        {
            Content = "✕",
            Width = 22,
            Height = 22,
            FontSize = 10,
            Foreground = TextSecondary,
            Background = Brushes.White,
            BorderBrush = NormalBorder,
            BorderThickness = new Thickness(1),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -2, -2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "删除此条",
        };
        // 隧道阶段拦截并置 Handled：避免触发条目的点击粘贴
        deleteBtn.PreviewMouseLeftButtonUp += (s, e) =>
        {
            e.Handled = true;
            DeleteEntry(entry);
        };
        Grid.SetRow(deleteBtn, 0);
        grid.Children.Add(deleteBtn);

        // 多选模式：左侧加复选框（点击复选框由 ButtonBase 拦截，不冒泡到条目）
        if (_selectMode)
        {
            var check = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                IsChecked = _selected.Contains(entry),
                Cursor = Cursors.Hand,
                Focusable = false,
            };
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _selected.Add(entry);
                else _selected.Remove(entry);
                _deleteArmed = false;
                RefreshSelectUi();
            };
            _itemChecks[entry] = check;

            var dock = new DockPanel();
            DockPanel.SetDock(check, Dock.Left);
            dock.Children.Add(check);
            dock.Children.Add(grid);
            item.Child = dock;
        }
        else
        {
            item.Child = grid;
        }

        // 悬停：高亮 + 显示删除按钮
        item.MouseEnter += (_, _) =>
        {
            item.Background = ItemHoverBg;
            item.BorderBrush = HoverBorder;
            deleteBtn.Opacity = 0.95;
        };
        item.MouseLeave += (_, _) =>
        {
            // 恢复外观时区分键盘选中态（选中项保持高亮）
            bool sel = !_selectMode && _itemBorders.IndexOf(item) == _selIndex;
            item.Background = sel ? ItemHoverBg : Brushes.White;
            item.BorderBrush = sel ? HoverBorder : NormalBorder;
            deleteBtn.Opacity = 0;
        };

        // 点击条目：粘贴（多选模式下切换勾选）
        item.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_selectMode)
            {
                ToggleItem(entry);
                return;
            }
            PasteAndClose(entry);
        };

        return item;
    }

    /// <summary>删除单条并刷新（保持当前搜索/筛选状态）</summary>
    private void DeleteEntry(ClipboardEntry entry)
    {
        _clipboard.Delete(entry);
        // Changed 事件会触发 OnEntriesChanged -> RebuildList
    }

    // ---------------- 批量清除（多选模式） ----------------

    /// <summary>进入多选模式：清空已选，条目出现复选框 + 顶部全选行</summary>
    private void EnterSelectMode()
    {
        _selectMode = true;
        _selected.Clear();
        _deleteArmed = false;
        UpdateBatchButton();
        RebuildList();
    }

    /// <summary>退出多选模式</summary>
    private void ExitSelectMode()
    {
        _selectMode = false;
        _selected.Clear();
        _deleteArmed = false;
        UpdateBatchButton();
        RebuildList();
    }

    /// <summary>批量按钮随模式切换文案：批量清除 <-> 取消</summary>
    private void UpdateBatchButton()
    {
        if (_selectMode)
        {
            _batchButton.Content = "取消";
            _batchButton.ToolTip = "退出多选模式";
        }
        else
        {
            _batchButton.Content = "批量清除";
            _batchButton.ToolTip = "多选删除剪贴板记录";
        }
    }

    /// <summary>列表顶行：全选复选框 + 删除所选按钮（只作用于当前过滤结果）</summary>
    private UIElement BuildSelectAllRow()
    {
        var bar = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ToolbarBg,
            BorderBrush = NormalBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(4, 3, 4, 3),
        };

        var dock = new DockPanel();

        _deleteSelBtn = new Button
        {
            Content = "删除所选",
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Foreground = TextSecondary,
            Background = Brushes.White,
            BorderBrush = NormalBorder,
            IsEnabled = false, // 无选中时禁用
            Cursor = Cursors.Hand,
            ToolTip = "删除勾选的记录（需点两次确认）",
        };
        _deleteSelBtn.Click += (_, _) => OnDeleteSelectedClick();
        DockPanel.SetDock(_deleteSelBtn, Dock.Right);
        dock.Children.Add(_deleteSelBtn);

        _selectAllCheck = new CheckBox
        {
            Content = "全选",
            FontSize = 12.5,
            Foreground = TextPrimary,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        _selectAllCheck.Click += (_, _) => ToggleSelectAll();
        dock.Children.Add(_selectAllCheck);

        bar.Child = dock;
        return bar;
    }

    /// <summary>全选 / 取消全选：只作用于当前过滤结果（含搜索 + 日期筛选）</summary>
    private void ToggleSelectAll()
    {
        bool all = _visibleItems.Count > 0 && _visibleItems.All(_selected.Contains);
        foreach (var e in _visibleItems)
        {
            if (all) _selected.Remove(e);
            else _selected.Add(e);
            if (_itemChecks.TryGetValue(e, out var check))
                check.IsChecked = !all;
        }
        _deleteArmed = false;
        RefreshSelectUi();
    }

    /// <summary>点击条目本体时切换其勾选（复选框同步）</summary>
    private void ToggleItem(ClipboardEntry entry)
    {
        if (!_selected.Remove(entry)) _selected.Add(entry);
        if (_itemChecks.TryGetValue(entry, out var check))
            check.IsChecked = _selected.Contains(entry);
        _deleteArmed = false;
        RefreshSelectUi();
    }

    /// <summary>刷新多选 UI：全选框三态（无选 / 部分 / 全选）+ 删除按钮文字 / 可用性</summary>
    private void RefreshSelectUi()
    {
        if (!_selectMode || _selectAllCheck is null || _deleteSelBtn is null) return;

        int visible = _visibleItems.Count;
        int sel = _visibleItems.Count(_selected.Contains);

        _selectAllCheck.IsChecked = sel == 0 ? false : sel == visible ? true : null;

        if (_deleteArmed)
        {
            _deleteSelBtn.Content = $"⚠ 确认删除 {sel} 条";
            _deleteSelBtn.Foreground = Brushes.White;
            _deleteSelBtn.Background = Danger;
            _deleteSelBtn.BorderBrush = Danger;
        }
        else
        {
            _deleteSelBtn.Content = sel > 0 ? $"删除所选 ({sel})" : "删除所选";
            _deleteSelBtn.Foreground = sel > 0 ? BadgeFg : TextSecondary;
            _deleteSelBtn.Background = Brushes.White;
            _deleteSelBtn.BorderBrush = sel > 0 ? HoverBorder : NormalBorder;
        }
        _deleteSelBtn.IsEnabled = sel > 0;
    }

    /// <summary>删除所选：第一次点击武装确认（避免误删），第二次执行</summary>
    private void OnDeleteSelectedClick()
    {
        if (_selected.Count == 0) return;
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            RefreshSelectUi();
            return;
        }

        var toDelete = _selected.ToList();
        // 先退出多选模式再删除，避免 Changed -> RebuildList 在旧模式下重建
        _selectMode = false;
        _selected.Clear();
        _deleteArmed = false;
        UpdateBatchButton();
        _clipboard.DeleteRange(toDelete);
        // Changed 事件会触发 OnEntriesChanged -> RebuildList
    }

    private void OnEntriesChanged()
    {
        // 来自后台记录的变更，回到 UI 线程刷新
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closed) RebuildList();
        });
    }

    // ---------------- 缩略图懒加载 ----------------

    private void QueueThumb(Border placeholder, ClipboardEntry entry)
    {
        _thumbQueue.Add((placeholder, entry));
    }

    /// <summary>后台优先级逐张解码缩略图，避免大量图片时打开卡顿</summary>
    private void PumpThumbs()
    {
        _thumbPumping = true;
        if (_closed || _thumbQueue.Count == 0)
        {
            _thumbPumping = false;
            return;
        }

        var (placeholder, entry) = _thumbQueue[0];
        _thumbQueue.RemoveAt(0);

        var img = _clipboard.LoadImage(entry, 180); // 解码宽度 180，展示足够
        if (img != null)
        {
            placeholder.MinWidth = 0;
            placeholder.MinHeight = 0;
            placeholder.Child = new Image
            {
                Source = img,
                Stretch = Stretch.Uniform,
                MaxHeight = 76,
                MaxWidth = 170,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }
        else
        {
            placeholder.Child = new TextBlock
            {
                Text = "图片已丢失",
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(18, 20, 18, 20),
            };
        }

        Dispatcher.BeginInvoke(PumpThumbs, DispatcherPriority.Background);
    }

    // ---------------- 粘贴 ----------------

    /// <summary>
    /// 选中条目粘贴：卸钩子 → 写回剪贴板 → 关窗 → 模拟 Ctrl+V。
    /// 默认（钩子）模式下前台从未离开目标窗口，无需任何焦点恢复，按键直接命中；
    /// IME 模式（弹窗被点激活）下焦点在弹窗，须按"前台→焦点→解挂"时序归还后再模拟。
    /// </summary>
    private void PasteAndClose(ClipboardEntry entry)
    {
        UninstallHooks(); // 先卸钩子：注入的 Ctrl+V 不能被自己吞掉

        _clipboard.SetEntryToClipboard(entry);
        _clipboard.Promote(entry); // 置顶（写回剪贴板已被抑制记录）

        var target = _targetHwnd; // 窗口关闭后再取字段不可靠，先存局部
        var foreBefore = NativeMethods.GetForegroundWindow();

        if (target == IntPtr.Zero)
        {
            // 无有效粘贴目标（按热键时前台是本应用窗口）：仅写回剪贴板置顶，不模拟按键
            Close();
            Program.LogDiag("粘贴跳过：无有效粘贴目标，仅写回剪贴板");
            return;
        }

        // IME 模式下焦点被弹窗持有，精确归还到弹窗打开时的焦点子窗口（输入框）。
        // 时序铁律：前台与焦点必须同线程——先 SetForegroundWindow 再 SetFocus，
        // 最后解除挂接、关窗；顺序反了焦点会在竞态中归零（目标焦点=0x0 的教训）
        if (_imeMode || _attached)
        {
            try
            {
                var appThread = NativeMethods.GetCurrentThreadId();
                var targetThread = NativeMethods.GetWindowThreadProcessId(target, out _);
                if (targetThread != 0 && targetThread != appThread)
                {
                    if (!_attached)
                    {
                        _targetThreadId = targetThread;
                        _attached = NativeMethods.AttachThreadInput(appThread, targetThread, true);
                    }
                    NativeMethods.SetForegroundWindow(target); // 挂接中调用不受系统前台锁定限制
                    NativeMethods.SetFocus(NativeMethods.IsWindow(_focusHwnd) ? _focusHwnd : target);
                    DetachFromTarget();
                }
                else
                {
                    // 同进程目标（如本应用主窗口/历史窗口）：同线程直接切前台落焦点
                    NativeMethods.SetForegroundWindow(target);
                    NativeMethods.SetFocus(NativeMethods.IsWindow(_focusHwnd) ? _focusHwnd : target);
                }
            }
            catch (Exception ex)
            {
                Program.LogError("粘贴前归还焦点", ex);
            }
        }

        Close();
        Program.LogDiag($"粘贴开始 ime={_imeMode} target=0x{target:X} 关窗前前台=0x{foreBefore:X} 前台即目标={foreBefore == target} 目标标题={WindowTitle(target)}");

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Task.Delay(50); // 等剪贴板写入与关窗稳定（前台本就未变，无需长轮询）
                RestoreTargetForeground(target); // 安全网：正常路径目标一直在前台，直接通过
                // 轮询确认目标真正在前台（IME 归还路径可能需要几帧），确保按键不落空
                int waited = 0;
                while (NativeMethods.GetForegroundWindow() != target && waited < 400)
                {
                    await Task.Delay(20);
                    waited += 20;
                }
                await Task.Delay(30);

                // 释放可能仍按住的 Alt（Ctrl+Alt+V 触发时），避免组合成其他快捷键
                NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                // 模拟 Ctrl+V
                NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
                NativeMethods.keybd_event(0x56, 0, 0, UIntPtr.Zero); // 'V'
                NativeMethods.keybd_event(0x56, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Program.LogDiag($"已发 Ctrl+V ime={_imeMode} 等待={waited}ms 前台即目标={NativeMethods.GetForegroundWindow() == target}");
            }
            catch (Exception ex)
            {
                Program.LogError("剪贴板粘贴", ex);
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// 兜底：把粘贴目标设回前台。非激活弹窗下目标本就一直是前台，正常路径直接通过；
    /// 若被其他程序意外抢走，则用 AttachThreadInput 挂接目标线程绕过系统前台锁定，
    /// 仍失败再用 Alt 抬起事件兜底重试。
    /// </summary>
    private static void RestoreTargetForeground(IntPtr target)
    {
        if (target == IntPtr.Zero) return;
        try
        {
            if (NativeMethods.GetForegroundWindow() == target) return; // 正常路径：从未失焦

            var appThread = NativeMethods.GetCurrentThreadId();
            var targetThread = NativeMethods.GetWindowThreadProcessId(target, out _);
            if (targetThread != 0 && targetThread != appThread)
            {
                NativeMethods.AttachThreadInput(appThread, targetThread, true);
                try { NativeMethods.SetForegroundWindow(target); }
                finally { NativeMethods.AttachThreadInput(appThread, targetThread, false); }
            }
            else
            {
                NativeMethods.SetForegroundWindow(target);
            }

            // 兜底：注入一次 Alt 抬起事件，让系统认为本进程刚收到用户输入，再试设前台
            if (NativeMethods.GetForegroundWindow() != target)
            {
                NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                NativeMethods.SetForegroundWindow(target);
            }
            Program.LogDiag($"兜底恢复前台 结果={NativeMethods.GetForegroundWindow() == target}");
        }
        catch (Exception ex)
        {
            Program.LogError("恢复粘贴目标焦点", ex);
        }
    }

    /// <summary>取窗口标题（诊断日志用）</summary>
    private static string WindowTitle(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(128);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return "?";
        }
    }

    // ---------------- 窗口行为 ----------------

    // 非激活显示（ShowActivated=false + WS_EX_NOACTIVATE + WM_MOUSEACTIVATE→MA_NOACTIVATE）：
    // 同 Win+V 机制，弹窗全程不夺走目标窗口的激活与焦点；键盘由低级钩子喂入，粘贴时直接模拟 Ctrl+V 即可命中。

    /// <summary>窗口定位到鼠标下方（物理像素计算，兼容多显示器 DPI）</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            if (_targetHwnd != IntPtr.Zero)
            {
                // 非激活窗口：鼠标点击也不夺走目标窗口的激活状态（无目标场景则正常激活便于搜索）
                var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                    (IntPtr)(exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
                // 双保险：WS_EX_NOACTIVATE 之外再拦 WM_MOUSEACTIVATE，任何点击都不激活弹窗
                System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(PanelWndProc);
            }

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.GetWindowRect(hwnd, out var rect);
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;

            var (vx, vy, vw, vh) = NativeMethods.GetVirtualScreen();
            int x = pt.X + 10, y = pt.Y + 14;
            if (x + w > vx + vw) x = pt.X - w - 10; // 右侧溢出：翻到鼠标左侧
            if (y + h > vy + vh) y = pt.Y - h - 14; // 下方溢出：翻到鼠标上方
            x = Math.Max(vx, Math.Min(x, vx + vw - w));
            y = Math.Max(vy, Math.Min(y, vy + vh - h));

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Program.LogError("剪贴板弹窗定位", ex);
        }

        RebuildList();
        _selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (_targetHwnd == IntPtr.Zero)
        {
            _searchBox.Focus(); // 极端：无前台窗口记录，正常激活模式，原生输入路径
            return;
        }

        // 键盘输入：低级键盘钩子捕获物理按键喂给搜索框（弹窗不取 Win32 焦点，目标窗口全程保持前台），
        // 另装低级鼠标钩子检测点击面板外部即关闭（同 Win+V）
        InstallHooks();

        // 关闭哨兵：非激活窗口不会触发 OnDeactivated，轮询检测前台离开"粘贴目标或弹窗自身"即关闭。
        // 须豁免弹窗自身：IME 模式激活弹窗期间前台是弹窗，不能误判为用户切换窗口
        _closeGuard = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _closeGuard.Tick += (_, _) =>
        {
            if (_closed) return;
            var fore = NativeMethods.GetForegroundWindow();
            if (fore != _targetHwnd && fore != _selfHwnd)
            {
                Program.LogDiag($"哨兵关窗 前台=0x{fore:X} 目标=0x{_targetHwnd:X} 弹窗=0x{_selfHwnd:X}");
                UninstallHooks();
                DetachFromTarget(); // 用户已切到其他窗口：不归还焦点以免抢回前台，仅解除挂接
                Close();
            }
        };
        _closeGuard.Start();
    }

    /// <summary>面板窗口过程：点击不激活（保持目标窗口前台）</summary>
    private IntPtr PanelWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)NativeMethods.WM_MOUSEACTIVATE && _targetHwnd != IntPtr.Zero)
        {
            handled = true;
            return (IntPtr)NativeMethods.MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    // ---------------- 低级钩子：非激活窗口的键盘/鼠标输入 ----------------

    /// <summary>安装低级键盘/鼠标钩子（UI 线程装，回调须经本线程消息循环分发）</summary>
    private void InstallHooks()
    {
        try
        {
            _targetThreadId = NativeMethods.GetWindowThreadProcessId(_targetHwnd, out _);
            // 种子修饰键状态：装钩子前的物理状态仍准确（之后被吞的键 GetAsyncKeyState 不再更新）
            _modCtrl = KeyIsDown(NativeMethods.VK_CONTROL);
            _modShift = KeyIsDown(NativeMethods.VK_LSHIFT) || KeyIsDown(NativeMethods.VK_RSHIFT);
            _modAlt = KeyIsDown(NativeMethods.VK_MENU);

            var hMod = NativeMethods.GetModuleHandle(null);
            _keyHookProc = KeyHookProc;
            _mouseHookProc = MouseHookProc;
            _keyHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyHookProc, hMod, 0);
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseHookProc, hMod, 0);
            Program.LogDiag($"低级钩子安装 键盘=0x{_keyHook:X} 鼠标=0x{_mouseHook:X} 目标tid={_targetThreadId} 前台=0x{NativeMethods.GetForegroundWindow():X}");
        }
        catch (Exception ex)
        {
            Program.LogError("剪贴板弹窗钩子", ex);
        }
    }

    /// <summary>卸载低级钩子（幂等；粘贴前、IME 模式、关窗时调用）</summary>
    private void UninstallHooks()
    {
        if (_keyHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _keyHookProc = null;
        _mouseHookProc = null;
    }

    private static bool KeyIsDown(byte vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>修饰键（含左右变体）放行系统：保持全局 Alt+Tab 等组合可用，自身状态在钩子里另行维护</summary>
    private static bool IsModifierKey(uint vk) => vk == NativeMethods.VK_SHIFT || vk == NativeMethods.VK_LSHIFT ||
        vk == NativeMethods.VK_RSHIFT || vk == NativeMethods.VK_CONTROL || vk == NativeMethods.VK_LCONTROL ||
        vk == NativeMethods.VK_RCONTROL || vk == NativeMethods.VK_MENU || vk == NativeMethods.VK_LMENU ||
        vk == NativeMethods.VK_RMENU;

    /// <summary>
    /// 低级键盘钩子回调：按键投递给面板窗口线程处理，不让其进入目标窗口。
    /// 放行：注入键（自己的 Ctrl+V）、Win 键 / Tab（Alt+Tab 等系统组合）、修饰键（维护全局状态一致性）。
    /// </summary>
    private IntPtr KeyHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION && !_imeMode && !_closed)
        {
            var k = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            // 自己注入的 Ctrl+V 必须放行，否则粘贴会被自己吃掉
            if ((k.flags & NativeMethods.LLKHF_INJECTED) == 0)
            {
                uint msg = (uint)wParam;
                bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
                if (down || up)
                {
                    // 吞键后 GetAsyncKeyState 不再更新，修饰键状态在钩子里自行维护
                    if (k.vkCode == NativeMethods.VK_CONTROL || k.vkCode == NativeMethods.VK_LCONTROL || k.vkCode == NativeMethods.VK_RCONTROL)
                        _modCtrl = down;
                    else if (k.vkCode == NativeMethods.VK_SHIFT || k.vkCode == NativeMethods.VK_LSHIFT || k.vkCode == NativeMethods.VK_RSHIFT)
                        _modShift = down;
                    else if (k.vkCode == NativeMethods.VK_MENU || k.vkCode == NativeMethods.VK_LMENU || k.vkCode == NativeMethods.VK_RMENU)
                        _modAlt = down;
                }

                // Win 键 / Tab / 修饰键交给系统，其余吞掉（目标窗口收不到这颗键）
                bool pass = k.vkCode == NativeMethods.VK_LWIN || k.vkCode == NativeMethods.VK_RWIN ||
                            k.vkCode == NativeMethods.VK_TAB || IsModifierKey(k.vkCode);
                if (!pass && (down || up))
                {
                    if (down)
                    {
                        uint vk = k.vkCode, scan = k.scanCode;
                        Dispatcher.BeginInvoke(() => OnForwardedKey(vk, scan)); // 回调里只投递，不在钩子链上做翻译
                    }
                    return (IntPtr)1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// 低级鼠标钩子回调：点击落在面板外即关闭面板（不归还焦点），点击继续传给底层窗口（同 Win+V）。
    /// </summary>
    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION && !_closed)
        {
            uint msg = (uint)wParam;
            if (msg == NativeMethods.WM_LBUTTONDOWN || msg == NativeMethods.WM_RBUTTONDOWN ||
                msg == NativeMethods.WM_MBUTTONDOWN || msg == NativeMethods.WM_NCLBUTTONDOWN ||
                msg == NativeMethods.WM_NCRBUTTONDOWN || msg == NativeMethods.WM_NCMBUTTONDOWN)
            {
                var m = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (!IsInsidePanel(m.pt))
                {
                    UninstallHooks(); // 立即卸载，避免关闭过程中的点击再排队关窗
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_closed) return;
                        // 点击的目标窗口会自行接管前台，无需归还焦点
                        DetachFromTarget();
                        Close();
                    });
                }
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>点击是否落在面板可视区域内（DWM 紧凑边界排除阴影边距；同进程弹层视作内部）</summary>
    private bool IsInsidePanel(NativeMethods.POINT pt)
    {
        try
        {
            var hit = NativeMethods.WindowFromPoint(pt);
            if (hit != IntPtr.Zero &&
                NativeMethods.GetWindowThreadProcessId(hit, out var pid) != 0 &&
                pid == Environment.ProcessId)
                return true; // DatePicker 日历等同进程弹层

            // DWM 扩展边界不含透明阴影边距；失败回退 GetWindowRect
            if (NativeMethods.DwmGetWindowAttribute(_selfHwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out var r, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
                NativeMethods.GetWindowRect(_selfHwnd, out r);
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }
        catch
        {
            return true; // 查询失败宁可不关
        }
    }

    // ---------------- 按键分发与搜索框编辑（无焦点手动喂键） ----------------

    /// <summary>
    /// 钩子转发来的按键：面板级按键（Esc/回车/列表导航）分发，其余翻译成字符或编辑操作喂给搜索框。
    /// 搜索框不持有 Win32 焦点，一切编辑状态靠 CaretIndex/Selection 手动维护。
    /// </summary>
    private void OnForwardedKey(uint vk, uint scan)
    {
        if (_closed || _imeMode) return;

        switch (vk)
        {
            case NativeMethods.VK_ESCAPE:
                if (_selectMode) ExitSelectMode(); // 多选模式下 Esc 先退出，再次按才关窗
                else Close(); // 非激活模式下从未动过目标焦点，直接关闭即可
                return;
            case NativeMethods.VK_RETURN:
                PasteFromKeyboard();
                return;
            case NativeMethods.VK_UP:
                MoveSel(-1);
                return;
            case NativeMethods.VK_DOWN:
                MoveSel(1);
                return;
        }

        // Ctrl / Alt 组合：热键重按、编辑快捷键就地处理，其余吞掉（避免漏给目标窗口）
        if (_modCtrl || _modAlt)
        {
            if (_modCtrl && _modAlt && vk == 'V')
            {
                EnterImeMode(); // 热键重按：激活弹窗聚焦搜索框
                return;
            }
            if (_modCtrl && !_modAlt)
            {
                if (vk == 'A') _searchBox.SelectAll();
                else if (vk == 'V')
                {
                    // 把最新剪贴板文本粘进搜索框（过滤常用）
                    var latest = _clipboard.Entries.FirstOrDefault()?.Text;
                    if (!string.IsNullOrEmpty(latest)) InsertAtCaret(latest);
                }
                else if (vk == NativeMethods.VK_BACK) DeleteBackward(true); // Ctrl+Backspace 删一词
            }
            return;
        }

        switch (vk)
        {
            case NativeMethods.VK_BACK: DeleteBackward(false); return;
            case NativeMethods.VK_DELETE: DeleteForward(); return;
            case NativeMethods.VK_LEFT:
            case NativeMethods.VK_RIGHT:
            case NativeMethods.VK_HOME:
            case NativeMethods.VK_END: MoveCaretKey(vk); return;
        }

        // 字符键：用目标线程的键盘布局翻译（含 Shift 状态），插入光标处
        var state = new byte[256];
        NativeMethods.GetKeyboardState(state); // 锁定键状态（大小写等）
        SetKeyState(ref state[NativeMethods.VK_SHIFT], _modShift);
        SetKeyState(ref state[NativeMethods.VK_LSHIFT], _modShift);
        SetKeyState(ref state[NativeMethods.VK_CONTROL], false);
        SetKeyState(ref state[NativeMethods.VK_MENU], false);
        var hkl = NativeMethods.GetKeyboardLayout(_targetThreadId);
        var buf = new char[8];
        int n = NativeMethods.ToUnicodeEx(vk, scan, state, buf, buf.Length, 0, hkl);
        if (n > 0) InsertAtCaret(new string(buf, 0, n));
    }

    private static void SetKeyState(ref byte state, bool down)
    {
        if (down) state |= 0x80;
        else state &= 0x7F;
    }

    /// <summary>点击搜索框时把光标放到点击处（无 Win32 焦点，TextBox 不会自己落光标）</summary>
    private void PlaceSearchCaret(Point pos)
    {
        var text = _searchBox.Text ?? "";
        if (text.Length == 0)
        {
            _searchBox.CaretIndex = 0;
            return;
        }

        int index = _searchBox.GetCharacterIndexFromPoint(pos, true);
        if (index < 0)
        {
            _searchBox.CaretIndex = text.Length;
            return;
        }

        try
        {
            var rect = _searchBox.GetRectFromCharacterIndex(index);
            if (!rect.IsEmpty && pos.X > rect.X + rect.Width / 2)
                index++;
        }
        catch
        {
            // 布局未完成时用最近点
        }

        _searchBox.CaretIndex = Math.Clamp(index, 0, text.Length);
    }

    /// <summary>钩子模式下绘制搜索光标；IME / 无粘贴目标时用系统光标，这里隐藏</summary>
    private void UpdateSearchCaret()
    {
        if (_searchCaret == null || _closed) return;
        bool show = _targetHwnd != IntPtr.Zero && !_imeMode && _searchBox.SelectionLength == 0;
        if (!show || _searchCaret.Parent is not UIElement parent)
        {
            _searchCaret.Visibility = Visibility.Collapsed;
            return;
        }

        Rect rect;
        Point topLeft;
        try
        {
            rect = _searchBox.GetRectFromCharacterIndex(_searchBox.CaretIndex);
            if (rect.IsEmpty)
            {
                _searchCaret.Visibility = Visibility.Collapsed;
                return;
            }
            topLeft = _searchBox.TranslatePoint(new Point(rect.X, rect.Y), parent);
        }
        catch
        {
            _searchCaret.Visibility = Visibility.Collapsed;
            return;
        }
        _searchCaret.Height = Math.Max(1, rect.Height);
        _searchCaret.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
        _searchCaret.Opacity = 1;
        _caretPhase = true;
        _searchCaret.Visibility = Visibility.Visible;

        if (_caretBlink == null)
        {
            _caretBlink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _caretBlink.Tick += (_, _) =>
            {
                if (_searchCaret == null || _searchCaret.Visibility != Visibility.Visible) return;
                _caretPhase = !_caretPhase;
                _searchCaret.Opacity = _caretPhase ? 1 : 0;
            };
            _caretBlink.Start();
        }
    }

    /// <summary>替换选区插入文本，光标落在插入文本之后</summary>
    private void InsertAtCaret(string text)
    {
        int start = _searchBox.SelectionStart;
        _searchBox.SelectedText = text;
        _searchBox.CaretIndex = start + text.Length;
    }

    /// <summary>退格删除：先删选区；无选区删光标前一个字符（word=true 按词删）</summary>
    private void DeleteBackward(bool word)
    {
        if (_searchBox.SelectionLength > 0)
        {
            _searchBox.SelectedText = "";
            return;
        }
        int i = _searchBox.CaretIndex;
        if (i == 0) return;
        int j = word ? WordBound(_searchBox.Text, i, -1) : i - 1;
        _searchBox.Select(j, i - j);
        _searchBox.SelectedText = "";
    }

    /// <summary>Delete 删除：先删选区；无选区删光标后一个字符</summary>
    private void DeleteForward()
    {
        if (_searchBox.SelectionLength > 0)
        {
            _searchBox.SelectedText = "";
            return;
        }
        int i = _searchBox.CaretIndex;
        if (i >= _searchBox.Text.Length) return;
        _searchBox.Select(i, 1);
        _searchBox.SelectedText = "";
    }

    /// <summary>左右/Home/End 移动光标：Ctrl 按词跳，Shift 扩选；有选区先塌缩到边界</summary>
    private void MoveCaretKey(uint vk)
    {
        var text = _searchBox.Text;
        int selStart = _searchBox.SelectionStart, selLen = _searchBox.SelectionLength;
        int caret = _searchBox.CaretIndex;

        if (!_modShift && selLen > 0)
        {
            // 无 Shift 时先塌缩到选区边界（再按一次才移动）
            _searchBox.CaretIndex = vk == NativeMethods.VK_LEFT || vk == NativeMethods.VK_HOME ? selStart : selStart + selLen;
            return;
        }

        int target = vk switch
        {
            NativeMethods.VK_HOME => 0,
            NativeMethods.VK_END => text.Length,
            _ => _modCtrl
                ? (vk == NativeMethods.VK_LEFT ? WordBound(text, caret, -1) : WordBound(text, caret, 1))
                : caret + (vk == NativeMethods.VK_LEFT ? -1 : 1),
        };
        target = Math.Max(0, Math.Min(text.Length, target));

        if (_modShift)
        {
            // 扩选：锚点取原选区靠光标的另一端
            int anchor = caret == selStart ? selStart + selLen : selStart;
            _searchBox.Select(Math.Min(anchor, target), Math.Abs(anchor - target));
        }
        else
        {
            _searchBox.CaretIndex = target;
        }
    }

    /// <summary>词边界（Ctrl+左右跳词）：以空格分词，dir=-1 向左 / 1 向右</summary>
    private static int WordBound(string text, int from, int dir)
    {
        int i = from;
        if (dir < 0)
        {
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        }
        else
        {
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }
        return i;
    }

    // ---------------- 键盘列表导航（同 Win+V 上下键选条目） ----------------

    /// <summary>上下键移动高亮条目并滚动可视</summary>
    private void MoveSel(int delta)
    {
        if (_selectMode || _visibleItems.Count == 0) return;
        int next = _selIndex < 0
            ? (delta > 0 ? 0 : _visibleItems.Count - 1)
            : Math.Max(0, Math.Min(_visibleItems.Count - 1, _selIndex + delta));
        if (next == _selIndex) return;
        _selIndex = next;
        ApplySelHighlight();
        if (next >= 0 && next < _itemBorders.Count) _itemBorders[next].BringIntoView();
    }

    /// <summary>刷新键盘高亮外观（选中项复用悬停配色）</summary>
    private void ApplySelHighlight()
    {
        for (int i = 0; i < _itemBorders.Count; i++)
        {
            bool sel = i == _selIndex;
            _itemBorders[i].Background = sel ? ItemHoverBg : Brushes.White;
            _itemBorders[i].BorderBrush = sel ? HoverBorder : NormalBorder;
        }
    }

    /// <summary>回车：粘贴当前键盘高亮条目（无高亮时取第一条，多选模式下禁用）</summary>
    private void PasteFromKeyboard()
    {
        if (_selectMode || _visibleItems.Count == 0) return;
        int idx = _selIndex >= 0 && _selIndex < _visibleItems.Count ? _selIndex : 0;
        PasteAndClose(_visibleItems[idx]);
    }

    // ---------------- IME 模式（再次按下热键后真正激活，支持输入法） ----------------

    /// <summary>弹窗已开时再按热键：激活弹窗聚焦搜索框（输入法需要真实焦点）</summary>
    public void FocusWindow()
    {
        if (_closed) return;
        EnterImeMode();
    }

    /// <summary>
    /// 激活弹窗进入 IME 模式：输入法组合窗口只能挂到真正拥有焦点的 HWND。
    /// 仅再次按下热键时进入；点击搜索框不走这里，避免目标窗口失焦。
    /// 激活会暂时夺走目标前台（标题栏变灰），粘贴/Esc 时按"前台→焦点→解挂"时序归还。
    /// </summary>
    private void EnterImeMode()
    {
        if (_closed || _imeMode) return;
        _imeMode = true;
        UninstallHooks(); // 真实键盘直达弹窗，钩子必须卸掉（否则吞掉 IME 按键）
        try
        {
            var appThread = NativeMethods.GetCurrentThreadId();
            if (!_attached)
            {
                var targetThread = NativeMethods.GetWindowThreadProcessId(_targetHwnd, out _);
                if (targetThread != 0 && targetThread != appThread)
                {
                    _targetThreadId = targetThread;
                    _attached = NativeMethods.AttachThreadInput(appThread, targetThread, true); // 挂接中激活可绕前台锁
                }
            }
            if (_attached) NativeMethods.SetForegroundWindow(_selfHwnd);
            else Activate();
            _searchBox.Focusable = true; // 钩子模式下为 false，避免点击搜索框激活弹窗
            _searchBox.Focus();
            UpdateSearchCaret();
            Program.LogDiag($"IME 模式激活 前台=0x{NativeMethods.GetForegroundWindow():X} 弹窗=0x{_selfHwnd:X} 即前台={NativeMethods.GetForegroundWindow() == _selfHwnd}");
        }
        catch (Exception ex)
        {
            Program.LogError("剪贴板弹窗激活", ex);
        }
    }

    /// <summary>解除与目标线程的挂接（归还焦点后调用；OnClosed 兜底）</summary>
    private void DetachFromTarget()
    {
        if (!_attached) return;
        _attached = false;
        try
        {
            NativeMethods.AttachThreadInput(NativeMethods.GetCurrentThreadId(), _targetThreadId, false);
        }
        catch (Exception ex)
        {
            Program.LogError("解除目标线程挂接", ex);
        }
    }

    /// <summary>
    /// 归还键盘焦点到弹窗打开时的输入框，再解除挂接（IME 模式下 Esc 等不粘贴的关闭路径共用）。
    /// 钩子模式下从未动过目标焦点，仅解除挂接即可。
    /// </summary>
    private void RestoreFocusAndDetach()
    {
        try
        {
            var focusTarget = NativeMethods.IsWindow(_focusHwnd) ? _focusHwnd : _targetHwnd;
            if (_imeMode)
            {
                // 与粘贴同款时序：先切前台再落焦点，避免挂接解除时焦点归零
                var appThread = NativeMethods.GetCurrentThreadId();
                var targetThread = NativeMethods.GetWindowThreadProcessId(_targetHwnd, out _);
                if (targetThread != 0 && targetThread != appThread)
                {
                    if (!_attached)
                    {
                        _targetThreadId = targetThread;
                        _attached = NativeMethods.AttachThreadInput(appThread, targetThread, true);
                    }
                    NativeMethods.SetForegroundWindow(_targetHwnd);
                    NativeMethods.SetFocus(focusTarget);
                }
                else
                {
                    NativeMethods.SetForegroundWindow(_targetHwnd);
                    NativeMethods.SetFocus(focusTarget);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError("归还焦点", ex);
        }
        DetachFromTarget();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_selectMode) ExitSelectMode(); // 多选模式下 Esc 先退出，再次按才关窗
            else
            {
                RestoreFocusAndDetach(); // IME 模式归还焦点到原输入框；钩子模式无操作
                Close();
            }
            e.Handled = true;
        }
        // IME 模式下的列表导航与钩子模式保持一致
        else if (e.Key == Key.Up)
        {
            MoveSel(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            MoveSel(1);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // 点击弹窗外部：关闭（IME/正常激活模式触发；钩子模式由鼠标钩子与哨兵兜底）
        UninstallHooks();
        RestoreFocusAndDetach();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _closeGuard?.Stop();
        _caretBlink?.Stop();
        UninstallHooks(); // 兜底：任何路径漏掉的钩子都在此卸载
        DetachFromTarget(); // 兜底：任何路径漏掉的挂接都在此解除
        _thumbQueue.Clear();
        _clipboard.Changed -= OnEntriesChanged;
        base.OnClosed(e);
    }
}
