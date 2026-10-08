using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SnipPin.Core.Native;
using SnipPin.Core.Services;
using SnipPin.History;

namespace SnipPin.Recording;

public sealed class RecordingBarWindow : Window
{
	private readonly RecordingService _recorder;

	private readonly TextBlock _timeText;

	private readonly DispatcherTimer _timer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(250.0)
	};

	private readonly Ellipse _dot = new Ellipse
	{
		Width = 10.0,
		Height = 10.0,
		Fill = RecordRed,
		VerticalAlignment = VerticalAlignment.Center
	};

	private Button _pauseBtn;

	private static readonly Brush TextPrimary = UiKit.Frozen(58, 65, 80);

	private static readonly Brush IconStroke = UiKit.Frozen(74, 82, 101);

	private static readonly Brush RecordRed = UiKit.Frozen(229, 72, 77);

	private static readonly Brush PausedAmber = UiKit.Frozen(245, 166, 35);

	public event Action? StopRequested;

	public event Action? CancelRequested;

	public event Action? PauseToggleRequested;

	public RecordingBarWindow(RecordingService recorder)
	{
		_recorder = recorder;
		base.Title = "TLSnipPin 录屏";
		base.WindowStyle = WindowStyle.None;
		base.AllowsTransparency = true;
		base.Background = Brushes.Transparent;
		base.ShowInTaskbar = false;
		base.ShowActivated = false;
		base.Topmost = true;
		base.ResizeMode = ResizeMode.NoResize;
		base.SizeToContent = SizeToContent.WidthAndHeight;
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		_timeText = new TextBlock
		{
			FontFamily = new FontFamily("Cascadia Mono, Consolas"),
			FontSize = 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = TextPrimary,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 4.0, 0.0)
		};
		base.Content = BuildContent();
		base.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			if (e.ButtonState == MouseButtonState.Pressed)
			{
				try
				{
					DragMove();
				}
				catch
				{
				}
			}
		};
		_timer.Tick += delegate
		{
			_timeText.Text = FormatElapsed(_recorder.Elapsed);
		};
		_recorder.Paused += OnPauseStateChanged;
		_recorder.Resumed += OnPauseStateChanged;
	}

	private static DoubleAnimation Pulse()
	{
		return new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(750.0))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever
		};
	}

	private void OnPauseStateChanged()
	{
		bool isPaused = _recorder.IsPaused;
		_pauseBtn.Content = UiKit.LineIcon(isPaused ? "M8.5 5.5l10 6.5-10 6.5z" : "M9 6.5v11 M15 6.5v11", 13.0, IconStroke);
		_pauseBtn.ToolTip = (isPaused ? "恢复录制" : "暂停录制");
		if (isPaused)
		{
			_dot.BeginAnimation(UIElement.OpacityProperty, null);
			_dot.Opacity = 1.0;
			_dot.Fill = PausedAmber;
		}
		else
		{
			_dot.Fill = RecordRed;
			_dot.BeginAnimation(UIElement.OpacityProperty, Pulse());
		}
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		try
		{
			NativeMethods.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 17u);
		}
		catch
		{
		}
	}

	protected override void OnContentRendered(EventArgs e)
	{
		base.OnContentRendered(e);
		Rect workArea = SystemParameters.WorkArea;
		base.Left = workArea.Left + (workArea.Width - base.ActualWidth) / 2.0;
		base.Top = workArea.Top + 12.0;
		_timeText.Text = FormatElapsed(_recorder.Elapsed);
		_timer.Start();
	}

	protected override void OnClosed(EventArgs e)
	{
		_timer.Stop();
		_recorder.Paused -= OnPauseStateChanged;
		_recorder.Resumed -= OnPauseStateChanged;
		base.OnClosed(e);
	}

	private UIElement BuildContent()
	{
		Border obj = new Border
		{
			Margin = new Thickness(10.0, 4.0, 10.0, 14.0),
			Padding = new Thickness(16.0, 8.0, 12.0, 8.0),
			Background = new SolidColorBrush(Color.FromArgb(247, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			CornerRadius = new CornerRadius(22.0),
			BorderBrush = UiKit.Frozen(228, 231, 242),
			BorderThickness = new Thickness(1.0),
			Effect = new DropShadowEffect
			{
				BlurRadius = 16.0,
				ShadowDepth = 2.0,
				Opacity = 0.22,
				Color = Color.FromRgb(58, 65, 80),
				RenderingBias = RenderingBias.Performance
			}
		};
		StackPanel stackPanel = (StackPanel)(obj.Child = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		});
		_dot.BeginAnimation(UIElement.OpacityProperty, Pulse());
		stackPanel.Children.Add(_dot);
		string text = _recorder.TargetTitle ?? "";
		stackPanel.Children.Add(new TextBlock
		{
			Text = ((text.Length > 0) ? ("REC " + text) : "REC"),
			FontSize = 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = TextPrimary,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
			MaxWidth = 320.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			ToolTip = text
		});
		stackPanel.Children.Add(_timeText);
		_pauseBtn = UiKit.MiniIconButton("M9 6.5v11 M15 6.5v11", "暂停录制");
		_pauseBtn.VerticalAlignment = VerticalAlignment.Center;
		_pauseBtn.Margin = new Thickness(10.0, 0.0, 4.0, 0.0);
		_pauseBtn.Click += delegate
		{
			this.PauseToggleRequested?.Invoke();
		};
		stackPanel.Children.Add(_pauseBtn);
		Button button = UiKit.MiniIconButton("M6.5 6.5l11 11 M17.5 6.5l-11 11", "取消本次录制（不保存）");
		button.VerticalAlignment = VerticalAlignment.Center;
		button.Margin = new Thickness(10.0, 0.0, 4.0, 0.0);
		button.Click += delegate
		{
			this.CancelRequested?.Invoke();
		};
		stackPanel.Children.Add(button);
		Button button2 = new Button
		{
			Height = 30.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = RecordRed,
			Cursor = Cursors.Hand,
			ToolTip = "停止并保存录屏",
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
			Template = UiKit.ButtonTemplate(9, "#F2555A", "#C93A3F"),
			Content = UiKit.ActionContent("M7 7h10v10H7z", Brushes.White, "停止", Brushes.White)
		};
		button2.Click += delegate
		{
			this.StopRequested?.Invoke();
		};
		stackPanel.Children.Add(button2);
		return obj;
	}

	internal static string FormatElapsed(TimeSpan t)
	{
		if (t.TotalHours >= 1.0)
		{
			return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
		}
		return $"{t.Minutes:D2}:{t.Seconds:D2}";
	}
}
