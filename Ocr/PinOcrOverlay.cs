using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnipPin.Core.Services;

namespace SnipPin.Ocr;

/// <summary>
/// 贴图"选择文字"覆盖层（企业微信式）：全图 OCR 后，鼠标在文字上按住拖动按字选择，
/// Ctrl+C 复制、Ctrl+A 全选、Esc 退出；空白处按下仍是拖动贴图。
/// 选中效果由覆盖层自绘（不改变贴图本身）；字框与选中范围均为源图物理像素坐标，
/// 按当前显示系数缩放；贴图缩放时由 Rescale 重算重绘。
/// </summary>
internal sealed class PinOcrOverlay
{
    /// <summary>可选择的单字/单词（全局阅读顺序）</summary>
    private sealed class Word
    {
        public required string Text;
        public required Rect Box;      // 物理像素
        public int LineIndex;
        public int GlobalIndex;
    }

    /// <summary>OCR 行（含逐字框与"字符下标 → 字块下标"映射，复制时用于还原空格）</summary>
    private sealed class Line
    {
        public required string Text;
        public required Rect Box;
        public required List<Word> Words;
        public required int[] CharToWord;  // Text 下标 → 行内字块下标（空白为 -1）
        public int FirstGlobal;            // 本行首个字在全局顺序中的下标
    }

    private readonly PinWindow _owner;
    private readonly IOcrService _ocr;

    private readonly List<Line> _lines = new();
    private readonly List<Word> _words = new();  // 全局阅读顺序：行先按 y 再按 x，行内按 OCR 顺序

    private SelectionLayer? _layer;   // 自绘覆盖层（挂图片栈）
    private Border? _bar;             // 底部操作条（唯一实例，只替换内容，避免旧状态条残留）
    private TextBlock? _countText;
    private Button? _copyButton;

    private int _anchor = -1;         // 选择起点（全局字下标）
    private int _focus = -1;          // 选择终点（拖动实时更新）
    private int _hover = -1;
    private bool _dragging;

    private int _sessionId;           // 会话号：识别返回时校验，丢弃上一次会话的迟到结果

    private static readonly Brush SelectedBrush =
        Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x28, 0x7A, 0xE0)));
    private static readonly Brush HoverBrush =
        Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x28, 0x7A, 0xE0)));

    public bool Active { get; private set; }

    public PinOcrOverlay(PinWindow owner, IOcrService ocr)
    {
        _owner = owner;
        _ocr = ocr;
    }

    /// <summary>进入选择模式：识别 composited（贴图当前所见合成图）并建立可选字块</summary>
    public async void Enter(BitmapSource composited)
    {
        if (Active) return;
        Active = true;
        int session = ++_sessionId;
        _anchor = _focus = _hover = -1;
        _dragging = false;
        _lines.Clear();
        _words.Clear();

        // 自绘覆盖层：铺满图片区域，命中由内部字框判定（空白处放行给窗口拖动贴图）
        _layer = new SelectionLayer(this);
        _owner.ImageHost.Children.Add(_layer);
        Rescale();
        _owner.FocusPin();

        _bar ??= CreateBar();
        SetBar(BuildText("⏳ 正在识别，请稍候…"), BarButton("取消", Exit));

        try
        {
            var result = await _ocr.RecognizeLinesAsync(composited);
            if (!Active || session != _sessionId || !_owner.IsLoaded) return; // 已退出/已重进/窗口已关

            BuildModel(result);
            if (_words.Count == 0)
            {
                SetBar(BuildText("未识别到文字"), BarButton("退出", Exit));
                return;
            }

            _countText = BuildText("");
            _copyButton = BarButton("📋 复制", CopySelected);
            SetBar(_countText, _copyButton, BarButton("全选", SelectAll), BarButton("✕ 退出", Exit));
            UpdateCount();
            _layer?.InvalidateVisual();
        }
        catch (Exception ex)
        {
            if (session != _sessionId) return;
            Program.LogError("OCR 选择模式", ex);
            SetBar(BuildText($"❌ 识别失败：{ex.Message}"), BarButton("关闭", Exit));
        }
    }

    /// <summary>退出选择模式，恢复贴图正常交互</summary>
    public void Exit()
    {
        if (!Active) return;
        Active = false;
        _sessionId++; // 使进行中的识别结果作废
        _dragging = false;
        _anchor = _focus = _hover = -1;

        if (_layer != null)
        {
            _layer.ReleaseMouseCapture();
            _owner.ImageHost.Children.Remove(_layer);
            _layer = null;
        }
        if (_bar != null)
        {
            _owner.OverlayBarHost.Children.Remove(_bar);
            _bar = null;
        }
        _countText = null;
        _copyButton = null;
        _lines.Clear();
        _words.Clear();
    }

    /// <summary>贴图缩放/DPI 变化后同步覆盖层尺寸并重绘</summary>
    public void Rescale()
    {
        if (_layer == null) return;
        var size = _owner.ImageHostSize;
        _layer.Width = size.Width;
        _layer.Height = size.Height;
        _layer.InvalidateVisual();
    }

    /// <summary>按键分发（PinWindow 在 KeyDown 开头调用）：Ctrl+C 复制、Ctrl+A 全选、Esc 退出</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!Active) return false;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.C) { CopySelected(); e.Handled = true; return true; }
        if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; return true; }
        if (e.Key == Key.Escape) { Exit(); e.Handled = true; return true; }
        return false;
    }

    // ---------------- 模型构建 ----------------

    /// <summary>OCR 结果 → 行/字块模型（行按 y 再 x 排序；逐字框缺失时退化为整行一块）</summary>
    private void BuildModel(IReadOnlyList<OcrTextLine> lines)
    {
        var ordered = lines.OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left).ToList();
        int global = 0;
        foreach (var src in ordered)
        {
            var words = new List<Word>();
            var items = src.Words is { Count: > 0 }
                ? src.Words
                : new[] { new OcrWord(src.Text, src.Box) }; // 兜底：无逐字框时整行一个可选项
            foreach (var w in items)
            {
                words.Add(new Word
                {
                    Text = w.Text,
                    Box = w.Box,
                    LineIndex = _lines.Count,
                    GlobalIndex = global++,
                });
            }
            var line = new Line
            {
                Text = src.Text,
                Box = src.Box,
                Words = words,
                CharToWord = BuildCharMap(src.Text, words),
                FirstGlobal = global - words.Count,
            };
            _lines.Add(line);
            _words.AddRange(words);
        }
    }

    /// <summary>为行内每个字符找到所属字块下标（顺序匹配，自动跳过空白）</summary>
    private static int[] BuildCharMap(string text, List<Word> words)
    {
        var map = new int[text.Length];
        Array.Fill(map, -1);
        int cursor = 0;
        for (int w = 0; w < words.Count; w++)
        {
            var wt = words[w].Text;
            if (string.IsNullOrEmpty(wt) || cursor >= text.Length) continue;
            int at = text.IndexOf(wt, cursor, StringComparison.Ordinal);
            if (at < 0) at = text.IndexOf(wt, StringComparison.Ordinal);
            if (at < 0) continue;
            for (int i = 0; i < wt.Length && at + i < text.Length; i++) map[at + i] = w;
            cursor = at + wt.Length;
        }
        return map;
    }

    // ---------------- 鼠标交互（由 SelectionLayer 转发） ----------------

    internal void OnLayerMouseDown(MouseButtonEventArgs e)
    {
        if (!Active || _layer == null) return;
        var phys = ToPhysical(e);
        int hit = HitTest(phys);

        if (hit < 0)
        {
            // 空白处：清除选择，不标记 handled → 冒泡到窗口拖动贴图
            if (_anchor >= 0 || _focus >= 0)
            {
                _anchor = _focus = -1;
                UpdateCount();
                _layer.InvalidateVisual();
            }
            return;
        }

        if (e.ClickCount == 2)
        {
            // 双击：选中整行
            var line = _lines[_words[hit].LineIndex];
            _anchor = line.FirstGlobal;
            _focus = line.FirstGlobal + line.Words.Count - 1;
            _hover = -1;
            e.Handled = true;
            UpdateCount();
            _layer.InvalidateVisual();
            return;
        }

        _anchor = _focus = hit;   // 按下即选中该字，拖动再扩展
        _hover = -1;
        _dragging = true;
        _layer.CaptureMouse();
        Keyboard.Focus(_owner);   // 确保 Ctrl+C 能到窗口
        e.Handled = true;         // 阻断了窗口的 DragMove
        UpdateCount();
        _layer.InvalidateVisual();
    }

    internal void OnLayerMouseMove(MouseEventArgs e)
    {
        if (!Active || _layer == null) return;
        var phys = ToPhysical(e);
        int hit = HitTest(phys);

        if (_dragging)
        {
            if (hit >= 0 && hit != _focus)
            {
                _focus = hit;      // 拖过空白时保持上一个位置，选择连续
                UpdateCount();
                _layer.InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (hit != _hover)
        {
            _hover = hit;
            _layer.InvalidateVisual();
        }
        _layer.Cursor = hit >= 0 ? Cursors.IBeam : Cursors.Arrow;
    }

    internal void OnLayerMouseUp(MouseButtonEventArgs e)
    {
        if (!Active || !_dragging) return;
        _dragging = false;
        _layer?.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>鼠标事件位置（层内 DIP）→ 源图物理像素</summary>
    private Point ToPhysical(MouseEventArgs e)
    {
        var p = e.GetPosition(_layer);
        double s = _owner.OcrRenderScale;
        return s > 0 ? new Point(p.X / s, p.Y / s) : p;
    }

    /// <summary>命中字块（矩形外扩，拖动时更宽松）；返回全局下标，-1 表示空白</summary>
    private int HitTest(Point phys)
    {
        double inflate = _dragging ? 6 : 2;
        int best = -1;
        double bestArea = double.MaxValue;
        for (int i = 0; i < _words.Count; i++)
        {
            var b = _words[i].Box;
            b.Inflate(inflate, inflate);
            if (!b.Contains(phys)) continue;
            double area = b.Width * b.Height;
            if (area < bestArea) { bestArea = area; best = i; }
        }
        return best;
    }

    // ---------------- 选择与复制 ----------------

    private bool HasSelection => _anchor >= 0 && _focus >= 0;

    private void SelectAll()
    {
        if (_words.Count == 0) return;
        _anchor = 0;
        _focus = _words.Count - 1;
        UpdateCount();
        _layer?.InvalidateVisual();
    }

    private void UpdateCount()
    {
        if (_countText == null) return;
        if (!HasSelection)
        {
            _countText.Text = "在文字上按住拖动选择";
            return;
        }
        int lo = Math.Min(_anchor, _focus), hi = Math.Max(_anchor, _focus);
        int lineCount = _lines.Count(l =>
        {
            int last = l.FirstGlobal + l.Words.Count - 1;
            return !(last < lo || l.FirstGlobal > hi);
        });
        _countText.Text = $"已选 {hi - lo + 1} 字 · {lineCount} 行";
    }

    private async void CopySelected()
    {
        if (_copyButton == null || !HasSelection) return;
        var text = BuildSelectedText();
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            _copyButton.Content = "✓ 已复制";
            await Task.Delay(1200);
            if (_copyButton != null) _copyButton.Content = "📋 复制";
        }
        catch (Exception ex)
        {
            Program.LogError("OCR 选择复制", ex);
        }
    }

    /// <summary>按行拼接选中文字（行内取字符区间，自动保留词间空格、去掉首尾空白）</summary>
    private string BuildSelectedText()
    {
        if (!HasSelection) return "";
        int lo = Math.Min(_anchor, _focus), hi = Math.Max(_anchor, _focus);
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            int last = line.FirstGlobal + line.Words.Count - 1;
            if (last < lo || line.FirstGlobal > hi) continue;
            int from = Math.Max(lo, line.FirstGlobal) - line.FirstGlobal;
            int to = Math.Min(hi, last) - line.FirstGlobal;
            var part = ExtractLine(line, from, to);
            if (part.Length > 0) sb.AppendLine(part);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>取一行中第 from..to 个字块对应的原文片段（空格在字块之外，靠字符映射还原）</summary>
    private static string ExtractLine(Line line, int from, int to)
    {
        int start = -1, end = -1;
        for (int i = 0; i < line.CharToWord.Length; i++)
        {
            int w = line.CharToWord[i];
            if (w < from || w > to) continue;
            if (start < 0) start = i;
            end = i;
        }
        return start < 0 ? "" : line.Text.Substring(start, end - start + 1).Trim();
    }

    // ---------------- 覆盖层绘制 ----------------

    /// <summary>绘制选中/悬停字框（仅在字框内着色，图片其余部分保持原样）</summary>
    private void RenderSelection(DrawingContext dc)
    {
        double s = _owner.OcrRenderScale;
        if (s <= 0) return;

        if (HasSelection)
        {
            int lo = Math.Min(_anchor, _focus), hi = Math.Max(_anchor, _focus);
            for (int i = lo; i <= hi; i++)
            {
                var b = _words[i].Box;
                dc.DrawRoundedRectangle(SelectedBrush, null,
                    new Rect(b.X * s, b.Y * s, b.Width * s, b.Height * s), 2, 2);
            }
        }
        else if (_hover >= 0 && !_dragging)
        {
            var b = _words[_hover].Box;
            dc.DrawRoundedRectangle(HoverBrush, null,
                new Rect(b.X * s, b.Y * s, b.Width * s, b.Height * s), 2, 2);
        }
    }

    /// <summary>覆盖层：自绘 + 全区域可命中（命中判定在事件回调里做，空白处放行）</summary>
    private sealed class SelectionLayer : FrameworkElement
    {
        private readonly PinOcrOverlay _owner;

        public SelectionLayer(PinOcrOverlay owner)
        {
            _owner = owner;
            Cursor = Cursors.Arrow;
            MouseLeftButtonDown += (_, e) => owner.OnLayerMouseDown(e);
            MouseMove += (_, e) => owner.OnLayerMouseMove(e);
            MouseLeftButtonUp += (_, e) => owner.OnLayerMouseUp(e);
        }

        protected override void OnRender(DrawingContext dc) => _owner.RenderSelection(dc);

        /// <summary>整块区域可命中（否则 WPF 对无背景的 FrameworkElement 不派发鼠标事件）</summary>
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
            => new PointHitTestResult(this, hitTestParameters.HitPoint);
    }

    // ---------------- 底部操作条 ----------------

    private Border CreateBar() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 6, 10, 6),
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 8, 0, 0),
        Child = new StackPanel { Orientation = Orientation.Horizontal },
    };

    /// <summary>替换操作条内容（条本身只有一个，避免旧状态条残留）</summary>
    private void SetBar(params UIElement[] items)
    {
        if (_bar == null) return;
        var panel = (StackPanel)_bar.Child;
        panel.Children.Clear();
        foreach (var it in items) panel.Children.Add(it);

        if (!_owner.OverlayBarHost.Children.Contains(_bar))
        {
            Grid.SetRow(_bar, 1);
            _owner.OverlayBarHost.Children.Add(_bar);
        }
    }

    private static TextBlock BuildText(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private static Button BarButton(string content, Action onClick)
    {
        var btn = new Button
        {
            Content = content,
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            Background = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x3D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
