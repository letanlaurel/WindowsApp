using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SnipPin.Core.Configuration;
using SnipPin.Settings;

namespace SnipPin.Recording;

public class RecordingSettingsWindow : Window
{
	private readonly ConfigService _config;

	private TextBox _hkRecord;

	private TextBox _hkPause;

	private ComboBox _quality;

	private TextBox _bitrate;

	private ComboBox _fps;

	private CheckBox _drawCursor;

	private TextBox _dir;

	private ComboBox _audioSource;

	private TextBox _activeLimit;

	private TextBox _archiveBuffer;

	private bool _syncing;

	private static readonly (string Label, int Kbps)[] QualityPresets = new(string, int)[5]
	{
		("自动（按分辨率）", 0),
		("标清 4 Mbps", 4000),
		("高清 8 Mbps", 8000),
		("超清 16 Mbps", 16000),
		("自定义", -1)
	};

	public event Action? SettingsSaved;

	public RecordingSettingsWindow(ConfigService config)
	{
		_config = config;
		base.Title = "TLSnipPin 录屏设置";
		base.Icon = Program.LoadAppIcon();
		base.Width = 500.0;
		base.SizeToContent = SizeToContent.Height;
		base.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Content = BuildContent();
	}

	private UIElement BuildContent()
	{
		Grid obj = new Grid
		{
			Margin = new Thickness(12.0),
			RowDefinitions = 
			{
				new RowDefinition
				{
					Height = GridLength.Auto
				},
				new RowDefinition
				{
					Height = GridLength.Auto
				}
			}
		};
		StackPanel element = new StackPanel
		{
			Children = 
			{
				BuildHotkeyGroup(),
				BuildQualityGroup(),
				BuildSaveGroup()
			}
		};
		Grid.SetRow(element, 0);
		obj.Children.Add(element);
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
		};
		Button button = new Button
		{
			Content = "保存",
			Width = 90.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		button.Click += delegate
		{
			OnSave();
		};
		Button button2 = new Button
		{
			Content = "取消",
			Width = 90.0
		};
		button2.Click += delegate
		{
			Close();
		};
		stackPanel.Children.Add(button);
		stackPanel.Children.Add(button2);
		Grid.SetRow(stackPanel, 1);
		obj.Children.Add(stackPanel);
		return obj;
	}

	private UIElement BuildHotkeyGroup()
	{
		HotkeyConfig hotkeys = _config.Config.Hotkeys;
		_hkRecord = SettingsWindow.HotkeyBox(hotkeys.RecordToggle);
		_hkPause = SettingsWindow.HotkeyBox(hotkeys.RecordPause);
		return SettingsWindow.Group("快捷键（点击输入框后按下新快捷键，Esc 清空）", SettingsWindow.Row("开始 / 停止录制", _hkRecord), SettingsWindow.Row("暂停 / 恢复录制", _hkPause));
	}

	private UIElement BuildQualityGroup()
	{
		RecordingConfig rec = _config.Config.Recording;
		_quality = new ComboBox
		{
			Width = 170.0
		};
		(string, int)[] qualityPresets = QualityPresets;
		for (int i = 0; i < qualityPresets.Length; i++)
		{
			string item = qualityPresets[i].Item1;
			_quality.Items.Add(item);
		}
		int num = Array.FindIndex(QualityPresets, ((string Label, int Kbps) p) => p.Kbps == rec.BitrateKbps);
		_quality.SelectedIndex = ((num >= 0) ? num : Array.FindIndex(QualityPresets, ((string Label, int Kbps) p) => p.Kbps == -1));
		_bitrate = new TextBox
		{
			Width = 120.0,
			Text = ((rec.BitrateKbps > 0) ? rec.BitrateKbps.ToString() : ""),
			ToolTip = "范围 500 - 100000；留空或 0 = 按分辨率自动估算"
		};
		_quality.SelectionChanged += delegate
		{
			if (!_syncing)
			{
				int item2 = QualityPresets[Math.Max(0, _quality.SelectedIndex)].Kbps;
				if (item2 >= 0)
				{
					_syncing = true;
					_bitrate.Text = ((item2 > 0) ? item2.ToString() : "");
					_syncing = false;
				}
			}
		};
		_bitrate.TextChanged += delegate
		{
			if (!_syncing)
			{
				_syncing = true;
				_quality.SelectedIndex = Array.FindIndex(QualityPresets, ((string Label, int Kbps) p) => p.Kbps == -1);
				_syncing = false;
			}
		};
		_fps = new ComboBox
		{
			Width = 120.0
		};
		int[] array = new int[4] { 15, 24, 30, 60 };
		foreach (int num2 in array)
		{
			_fps.Items.Add(num2.ToString());
		}
		string text = rec.Fps.ToString();
		if (!_fps.Items.Contains(text))
		{
			_fps.Items.Add(text);
		}
		_fps.SelectedItem = text;
		_drawCursor = new CheckBox
		{
			Content = "把鼠标光标画进录屏画面",
			IsChecked = rec.DrawCursor
		};
		_audioSource = new ComboBox
		{
			Width = 170.0
		};
		string[] array2 = new string[3] { "不录制声音", "系统声音", "麦克风" };
		string[] array3 = new string[3] { "none", "system", "microphone" };
		foreach (string item2 in array2)
		{
			_audioSource.Items.Add(item2);
		}
		int num3 = Array.IndexOf(array3, (rec.AudioSource ?? "none").Trim().ToLowerInvariant());
		_audioSource.SelectedIndex = ((num3 >= 0) ? num3 : 0);
		_audioSource.ToolTip = "系统声音：录制电脑播放的音频（扬声器输出）；麦克风：录制环境声音（不含系统播放）";
		return SettingsWindow.Group("画质与录制内容", SettingsWindow.Row("画质预设", _quality), SettingsWindow.Row("码率 (kbps)", _bitrate), SettingsWindow.Row("帧率", _fps), SettingsWindow.Row("录制声音", _audioSource), SettingsWindow.Row("鼠标光标", _drawCursor));
	}

	private UIElement BuildSaveGroup()
	{
		_dir = new TextBox
		{
			Text = _config.Config.Recording.Directory,
			Width = 260.0
		};
		Button button = new Button
		{
			Content = "浏览...",
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			Padding = new Thickness(8.0, 2.0, 8.0, 2.0)
		};
		button.Click += delegate
		{
			OpenFolderDialog openFolderDialog = new OpenFolderDialog
			{
				Title = "选择录屏保存目录"
			};
			if (openFolderDialog.ShowDialog(this).GetValueOrDefault())
			{
				_dir.Text = openFolderDialog.FolderName;
			}
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		stackPanel.Children.Add(_dir);
		stackPanel.Children.Add(button);
		_activeLimit = new TextBox
		{
			Text = _config.Config.Recording.ActiveLimit.ToString(),
			Width = 80,
			ToolTip = "活跃列表条数上限；超过后开始攒缓冲，攒满一次性把最旧的按月归档到压缩包（0 = 永不归档）"
		};
		TextBlock textBlock = new TextBlock
		{
			Text = "条，超过后开始攒缓冲",
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0)
		};
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		stackPanel2.Children.Add(_activeLimit);
		stackPanel2.Children.Add(textBlock);
		_archiveBuffer = new TextBox
		{
			Text = _config.Config.Recording.ArchiveBuffer.ToString(),
			Width = 80,
			ToolTip = "超过上限后再攒多少条才批量归档（0 = 每超 1 条立即归档 1 条）"
		};
		TextBlock textBlock2 = new TextBlock
		{
			Text = "条，攒满后一次性归档",
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0)
		};
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		stackPanel3.Children.Add(_archiveBuffer);
		stackPanel3.Children.Add(textBlock2);
		return SettingsWindow.Group("保存", SettingsWindow.Row("保存目录", stackPanel), SettingsWindow.Row("活跃上限", stackPanel2), SettingsWindow.Row("归档缓冲", stackPanel3));
	}

	private void OnSave()
	{
		AppConfig config = _config.Config;
		config.Hotkeys.RecordToggle = _hkRecord.Text.Trim();
		config.Hotkeys.RecordPause = _hkPause.Text.Trim();
		RecordingConfig recording = config.Recording;
		if (!int.TryParse(_bitrate.Text.Trim(), out var result) || result < 0)
		{
			result = 0;
		}
		recording.BitrateKbps = Math.Clamp(result, 0, 100000);
		if (int.TryParse(_fps.SelectedItem as string, out var result2))
		{
			recording.Fps = Math.Clamp(result2, 5, 60);
		}
		recording.DrawCursor = _drawCursor.IsChecked.GetValueOrDefault();
		string[] array = new string[3] { "none", "system", "microphone" };
		recording.AudioSource = array[Math.Max(0, _audioSource.SelectedIndex)];
		if (!int.TryParse(_activeLimit.Text.Trim(), out var result3) || result3 < 0)
		{
			result3 = 50;
		}
		recording.ActiveLimit = Math.Clamp(result3, 0, 10000);
		if (!int.TryParse(_archiveBuffer.Text.Trim(), out var result4) || result4 < 0)
		{
			result4 = 5;
		}
		recording.ArchiveBuffer = Math.Clamp(result4, 0, 10000);
		recording.Directory = _dir.Text.Trim();
		_config.Save();
		this.SettingsSaved?.Invoke();
		Close();
	}
}
