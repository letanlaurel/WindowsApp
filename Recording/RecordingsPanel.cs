using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SnipPin.Core.Configuration;
using SnipPin.Core.Services;
using SnipPin.History;

namespace SnipPin.Recording;

internal sealed class RecordingsPanel : Grid
{
	private sealed class CardParts
	{
		public required Border Bg;

		public required Border Badge;
	}

	private readonly RecordingHistoryService _history;

	private readonly RecordingService _recorder;

	private readonly ConfigService _config;

	private TextBlock _countText;

	private Button _archiveBtn;

	private WrapPanel _itemsPanel;

	private StackPanel _emptyHint;

	private TextBlock _emptySubText;

	private Button _recordBtn;

	private TextBlock _kbdRecord;

	private DatePicker _searchStart;

	private DatePicker _searchEnd;

	private TextBlock _searchInfo;

	private Button _clearSearchBtn;

	private bool _searching;

	private DateTime _searchRangeFrom;

	private DateTime _searchRangeTo;

	private bool _multiSelect;

	private readonly HashSet<string> _selectedIds = new HashSet<string>();

	private readonly Dictionary<string, Grid> _cards = new Dictionary<string, Grid>();

	private Button _multiSelectBtn;

	private Button _deleteSelectedBtn;

	private TextBlock _selectedInfo;

	private static readonly Brush RecordRed = UiKit.Frozen(229, 72, 77);

	private static readonly Brush Accent = UiKit.Frozen(124, 106, 240);

	private static readonly Brush SelectedCardBg = UiKit.Frozen(251, 250, byte.MaxValue);

	private static readonly Brush TextPrimary = UiKit.Frozen(58, 65, 80);

	private static readonly Brush TextSecondary = UiKit.Frozen(138, 145, 166);

	private static readonly Brush IconStroke = UiKit.Frozen(74, 82, 101);

	private static readonly Brush NormalBorder = UiKit.Frozen(232, 235, 243);

	private static readonly Brush HoverBorder = UiKit.Frozen(201, 193, 246);

	public event Action? RecordRequested;

	public event Action? RecordingSettingsSaved;

	public RecordingsPanel(RecordingHistoryService history, RecordingService recorder, ConfigService config)
	{
		_history = history;
		_recorder = recorder;
		_config = config;
		base.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		base.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		base.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		UIElement element = BuildToolbar();
		Grid.SetRow(element, 0);
		base.Children.Add(element);
		UIElement element2 = BuildSearchBar();
		Grid.SetRow(element2, 1);
		base.Children.Add(element2);
		Grid grid = new Grid();
		_itemsPanel = new WrapPanel
		{
			Margin = new Thickness(22.0, 18.0, 14.0, 14.0),
			HorizontalAlignment = HorizontalAlignment.Left
		};
		_emptyHint = BuildEmptyHint();
		grid.Children.Add(_itemsPanel);
		grid.Children.Add(_emptyHint);
		ScrollViewer element3 = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			PanningMode = PanningMode.VerticalOnly,
			Content = grid
		};
		Grid.SetRow(element3, 2);
		base.Children.Add(element3);
		_recorder.Started += delegate
		{
			UpdateRecordButton();
		};
		_recorder.Stopped += delegate
		{
			UpdateRecordButton();
		};
		base.Loaded += delegate
		{
			Window window = Window.GetWindow(this);
			if (window != null)
			{
				window.PreviewKeyDown += OnHostPreviewKeyDown;
			}
		};
		_history.Changed += Refresh;
		Refresh();
	}

	private UIElement BuildToolbar()
	{
		Grid grid = new Grid();
		Border element = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(224, 245, 247, 252)),
			BorderBrush = UiKit.Frozen(230, 233, 242),
			BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
			Effect = new DropShadowEffect
			{
				BlurRadius = 14.0,
				ShadowDepth = 0.0,
				Opacity = 0.045,
				Color = Color.FromRgb(100, 110, 143),
				RenderingBias = RenderingBias.Performance
			}
		};
		grid.Children.Add(element);
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(20.0, 10.0, 18.0, 12.0),
			LastChildFill = false
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		_countText = new TextBlock
		{
			FontSize = 16.0,
			FontWeight = FontWeights.SemiBold,
			FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
			Foreground = TextPrimary,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(2.0, 0.0, 16.0, 0.0)
		};
		stackPanel.Children.Add(_countText);
		(Border Host, TextBlock Label) tuple = UiKit.KbdBadge(primary: true);
		Border item = tuple.Host;
		TextBlock item2 = tuple.Label;
		_kbdRecord = item2;
		_recordBtn = new Button
		{
			Height = 34.0,
			Padding = new Thickness(20.0, 0.0, 18.0, 0.0),
			Background = RecordRed,
			Cursor = Cursors.Hand,
			ToolTip = "选择窗口或全屏开始录制",
			Template = UiKit.ButtonTemplate(10, "#F2555A", "#C93A3F"),
			Content = UiKit.ActionContent("M4.5 7.5A2.5 2.5 0 0 1 7 5h7a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5H7a2.5 2.5 0 0 1-2.5-2.5z M16.5 10.2l3-2v7.6l-3-2", Brushes.White, "录制", Brushes.White, item)
		};
		_recordBtn.Click += delegate
		{
			if (_recorder.IsRecording)
			{
				_recorder.Stop();
			}
			else
			{
				this.RecordRequested?.Invoke();
			}
		};
		stackPanel.Children.Add(_recordBtn);
		DockPanel.SetDock(stackPanel, Dock.Left);
		dockPanel.Children.Add(stackPanel);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		Button button = new Button
		{
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			Padding = new Thickness(19.0, 0.0, 19.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = "打开录屏保存目录",
			Template = UiKit.ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
			Content = UiKit.ActionContent("M4.5 6.5A2 2 0 0 1 6.5 4.5h3.2l2 2.3h6a2 2 0 0 1 2 2v8.2a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2z", IconStroke, "打开目录", TextPrimary)
		};
		button.Click += delegate
		{
			_history.OpenFolder();
		};
		Button button2 = new Button
		{
			Height = 32.0,
			Padding = new Thickness(18.0, 0.0, 15.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = "清空全部录屏（视频文件一并删除）",
			Template = UiKit.ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
			Content = UiKit.ActionContent("M4.5 7h15 M9.5 7V5.5A1.5 1.5 0 0 1 11 4h2a1.5 1.5 0 0 1 1.5 1.5V7 M7.5 7l.7 11.1A2 2 0 0 0 10.2 20h3.6a2 2 0 0 0 2-1.9L16.5 7 M10 10.5v6 M14 10.5v6", IconStroke, "清空列表", TextPrimary)
		};
		button2.Click += delegate
		{
			ClearAll();
		};
		_multiSelectBtn = new Button
		{
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			Padding = new Thickness(19.0, 0.0, 19.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = "进入多选模式，批量删除（Esc 退出）",
			Template = UiKit.ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
			Content = UiKit.ActionContent("M7.5 4.5h9A3 3 0 0 1 19.5 7.5v9a3 3 0 0 1-3 3h-9a3 3 0 0 1-3-3v-9a3 3 0 0 1 3-3z M8.7 12.2l2.3 2.3 4.5-5", IconStroke, "多选", TextPrimary)
		};
		_multiSelectBtn.Click += delegate
		{
			SetMultiSelect(!_multiSelect);
		};
		_selectedInfo = new TextBlock
		{
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = Accent,
			FontWeight = FontWeights.SemiBold,
			FontSize = 12.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			Visibility = Visibility.Collapsed
		};
		_deleteSelectedBtn = new Button
		{
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			Padding = new Thickness(19.0, 0.0, 19.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = "删除所选的录屏",
			Template = UiKit.ButtonTemplate(9, "#FDEBEE", "#F9DDE2"),
			Content = UiKit.ActionContent("M4.5 7h15 M9.5 7V5.5A1.5 1.5 0 0 1 11 4h2a1.5 1.5 0 0 1 1.5 1.5V7 M7.5 7l.7 11.1A2 2 0 0 0 10.2 20h3.6a2 2 0 0 0 2-1.9L16.5 7 M10 10.5v6 M14 10.5v6", UiKit.Frozen(214, 69, 98), "删除所选", UiKit.Frozen(214, 69, 98)),
			Visibility = Visibility.Collapsed
		};
		_deleteSelectedBtn.Click += delegate
		{
			DeleteSelected();
		};
		Button button3 = new Button
		{
			Height = 32.0,
			Padding = new Thickness(19.0, 0.0, 19.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = "录屏设置（快捷键、画质、保存位置）",
			Template = UiKit.ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
			Content = UiKit.ActionContent("M5 4.5v5 M5 13.5v6 M12 4.5v9 M12 17.5v2 M19 4.5v2 M19 10.5v9 M3 9.5h4 M10 13.5h4 M17 6.5h4", IconStroke, "设置", TextPrimary)
		};
		button3.Click += delegate
		{
			OpenRecordingSettings();
		};
		stackPanel2.Children.Add(_archiveBtn = new Button
		{
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			Padding = new Thickness(19.0, 0.0, 19.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = "超过上限的录屏已按月归档到压缩包，点击打开所在文件夹",
			Template = UiKit.ButtonTemplate(9, "#EFEDFB", "#E4E1F7"),
			Visibility = Visibility.Collapsed
		});
		_archiveBtn.Click += delegate
		{
			_history.OpenFolder();
		};
		stackPanel2.Children.Add(_multiSelectBtn);
		stackPanel2.Children.Add(_selectedInfo);
		stackPanel2.Children.Add(_deleteSelectedBtn);
		stackPanel2.Children.Add(button);
		stackPanel2.Children.Add(button3);
		stackPanel2.Children.Add(button2);
		DockPanel.SetDock(stackPanel2, Dock.Right);
		dockPanel.Children.Add(stackPanel2);
		grid.Children.Add(dockPanel);
		return grid;
	}

	private UIElement BuildSearchBar()
	{
		Border obj = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(184, 245, 247, 252)),
			BorderBrush = UiKit.Frozen(230, 233, 242),
			BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
			Padding = new Thickness(22.0, 8.0, 22.0, 8.0)
		};
		DockPanel dockPanel = new DockPanel
		{
			LastChildFill = false
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "日期范围",
			FontSize = 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = TextPrimary,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		});
		_searchStart = SearchDateBox();
		stackPanel.Children.Add(_searchStart);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "至",
			FontSize = 12.0,
			Foreground = TextSecondary,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8.0, 0.0, 8.0, 0.0)
		});
		_searchEnd = SearchDateBox();
		stackPanel.Children.Add(_searchEnd);
		Button button = new Button
		{
			Height = 30.0,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = Accent,
			Cursor = Cursors.Hand,
			ToolTip = "按所选日期范围筛选",
			Template = UiKit.ButtonTemplate(8, "#8B7CF8", "#6B59E4"),
			Content = new TextBlock
			{
				Text = "搜索",
				FontSize = 12.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brushes.White,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		button.Click += delegate
		{
			ApplySearch();
		};
		stackPanel.Children.Add(button);
		_clearSearchBtn = new Button
		{
			Height = 30.0,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = "清除筛选，显示全部",
			Visibility = Visibility.Collapsed,
			Template = UiKit.ButtonTemplate(8, "#EFEDFB", "#E4E1F7"),
			Content = new TextBlock
			{
				Text = "清除",
				FontSize = 12.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = TextPrimary,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		_clearSearchBtn.Click += delegate
		{
			ClearSearch();
		};
		stackPanel.Children.Add(_clearSearchBtn);
		DockPanel.SetDock(stackPanel, Dock.Left);
		dockPanel.Children.Add(stackPanel);
		_searchInfo = new TextBlock
		{
			FontSize = 12.0,
			Foreground = Accent,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed
		};
		DockPanel.SetDock(_searchInfo, Dock.Right);
		dockPanel.Children.Add(_searchInfo);
		obj.Child = dockPanel;
		return obj;
	}

	private DatePicker SearchDateBox()
	{
		DatePicker datePicker = new DatePicker();
		datePicker.Width = 130.0;
		datePicker.Height = 30.0;
		datePicker.VerticalContentAlignment = VerticalAlignment.Center;
		datePicker.FontSize = 12.5;
		datePicker.KeyDown += delegate(object _, KeyEventArgs e)
		{
			if (e.Key == Key.Return)
			{
				ApplySearch();
				e.Handled = true;
			}
		};
		return datePicker;
	}

	private void ApplySearch()
	{
		DateTime? dateTime = _searchStart?.SelectedDate;
		DateTime? dateTime2 = _searchEnd?.SelectedDate;
		if (!dateTime.HasValue && !dateTime2.HasValue)
		{
			ClearSearch();
			return;
		}
		DateTime dateTime3 = (dateTime ?? DateTime.MinValue).Date;
		DateTime dateTime4 = (dateTime2 ?? DateTime.Today).Date;
		if (dateTime3 > dateTime4)
		{
			DateTime dateTime5 = dateTime4;
			dateTime4 = dateTime3;
			dateTime3 = dateTime5;
		}
		_searching = true;
		_searchRangeFrom = dateTime3;
		_searchRangeTo = dateTime4;
		_history.EnsureArchivesExtracted(dateTime3, dateTime4);
		Refresh();
	}

	private void ClearSearch()
	{
		_searching = false;
		_searchRangeFrom = default(DateTime);
		_searchRangeTo = default(DateTime);
		if (_searchStart != null)
		{
			_searchStart.SelectedDate = null;
		}
		if (_searchEnd != null)
		{
			_searchEnd.SelectedDate = null;
		}
		_history.DiscardExtracted();
		Refresh();
	}

	private IEnumerable<RecordingEntry> FilteredEntries()
	{
		IReadOnlyList<RecordingEntry> entries = _history.Entries;
		if (!_searching)
		{
			return entries;
		}
		DateTime from = _searchRangeFrom.Date;
		DateTime to = _searchRangeTo.Date.AddDays(1.0);
		return entries.Where((RecordingEntry x) => x.CreatedAt >= from && x.CreatedAt < to);
	}

	private void SetMultiSelect(bool on)
	{
		_multiSelect = on;
		_multiSelectBtn.Background = (on ? Accent : Brushes.Transparent);
		_multiSelectBtn.Template = (on ? UiKit.ButtonTemplate(9, "#6B59E4", "#5F4FD6") : UiKit.ButtonTemplate(9, "#EFEDFB", "#E4E1F7"));
		_multiSelectBtn.Content = (on ? UiKit.ActionContent("M7.5 4.5h9A3 3 0 0 1 19.5 7.5v9a3 3 0 0 1-3 3h-9a3 3 0 0 1-3-3v-9a3 3 0 0 1 3-3z M8.7 12.2l2.3 2.3 4.5-5", Brushes.White, "退出多选", Brushes.White) : UiKit.ActionContent("M7.5 4.5h9A3 3 0 0 1 19.5 7.5v9a3 3 0 0 1-3 3h-9a3 3 0 0 1-3-3v-9a3 3 0 0 1 3-3z M8.7 12.2l2.3 2.3 4.5-5", IconStroke, "多选", TextPrimary));
		_deleteSelectedBtn.Visibility = ((!on) ? Visibility.Collapsed : Visibility.Visible);
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
		{
			_selectedIds.Add(id);
		}
		RefreshCardSelection();
		UpdateSelectedInfo();
	}

	private void RefreshCardSelection()
	{
		foreach (KeyValuePair<string, Grid> card in _cards)
		{
			card.Deconstruct(out var key, out var value);
			string item = key;
			Grid grid = value;
			bool flag = _selectedIds.Contains(item);
			if (grid.Tag is CardParts cardParts)
			{
				cardParts.Bg.BorderBrush = (flag ? Accent : NormalBorder);
				cardParts.Bg.BorderThickness = new Thickness(flag ? 1.5 : 1.0);
				cardParts.Bg.Background = (flag ? SelectedCardBg : Brushes.White);
				cardParts.Badge.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
			}
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
		_selectedInfo.Visibility = ((!_multiSelect) ? Visibility.Collapsed : Visibility.Visible);
		_deleteSelectedBtn.IsEnabled = _selectedIds.Count > 0;
	}

	private void DeleteSelected()
	{
		if (_selectedIds.Count == 0 || MessageBox.Show(Window.GetWindow(this), $"确定删除所选的 {_selectedIds.Count} 条录屏？\n（视频文件将一并删除）", "删除所选", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		foreach (RecordingEntry item in _history.Entries.Where((RecordingEntry e) => _selectedIds.Contains(e.Id)).ToList())
		{
			_history.Delete(item);
		}
	}

	private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && _multiSelect && base.Visibility == Visibility.Visible)
		{
			SetMultiSelect(on: false);
			e.Handled = true;
		}
	}

	private void OpenRecordingSettings()
	{
		RecordingSettingsWindow recordingSettingsWindow = new RecordingSettingsWindow(_config);
		recordingSettingsWindow.Owner = Window.GetWindow(this);
		recordingSettingsWindow.SettingsSaved += delegate
		{
			RefreshHotkeyBadge();
			this.RecordingSettingsSaved?.Invoke();
		};
		recordingSettingsWindow.Show();
	}

	private void UpdateRecordButton()
	{
		(Border Host, TextBlock Label) tuple = UiKit.KbdBadge(primary: true);
		Border item = tuple.Host;
		TextBlock item2 = tuple.Label;
		_kbdRecord = item2;
		RefreshHotkeyBadge();
		if (_recorder.IsRecording)
		{
			_recordBtn.Content = UiKit.ActionContent("M7 7h10v10H7z", Brushes.White, "停止录制", Brushes.White, item);
			_recordBtn.ToolTip = "正在录制「" + _recorder.TargetTitle + "」，点击停止并保存";
		}
		else
		{
			_recordBtn.Content = UiKit.ActionContent("M4.5 7.5A2.5 2.5 0 0 1 7 5h7a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5H7a2.5 2.5 0 0 1-2.5-2.5z M16.5 10.2l3-2v7.6l-3-2", Brushes.White, "录制", Brushes.White, item);
			_recordBtn.ToolTip = "选择窗口或全屏开始录制（无音频）";
		}
	}

	private void RefreshHotkeyBadge()
	{
		string recordToggle = _config.Config.Hotkeys.RecordToggle;
		_kbdRecord.Text = recordToggle;
		_emptySubText.Text = (string.IsNullOrEmpty(recordToggle) ? "点击上方「录制」按钮，选择要录制的窗口" : ("点击上方「录制」按钮或按 " + recordToggle + "，选择要录制的窗口"));
	}

	private StackPanel BuildEmptyHint()
	{
		StackPanel obj = new StackPanel
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed,
			Children = 
			{
				(UIElement)UiKit.LineIcon("M4.5 7.5A2.5 2.5 0 0 1 7 5h7a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5H7a2.5 2.5 0 0 1-2.5-2.5z M16.5 10.2l3-2v7.6l-3-2", 56.0, UiKit.Frozen(199, 205, 227), 1.4),
				(UIElement)new TextBlock
				{
					Text = "还没有录屏记录",
					FontSize = 15.0,
					FontWeight = FontWeights.SemiBold,
					Foreground = UiKit.Frozen(90, 97, 114),
					HorizontalAlignment = HorizontalAlignment.Center,
					Margin = new Thickness(0.0, 14.0, 0.0, 4.0)
				}
			}
		};
		_emptySubText = new TextBlock
		{
			FontSize = 12.0,
			Foreground = TextSecondary,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		obj.Children.Add(_emptySubText);
		return obj;
	}

	public void Refresh()
	{
		if (!base.Dispatcher.CheckAccess())
		{
			base.Dispatcher.Invoke(Refresh);
			return;
		}
		_cards.Clear();
		_itemsPanel.Children.Clear();
		_selectedIds.Clear();
		List<RecordingEntry> list = FilteredEntries().ToList();
		_countText.Inlines.Clear();
		_countText.Inlines.Add(new Run("录屏列表"));
		if (list.Count > 0)
		{
			_countText.Inlines.Add(new Run($" ({list.Count})")
			{
				Foreground = RecordRed,
				FontSize = 14.0
			});
		}
		if (_history.ArchivedCount > 0)
		{
			_archiveBtn.Content = UiKit.ActionContent("M4.5 4.5h15V8h-15z M6 8h12v9.5A2.5 2.5 0 0 1 15.5 20h-7A2.5 2.5 0 0 1 6 17.5z M10 12h4", IconStroke, $"已归档 {_history.ArchivedCount} 条", TextSecondary);
			_archiveBtn.Visibility = Visibility.Visible;
		}
		else
		{
			_archiveBtn.Visibility = Visibility.Collapsed;
		}
		_emptyHint.Visibility = ((list.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		foreach (RecordingEntry item in list)
		{
			Grid grid = BuildCard(item);
			_cards[item.Id] = grid;
			_itemsPanel.Children.Add(grid);
		}
		if (_searching)
		{
			_searchInfo.Text = $"找到 {list.Count} 条 · {_searchRangeFrom:yyyy-MM-dd} ~ {_searchRangeTo:yyyy-MM-dd}";
			_searchInfo.Visibility = Visibility.Visible;
			_clearSearchBtn.Visibility = Visibility.Visible;
		}
		else
		{
			_searchInfo.Visibility = Visibility.Collapsed;
			_clearSearchBtn.Visibility = Visibility.Collapsed;
		}
		UpdateSelectedInfo();
		UpdateRecordButton();
	}

	private Grid BuildCard(RecordingEntry entry)
	{
		RecordingEntry entry2 = entry;
		Image child = new Image
		{
			Source = _history.LoadThumb(entry2, 432),
			Stretch = Stretch.UniformToFill,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		double width = 196.0;
		RectangleGeometry rectangleGeometry = new RectangleGeometry(new Rect(0.0, 0.0, width, 124.0), 10.0, 10.0);
		rectangleGeometry.Freeze();
		Border element = new Border
		{
			Height = 124.0,
			CornerRadius = new CornerRadius(10.0),
			Background = UiKit.Frozen(243, 245, 250),
			Clip = rectangleGeometry,
			Child = child
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		stackPanel.Children.Add(new Viewbox
		{
			Width = 8.0,
			Height = 8.0,
			Stretch = Stretch.Uniform,
			VerticalAlignment = VerticalAlignment.Center,
			Child = new System.Windows.Shapes.Path
			{
				Data = Geometry.Parse("M8 5.2v13.6L18.8 12z"),
				Fill = Brushes.White
			}
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = FormatDuration(entry2.DurationSeconds),
			FontSize = 10.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(3.0, 0.0, 0.0, 0.0)
		});
		Border element2 = new Border
		{
			CornerRadius = new CornerRadius(7.0),
			Background = new SolidColorBrush(Color.FromArgb(184, 31, 36, 48)),
			Padding = new Thickness(7.0, 3.0, 8.0, 3.0),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(6.0, 0.0, 0.0, 6.0),
			Child = stackPanel
		};
		Border border = new Border
		{
			Width = 22.0,
			Height = 22.0,
			CornerRadius = new CornerRadius(11.0),
			Background = Accent,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(6.0, 6.0, 0.0, 0.0),
			Visibility = Visibility.Collapsed,
			Child = new Viewbox
			{
				Width = 12.0,
				Height = 12.0,
				Stretch = Stretch.Uniform,
				Child = new System.Windows.Shapes.Path
				{
					Data = Geometry.Parse("M6 12.5l4 4 8-8"),
					Stroke = Brushes.White,
					StrokeThickness = 2.6,
					StrokeStartLineCap = PenLineCap.Round,
					StrokeEndLineCap = PenLineCap.Round,
					StrokeLineJoin = PenLineJoin.Round
				}
			}
		};
		StackPanel overlay = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 6.0, 6.0, 0.0),
			Visibility = Visibility.Collapsed
		};
		Button button = UiKit.MiniIconButton("M8.5 5.5l10 6.5-10 6.5z", "播放录屏");
		button.Click += delegate
		{
			PlayEntry(entry2);
		};
		Button button2 = UiKit.MiniIconButton("M4.5 6.5A2 2 0 0 1 6.5 4.5h3.2l2 2.3h6a2 2 0 0 1 2 2v8.2a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2z", "打开所在文件夹");
		button2.Click += delegate
		{
			OpenInFolder(entry2);
		};
		Button button3 = UiKit.MiniIconButton("M4.5 7h15 M9.5 7V5.5A1.5 1.5 0 0 1 11 4h2a1.5 1.5 0 0 1 1.5 1.5V7 M7.5 7l.7 11.1A2 2 0 0 0 10.2 20h3.6a2 2 0 0 0 2-1.9L16.5 7 M10 10.5v6 M14 10.5v6", "删除这条录屏");
		button3.Click += delegate
		{
			_history.Delete(entry2);
		};
		overlay.Children.Add(button);
		overlay.Children.Add(button2);
		overlay.Children.Add(button3);
		Grid grid = new Grid();
		grid.Children.Add(element);
		grid.Children.Add(overlay);
		grid.Children.Add(element2);
		grid.Children.Add(border);
		StackPanel stackPanel2 = new StackPanel
		{
			Margin = new Thickness(2.0, 9.0, 2.0, 0.0)
		};
		stackPanel2.Children.Add(new TextBlock
		{
			Text = ((entry2.WindowTitle.Length > 0) ? entry2.WindowTitle : "(无标题)"),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = UiKit.Frozen(74, 82, 101),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		stackPanel2.Children.Add(new TextBlock
		{
			Text = $"{entry2.CreatedAt:MM-dd HH:mm} · {FormatSize(entry2)}",
			FontSize = 11.0,
			Foreground = TextSecondary,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		});
		StackPanel stackPanel3 = new StackPanel
		{
			Margin = new Thickness(10.0, 10.0, 10.0, 11.0)
		};
		stackPanel3.Children.Add(grid);
		stackPanel3.Children.Add(stackPanel2);
		DropShadowEffect shadow = new DropShadowEffect
		{
			BlurRadius = 16.0,
			ShadowDepth = 0.0,
			Opacity = 0.09,
			Color = Color.FromRgb(90, 100, 140),
			RenderingBias = RenderingBias.Performance
		};
		Border bg = new Border
		{
			Background = Brushes.White,
			BorderBrush = NormalBorder,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(14.0),
			Effect = shadow
		};
		Grid card = new Grid
		{
			Width = 216.0,
			Margin = new Thickness(0.0, 0.0, 18.0, 16.0),
			Cursor = Cursors.Hand,
			RenderTransform = new TranslateTransform(),
			ToolTip = $"{entry2.WindowTitle}\n{entry2.CreatedAt:yyyy-MM-dd HH:mm:ss} · {entry2.Width}×{entry2.Height} · 时长 {FormatDuration(entry2.DurationSeconds)}",
			Tag = new CardParts
			{
				Bg = bg,
				Badge = border
			}
		};
		card.Children.Add(bg);
		card.Children.Add(stackPanel3);
		card.MouseEnter += delegate
		{
			overlay.Visibility = Visibility.Visible;
			if (!_selectedIds.Contains(entry2.Id))
			{
				bg.BorderBrush = HoverBorder;
			}
			UiKit.AnimateDouble((TranslateTransform)card.RenderTransform, TranslateTransform.YProperty, -3.0);
			UiKit.AnimateDouble(shadow, DropShadowEffect.OpacityProperty, 0.17);
		};
		card.MouseLeave += delegate
		{
			overlay.Visibility = Visibility.Collapsed;
			if (!_selectedIds.Contains(entry2.Id))
			{
				bg.BorderBrush = NormalBorder;
			}
			UiKit.AnimateDouble((TranslateTransform)card.RenderTransform, TranslateTransform.YProperty, 0.0);
			UiKit.AnimateDouble(shadow, DropShadowEffect.OpacityProperty, 0.09);
		};
		card.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			if (_multiSelect)
			{
				e.Handled = true;
				ToggleSelect(entry2.Id);
			}
			else if (e.ClickCount == 2)
			{
				PlayEntry(entry2);
			}
		};
		return card;
	}

	private void PlayEntry(RecordingEntry entry)
	{
		string text = _history.FilePath(entry);
		if (!File.Exists(text))
		{
			MessageBox.Show(Window.GetWindow(this), "视频文件已不存在。", "播放录屏", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = text,
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Program.LogError("播放录屏", ex);
			MessageBox.Show(Window.GetWindow(this), "播放失败：\n" + ex.Message, "播放录屏", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void OpenInFolder(RecordingEntry entry)
	{
		try
		{
			string text = _history.FilePath(entry);
			Process.Start(new ProcessStartInfo
			{
				FileName = "explorer.exe",
				Arguments = "/select,\"" + text + "\"",
				UseShellExecute = true
			});
		}
		catch (Exception exception)
		{
			Program.LogError("打开录屏目录", exception);
		}
	}

	private void ClearAll()
	{
		if (_history.Entries.Count != 0 && MessageBox.Show(Window.GetWindow(this), $"确定清空全部 {_history.Entries.Count} 条录屏？\n（视频文件将一并删除）", "清空录屏", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			_history.Clear();
		}
	}

	internal static string FormatDuration(int seconds)
	{
		TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Max(0, seconds));
		if (timeSpan.TotalHours >= 1.0)
		{
			return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
		}
		return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
	}

	private static string FormatSize(RecordingEntry entry)
	{
		long fileSizeBytes = entry.FileSizeBytes;
		if (fileSizeBytes <= 0)
		{
			return "";
		}
		if (fileSizeBytes < 1073741824)
		{
			if (fileSizeBytes < 1048576)
			{
				return $"{(double)fileSizeBytes / 1024.0:0.#} KB";
			}
			return $"{(double)fileSizeBytes / 1048576.0:0.#} MB";
		}
		return $"{(double)fileSizeBytes / 1073741824.0:0.##} GB";
	}
}
