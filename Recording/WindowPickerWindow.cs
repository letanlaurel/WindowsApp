using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using SnipPin.Core.Native;
using SnipPin.History;

namespace SnipPin.Recording;

public sealed class WindowPickerWindow : Window
{
	public sealed record Candidate(bool IsScreen, nint Hwnd, string Title, string Process, int Width, int Height, ImageSource? Icon, Rect ScreenBounds);

	private sealed class CardParts
	{
		public Border Bg;

		public Border Badge;

		public Image Preview;
	}

	private readonly List<Candidate> _all = new List<Candidate>();

	private List<Candidate> _filtered = new List<Candidate>();

	private readonly Dictionary<Candidate, CardParts> _cards = new Dictionary<Candidate, CardParts>();

	private Candidate? _selected;

	private TextBox _filterBox;

	private WrapPanel _itemsPanel;

	private Button _okBtn;

	private TextBlock _emptyHint;

	private CancellationTokenSource? _previewCts;

	private const int PreviewWidth = 432;

	private static readonly Brush TextPrimary = UiKit.Frozen(58, 65, 80);

	private static readonly Brush TextSecondary = UiKit.Frozen(138, 145, 166);

	private static readonly Brush IconStroke = UiKit.Frozen(74, 82, 101);

	private static readonly Brush Accent = UiKit.Frozen(124, 106, 240);

	private static readonly Brush RecordRed = UiKit.Frozen(229, 72, 77);

	private static readonly Brush NormalBorder = UiKit.Frozen(232, 235, 243);

	private static readonly Brush HoverBorder = UiKit.Frozen(201, 193, 246);

	private static readonly Brush ThumbBg = UiKit.Frozen(243, 245, 250);

	public event Action<Candidate>? WindowPicked;

	public WindowPickerWindow()
	{
		base.Title = "选择要录制的窗口";
		base.Icon = Program.LoadAppIcon();
		base.Width = 990.0;
		base.Height = 640.0;
		base.MinWidth = 560.0;
		base.MinHeight = 420.0;
		base.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		base.ShowInTaskbar = false;
		base.ResizeMode = ResizeMode.CanResize;
		base.WindowStyle = WindowStyle.None;
		WindowChrome.SetWindowChrome(this, new WindowChrome
		{
			CaptionHeight = 44.0,
			ResizeBorderThickness = new Thickness(6.0),
			GlassFrameThickness = new Thickness(0.0),
			UseAeroCaptionButtons = false
		});
		LinearGradientBrush linearGradientBrush = new LinearGradientBrush
		{
			StartPoint = new Point(0.0, 0.0),
			EndPoint = new Point(1.0, 1.0)
		};
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromRgb(250, 251, 254), 0.0));
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromRgb(236, 239, 247), 1.0));
		linearGradientBrush.Freeze();
		base.Background = linearGradientBrush;
		TextElement.SetFontFamily(this, new FontFamily("Segoe UI Variable Text, Segoe UI"));
		TextElement.SetForeground(this, TextPrimary);
		base.Content = BuildContent();
		base.Loaded += delegate
		{
			LoadWindows();
			StartPreviewLoop();
		};
	}

	protected override void OnClosed(EventArgs e)
	{
		_previewCts?.Cancel();
		base.OnClosed(e);
	}

	private UIElement BuildContent()
	{
		Grid obj = new Grid
		{
			RowDefinitions = 
			{
				new RowDefinition
				{
					Height = new GridLength(44.0)
				},
				new RowDefinition
				{
					Height = GridLength.Auto
				},
				new RowDefinition
				{
					Height = new GridLength(1.0, GridUnitType.Star)
				},
				new RowDefinition
				{
					Height = GridLength.Auto
				}
			}
		};
		UIElement element = BuildTitleBar();
		Grid.SetRow(element, 0);
		obj.Children.Add(element);
		UIElement element2 = BuildSearchBox();
		Grid.SetRow(element2, 1);
		obj.Children.Add(element2);
		_itemsPanel = new WrapPanel
		{
			Margin = new Thickness(22.0, 14.0, 14.0, 6.0),
			HorizontalAlignment = HorizontalAlignment.Left
		};
		_emptyHint = new TextBlock
		{
			Text = "没有可录制的窗口",
			FontSize = 13.0,
			Foreground = TextSecondary,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed
		};
		Grid content = new Grid
		{
			Children = 
			{
				(UIElement)_itemsPanel,
				(UIElement)_emptyHint
			}
		};
		ScrollViewer element3 = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			PanningMode = PanningMode.VerticalOnly,
			Content = content
		};
		Grid.SetRow(element3, 2);
		obj.Children.Add(element3);
		UIElement element4 = BuildBottomBar();
		Grid.SetRow(element4, 3);
		obj.Children.Add(element4);
		base.PreviewKeyDown += delegate(object _, KeyEventArgs e)
		{
			if (e.Key == Key.Return)
			{
				ConfirmSelected();
				e.Handled = true;
			}
		};
		return obj;
	}

	private UIElement BuildTitleBar()
	{
		DockPanel obj = new DockPanel
		{
			Margin = new Thickness(16.0, 0.0, 6.0, 0.0),
			LastChildFill = false
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		ImageSource imageSource = Program.LoadAppIcon();
		if (imageSource != null)
		{
			stackPanel.Children.Add(new Image
			{
				Source = imageSource,
				Width = 18.0,
				Height = 18.0,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0.0, 0.0, 9.0, 0.0)
			});
		}
		stackPanel.Children.Add(new TextBlock
		{
			Text = "选择要录制的窗口或全屏",
			FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
			FontSize = 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = TextPrimary,
			VerticalAlignment = VerticalAlignment.Center
		});
		DockPanel.SetDock(stackPanel, Dock.Left);
		obj.Children.Add(stackPanel);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		Viewbox viewbox = UiKit.LineIcon("M6.5 6.5l11 11 M17.5 6.5l-11 11", 10.0, IconStroke, 1.2);
		Path closePath = UiKit.IconPath(viewbox);
		Button button = new Button
		{
			Width = 42.0,
			Height = 30.0,
			Padding = new Thickness(0.0),
			Background = Brushes.Transparent,
			ToolTip = "关闭",
			Content = viewbox,
			Template = UiKit.ButtonTemplate(7, "#E81123", "#C50F1F")
		};
		button.MouseEnter += delegate
		{
			closePath.Stroke = Brushes.White;
		};
		button.MouseLeave += delegate
		{
			closePath.Stroke = IconStroke;
		};
		button.Click += delegate
		{
			Close();
		};
		WindowChrome.SetIsHitTestVisibleInChrome(button, hitTestVisible: true);
		stackPanel2.Children.Add(button);
		DockPanel.SetDock(stackPanel2, Dock.Right);
		obj.Children.Add(stackPanel2);
		return obj;
	}

	private UIElement BuildSearchBox()
	{
		Border obj = new Border
		{
			Margin = new Thickness(22.0, 10.0, 22.0, 0.0),
			Height = 34.0,
			CornerRadius = new CornerRadius(9.0),
			Background = Brushes.White,
			BorderBrush = UiKit.Frozen(230, 233, 242),
			BorderThickness = new Thickness(1.0)
		};
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(10.0, 0.0, 10.0, 0.0),
			LastChildFill = true
		};
		Viewbox viewbox = UiKit.LineIcon("M10.5 4.5a6 6 0 1 0 0 12 6 6 0 0 0 0-12z M15 15l4.5 4.5", 14.0, TextSecondary);
		viewbox.VerticalAlignment = VerticalAlignment.Center;
		DockPanel.SetDock(viewbox, Dock.Left);
		dockPanel.Children.Add(viewbox);
		TextBlock placeholder = new TextBlock
		{
			Text = "搜索窗口名称或进程",
			FontSize = 12.5,
			Foreground = UiKit.Frozen(168, 174, 192),
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};
		_filterBox = new TextBox
		{
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			FontSize = 12.5,
			Foreground = TextPrimary,
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(0.0)
		};
		_filterBox.TextChanged += delegate
		{
			placeholder.Visibility = ((_filterBox.Text.Length != 0) ? Visibility.Collapsed : Visibility.Visible);
			ApplyFilter();
		};
		Grid element = new Grid
		{
			Children = 
			{
				(UIElement)_filterBox,
				(UIElement)placeholder
			}
		};
		dockPanel.Children.Add(element);
		obj.Child = dockPanel;
		return obj;
	}

	private UIElement BuildBottomBar()
	{
		StackPanel obj = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(22.0, 10.0, 22.0, 16.0)
		};
		Button button = new Button
		{
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			Padding = new Thickness(19.0, 0.0, 19.0, 0.0),
			Background = Brushes.Transparent,
			Cursor = Cursors.Hand,
			Content = new TextBlock
			{
				Text = "取消",
				FontSize = 12.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = TextPrimary
			},
			Template = UiKit.ButtonTemplate(9, "#EFEDFB", "#E4E1F7")
		};
		button.Click += delegate
		{
			Close();
		};
		_okBtn = new Button
		{
			Height = 32.0,
			Padding = new Thickness(20.0, 0.0, 18.0, 0.0),
			Background = RecordRed,
			Cursor = Cursors.Hand,
			ToolTip = "开始录制所选目标",
			IsEnabled = false,
			Template = UiKit.ButtonTemplate(9, "#F2555A", "#C93A3F"),
			Content = UiKit.ActionContent("M4.5 7.5A2.5 2.5 0 0 1 7 5h7a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5H7a2.5 2.5 0 0 1-2.5-2.5z M16.5 10.2l3-2v7.6l-3-2", Brushes.White, "开始录制", Brushes.White)
		};
		_okBtn.Click += delegate
		{
			ConfirmSelected();
		};
		obj.Children.Add(button);
		obj.Children.Add(_okBtn);
		return obj;
	}

	private Grid BuildCard(Candidate c)
	{
		Candidate c2 = c;
		Image image = new Image
		{
			Source = null,
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
			Background = ThumbBg,
			Clip = rectangleGeometry,
			Child = image
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
				Child = new Path
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
		Grid grid = new Grid();
		grid.Children.Add(element);
		grid.Children.Add(border);
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		UIElement uIElement = ((c2.Icon != null) ? ((FrameworkElement)new Image
		{
			Source = c2.Icon,
			Width = 16.0,
			Height = 16.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
		}) : ((FrameworkElement)UiKit.LineIcon(c2.IsScreen ? "M5 7A2.5 2.5 0 0 1 7.5 4.5h9A2.5 2.5 0 0 1 19 7v6.5a2.5 2.5 0 0 1-2.5 2.5h-9A2.5 2.5 0 0 1 5 13.5z M9.5 20h5 M12 16v4" : "M4.5 7.5A2.5 2.5 0 0 1 7 5h7a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5H7a2.5 2.5 0 0 1-2.5-2.5z M16.5 10.2l3-2v7.6l-3-2", 14.0, IconStroke)));
		if (uIElement is FrameworkElement frameworkElement)
		{
			frameworkElement.VerticalAlignment = VerticalAlignment.Center;
			frameworkElement.Margin = new Thickness(0.0, 0.0, 6.0, 0.0);
		}
		stackPanel.Children.Add(uIElement);
		stackPanel.Children.Add(new TextBlock
		{
			Text = c2.Title,
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = UiKit.Frozen(74, 82, 101),
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		});
		string text = ((c2.Process.Length > 0) ? $"{c2.Process} · {c2.Width}×{c2.Height}" : $"{c2.Width}×{c2.Height}");
		StackPanel stackPanel2 = new StackPanel
		{
			Margin = new Thickness(2.0, 8.0, 2.0, 0.0)
		};
		stackPanel2.Children.Add(stackPanel);
		stackPanel2.Children.Add(new TextBlock
		{
			Text = text,
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
		Border bg = new Border
		{
			Background = Brushes.White,
			BorderBrush = NormalBorder,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(14.0),
			Effect = new DropShadowEffect
			{
				BlurRadius = 16.0,
				ShadowDepth = 0.0,
				Opacity = 0.09,
				Color = Color.FromRgb(90, 100, 140),
				RenderingBias = RenderingBias.Performance
			}
		};
		Grid card = new Grid
		{
			Width = 216.0,
			Margin = new Thickness(0.0, 0.0, 18.0, 14.0),
			Cursor = Cursors.Hand,
			RenderTransform = new TranslateTransform(),
			ToolTip = (c2.IsScreen ? $"{c2.Title}\n{c2.Width}×{c2.Height} · 双击开始录制" : (c2.Title + "\n" + text + " · 双击开始录制"))
		};
		card.Children.Add(bg);
		card.Children.Add(stackPanel3);
		card.MouseEnter += delegate
		{
			if (!object.Equals(_selected, c2))
			{
				bg.BorderBrush = HoverBorder;
			}
			UiKit.AnimateDouble((TranslateTransform)card.RenderTransform, TranslateTransform.YProperty, -3.0);
		};
		card.MouseLeave += delegate
		{
			if (!object.Equals(_selected, c2))
			{
				bg.BorderBrush = NormalBorder;
			}
			UiKit.AnimateDouble((TranslateTransform)card.RenderTransform, TranslateTransform.YProperty, 0.0);
		};
		card.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			Select(c2);
			if (e.ClickCount == 2)
			{
				ConfirmSelected();
			}
		};
		_cards[c2] = new CardParts
		{
			Bg = bg,
			Badge = border,
			Preview = image
		};
		return card;
	}

	private void Select(Candidate? c)
	{
		_selected = c;
		foreach (KeyValuePair<Candidate, CardParts> card in _cards)
		{
			card.Deconstruct(out var key, out var value);
			Candidate objA = key;
			CardParts cardParts = value;
			bool flag = object.Equals(objA, c);
			cardParts.Bg.BorderBrush = (flag ? Accent : NormalBorder);
			cardParts.Badge.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		}
		_okBtn.IsEnabled = c != null;
	}

	private void LoadWindows()
	{
		_all.Clear();
		_all.AddRange(EnumerateCandidateWindows());
		ApplyFilter();
	}

	private void ApplyFilter()
	{
		string key = _filterBox.Text.Trim();
		_filtered = _all.Where((Candidate c) => key.Length == 0 || c.Title.Contains(key, StringComparison.OrdinalIgnoreCase) || c.Process.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
		_cards.Clear();
		_itemsPanel.Children.Clear();
		foreach (Candidate item in _filtered)
		{
			_itemsPanel.Children.Add(BuildCard(item));
		}
		if (_selected != null && !_filtered.Contains(_selected))
		{
			Select(null);
		}
		_emptyHint.Text = ((_all.Count == 0) ? "没有可录制的窗口" : "没有匹配的窗口");
		_emptyHint.Visibility = ((_filtered.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void ConfirmSelected()
	{
		if (_selected != null)
		{
			this.WindowPicked?.Invoke(_selected);
			Close();
		}
	}

	private void StartPreviewLoop()
	{
		_previewCts?.Cancel();
		_previewCts = new CancellationTokenSource();
		CancellationToken ct = _previewCts.Token;
		Task.Run(async delegate
		{
			while (!ct.IsCancellationRequested)
			{
				List<Candidate> snapshot;
				try
				{
					snapshot = base.Dispatcher.Invoke(() => _filtered.ToList());
				}
				catch
				{
					break;
				}
				if (snapshot.Count == 0)
				{
					try
					{
						await Task.Delay(800, ct);
					}
					catch (TaskCanceledException)
					{
					}
				}
				else
				{
					BitmapSource?[] previews = new BitmapSource[snapshot.Count];
					for (int i = 0; i < snapshot.Count; i++)
					{
						if (ct.IsCancellationRequested)
						{
							break;
						}
						previews[i] = CapturePreview(snapshot[i]);
					}
					if (ct.IsCancellationRequested)
					{
						break;
					}
					try
					{
						base.Dispatcher.Invoke(delegate
						{
							for (int j = 0; j < snapshot.Count; j++)
							{
								if (previews[j] != null && _cards.TryGetValue(snapshot[j], out CardParts value))
								{
									value.Preview.Source = previews[j];
								}
							}
						});
					}
					catch
					{
						break;
					}
					try
					{
						await Task.Delay(800, ct);
					}
					catch (TaskCanceledException)
					{
					}
				}
			}
		}, ct);
	}

	private static BitmapSource? CapturePreview(Candidate c)
	{
		nint num = IntPtr.Zero;
		nint num2 = IntPtr.Zero;
		nint num3 = IntPtr.Zero;
		nint num4 = IntPtr.Zero;
		try
		{
			num = NativeMethods.GetDC(IntPtr.Zero);
			if (num == IntPtr.Zero)
			{
				return null;
			}
			num2 = NativeMethods.CreateCompatibleDC(num);
			int num5;
			int num6;
			if (c.IsScreen)
			{
				num5 = Math.Min(432, c.Width);
				num6 = Math.Max(1, (int)((long)c.Height * (long)num5 / c.Width));
			}
			else
			{
				num5 = c.Width;
				num6 = c.Height;
			}
			NativeMethods.BITMAPINFO bITMAPINFO = default(NativeMethods.BITMAPINFO);
			bITMAPINFO.bmiHeader = new NativeMethods.BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
				biWidth = num5,
				biHeight = -num6,
				biPlanes = 1,
				biBitCount = 32
			};
			NativeMethods.BITMAPINFO pbmi = bITMAPINFO;
			num3 = NativeMethods.CreateDIBSection(num, ref pbmi, 0u, out var ppvBits, IntPtr.Zero, 0u);
			if (num3 == IntPtr.Zero || ppvBits == IntPtr.Zero)
			{
				return null;
			}
			num4 = NativeMethods.SelectObject(num2, num3);
			bool flag = false;
			if (c.IsScreen)
			{
				flag = NativeMethods.StretchBlt(num2, 0, 0, num5, num6, num, (int)c.ScreenBounds.X, (int)c.ScreenBounds.Y, c.Width, c.Height, 13369376);
			}
			else
			{
				flag = NativeMethods.PrintWindow(c.Hwnd, num2, 2u);
				if (!flag)
				{
					nint windowDC = NativeMethods.GetWindowDC(c.Hwnd);
					if (windowDC != IntPtr.Zero)
					{
						flag = NativeMethods.BitBlt(num2, 0, 0, num5, num6, windowDC, 0, 0, 13369376);
						NativeMethods.ReleaseDC(c.Hwnd, windowDC);
					}
				}
			}
			if (!flag)
			{
				return null;
			}
			int num7 = num5 * 4;
			byte[] array = new byte[num7 * num6];
			Marshal.Copy(ppvBits, array, 0, array.Length);
			BitmapSource bitmapSource = BitmapSource.Create(num5, num6, 96.0, 96.0, PixelFormats.Bgra32, null, array, num7);
			bitmapSource.Freeze();
			if (!c.IsScreen && bitmapSource.PixelWidth > 432)
			{
				double num8 = 432.0 / (double)bitmapSource.PixelWidth;
				TransformedBitmap transformedBitmap = new TransformedBitmap(bitmapSource, new ScaleTransform(num8, num8));
				transformedBitmap.Freeze();
				return transformedBitmap;
			}
			return bitmapSource;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (num4 != IntPtr.Zero)
			{
				NativeMethods.SelectObject(num2, num4);
			}
			if (num3 != IntPtr.Zero)
			{
				NativeMethods.DeleteObject(num3);
			}
			if (num2 != IntPtr.Zero)
			{
				NativeMethods.DeleteDC(num2);
			}
			if (num != IntPtr.Zero)
			{
				NativeMethods.ReleaseDC(IntPtr.Zero, num);
			}
		}
	}

	private static List<Candidate> EnumerateCandidateWindows()
	{
		List<Candidate> result = new List<Candidate>();
		List<NativeMethods.MonitorInfo> list = (from m in NativeMethods.GetMonitors()
			orderby m.Bounds.X
			select m).ToList();
		for (int i = 0; i < list.Count; i++)
		{
			NativeMethods.MonitorInfo monitorInfo = list[i];
			int num = (int)monitorInfo.Bounds.Width;
			int num2 = (int)monitorInfo.Bounds.Height;
			if (num >= 32 && num2 >= 32)
			{
				result.Add(new Candidate(IsScreen: true, IntPtr.Zero, $"全屏 · 显示器 {i + 1}", "", num, num2, null, monitorInfo.Bounds));
			}
		}
		uint currentPid = (uint)Environment.ProcessId;
		NativeMethods.EnumWindows(delegate(nint hWnd, nint _)
		{
			if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd))
			{
				return true;
			}
			NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
			if (processId == currentPid)
			{
				return true;
			}
			StringBuilder stringBuilder = new StringBuilder(256);
			NativeMethods.GetWindowText(hWnd, stringBuilder, stringBuilder.Capacity);
			string text = stringBuilder.ToString().Trim();
			if (text.Length == 0)
			{
				return true;
			}
			long num3 = ((IntPtr)NativeMethods.GetWindowLongPtr(hWnd, -20)).ToInt64();
			if ((num3 & 0x80) != 0L || (num3 & 0x8000000) != 0L)
			{
				return true;
			}
			if (NativeMethods.DwmGetWindowAttributeInt(hWnd, 14, out var value, 4) == 0 && value != 0)
			{
				return true;
			}
			if (NativeMethods.DwmGetWindowAttribute(hWnd, 9, out var rect, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
			{
				NativeMethods.GetWindowRect(hWnd, out rect);
			}
			int num4 = rect.Right - rect.Left;
			int num5 = rect.Bottom - rect.Top;
			if (num4 < 160 || num5 < 100)
			{
				return true;
			}
			string process = "";
			try
			{
				process = Process.GetProcessById((int)processId).ProcessName;
			}
			catch
			{
			}
			result.Add(new Candidate(IsScreen: false, hWnd, text, process, num4, num5, LoadWindowIcon(hWnd), default(Rect)));
			return true;
		}, IntPtr.Zero);
		return result;
	}

	private static ImageSource? LoadWindowIcon(nint hwnd)
	{
		try
		{
			nint num = NativeMethods.SendMessageW(hwnd, 127u, new IntPtr(2), IntPtr.Zero);
			if (num == IntPtr.Zero)
			{
				num = NativeMethods.GetClassLongPtr(hwnd, -34);
			}
			if (num == IntPtr.Zero)
			{
				num = NativeMethods.GetClassLongPtr(hwnd, -14);
			}
			if (num == IntPtr.Zero)
			{
				return null;
			}
			BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(num, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			bitmapSource.Freeze();
			return bitmapSource;
		}
		catch
		{
			return null;
		}
	}
}
