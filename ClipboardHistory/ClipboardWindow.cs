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
/// 剪贴板历史弹窗（Ctrl+Alt+V 在鼠标附近弹出，参考 Win+V）：
/// - 顶部：模糊搜索框 + 日期筛选（DatePicker）
/// - 列表：文本摘要 / 图片缩略图 / 文件列表，时间精确到秒，悬停显示删除按钮
/// - 点击条目：写回剪贴板 → 恢复原前台窗口 → 模拟 Ctrl+V 完成粘贴
/// - Esc / 失去焦点 自动关闭
/// </summary>
public class ClipboardWindow : Window
{
    private readonly ClipboardService _clipboard;
    /// <summary>弹窗打开前的前台窗口（粘贴目标）</summary>
    private readonly IntPtr _targetHwnd;

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

    public ClipboardWindow(ClipboardService clipboard, IntPtr targetHwnd)
    {
        _clipboard = clipboard;
        _targetHwnd = targetHwnd;

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
        };
        var searchGrid = new Grid();
        _placeholder = new TextBlock
        {
            Text = "搜索文本内容...",
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
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = string.IsNullOrEmpty(_searchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        // 回车：粘贴当前过滤结果的第一条（多选模式下禁用）
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !_selectMode)
            {
                var first = Filtered().FirstOrDefault();
                if (first != null) PasteAndClose(first);
            }
        };
        searchGrid.Children.Add(_placeholder);
        searchGrid.Children.Add(_searchBox);
        searchBorder.Child = searchGrid;
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
            _listPanel.Children.Add(BuildItem(entry));

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
            item.Background = Brushes.White;
            item.BorderBrush = NormalBorder;
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
    /// 选中条目：写回剪贴板 → 关闭弹窗 → 恢复原前台窗口 → 模拟 Ctrl+V。
    /// 延时等待焦点切换完成，避免目标应用丢失按键。
    /// </summary>
    private void PasteAndClose(ClipboardEntry entry)
    {
        _clipboard.SetEntryToClipboard(entry);
        _clipboard.Promote(entry); // 置顶（写回剪贴板已被抑制记录）

        Close();

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Task.Delay(120); // 等弹窗关闭、焦点稳定
                if (_targetHwnd != IntPtr.Zero)
                    NativeMethods.SetForegroundWindow(_targetHwnd);
                await Task.Delay(60);

                // 释放可能仍按住的 Alt（Ctrl+Alt+V 触发时），避免组合成其他快捷键
                NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                // 模拟 Ctrl+V
                NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
                NativeMethods.keybd_event(0x56, 0, 0, UIntPtr.Zero); // 'V'
                NativeMethods.keybd_event(0x56, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                Program.LogError("剪贴板粘贴", ex);
            }
        }), DispatcherPriority.Background);
    }

    // ---------------- 窗口行为 ----------------

    /// <summary>窗口定位到鼠标下方（物理像素计算，兼容多显示器 DPI）</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            NativeMethods.GetCursorPos(out var pt);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
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
        _searchBox.Focus();
        _searchBox.CaretIndex = 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_selectMode) ExitSelectMode(); // 多选模式下 Esc 先退出，再次按才关窗
            else Close();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // 点击弹窗外部：关闭（同 Win+V 行为）
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _thumbQueue.Clear();
        _clipboard.Changed -= OnEntriesChanged;
        base.OnClosed(e);
    }
}
