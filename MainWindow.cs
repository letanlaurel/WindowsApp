using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using SnipPin.Core.Configuration;
using SnipPin.Core.Native;
using SnipPin.Core.Services;

namespace SnipPin;

/// <summary>
/// 应用主窗体：默认隐藏，仅承载消息泵、全局热键钩子与系统托盘。
/// 截图历史主窗口（HistoryWindow）在启动时显示。
/// </summary>
public class MainWindow : Window
{
    private TaskbarIcon? _tray;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;

    private IHotkeyService _hotkeys = null!;
    private ICaptureService _capture = null!;
    private IPinService _pin = null!;
    private IStorageService _storage = null!;
    private ConfigService _config = null!;
    private HistoryService _historySvc = null!;
    private ClipboardService _clipboard = null!;
    private RecordingService _recorder = null!;
    private RecordingHistoryService _recHistory = null!;

    private History.HistoryWindow? _historyWin;
    private ClipboardHistory.ClipboardWindow? _clipboardWin;
    private Recording.RecordingBarWindow? _recBar;

    public MainWindow()
    {
        // 彻底隐藏：不在任务栏、Alt+Tab 中显示
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 0;
        Height = 0;
        Opacity = 0;
    }

    /// <summary>由入口在 Startup 时调用，注入服务并完成初始化</summary>
    public void Init(IServiceProvider services)
    {
        _hotkeys = services.GetRequiredService<IHotkeyService>();
        _capture = services.GetRequiredService<ICaptureService>();
        _pin = services.GetRequiredService<IPinService>();
        _storage = services.GetRequiredService<IStorageService>();
        _config = services.GetRequiredService<ConfigService>();
        _historySvc = services.GetRequiredService<HistoryService>();
        _clipboard = services.GetRequiredService<ClipboardService>();
        _recorder = services.GetRequiredService<RecordingService>();
        _recHistory = services.GetRequiredService<RecordingHistoryService>();

        // 录屏状态联动：开始 → 悬浮控制条；结束 → 关闭控制条并提示；错误 → 气泡
        _recorder.Started += _ => ShowRecBar();
        _recorder.Stopped += OnRecordingStopped;
        _recorder.Error += text => _tray?.ShowBalloonTip("录屏出错", text, BalloonIcon.Error);

        // 建立 Win32 消息钩子以接收 WM_HOTKEY
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);

        // 先建托盘（热键注册失败时需要气泡通知）
        InitTray();

        // 订阅热键动作与注册失败通知
        if (_hotkeys is HotkeyService hs)
        {
            hs.HotkeyPressed += OnHotkey;
            hs.RegistrationFailed += text => _tray?.ShowBalloonTip(
                "热键注册失败",
                $"{text} 可能被其他程序占用，请在设置中更换。",
                BalloonIcon.Warning);
        }
        _hotkeys.RegisterAll(helper.Handle);

        // 挂载系统剪贴板监听（复用本窗口句柄接收 WM_CLIPBOARDUPDATE）
        _clipboard.Start(_hwnd);

        // 恢复上次的贴图会话
        _pin.RestoreSession();

        // 启动即显示截图历史主窗口
        ShowHistory();
    }

    /// <summary>
    /// 显示截图历史窗口；已关闭则重建（HistoryService 为单例，数据不丢）。
    /// </summary>
    private void ShowHistory()
    {
        if (_historyWin != null)
        {
            _historyWin.Activate();
            return;
        }

        _historyWin = new History.HistoryWindow(_historySvc, _pin, _storage, _config, _recHistory, _recorder);
        // 历史窗内点击"设置"时，由主窗体打开设置（保存后自动重注册热键）
        _historyWin.OpenSettingsRequested += OpenSettings;
        // 工具栏截图/剪贴板/全屏按钮复用热键分发逻辑
        _historyWin.ActionRequested += OnHotkey;
        // 录屏面板点击「录制窗口」：打开录制目标选择器
        _historyWin.RecordRequested += StartWindowRecording;
        // 录屏设置保存后：重注册热键并迁移录屏目录
        _historyWin.RecordingSettingsSaved += () =>
        {
            _hotkeys.RegisterAll(_hwnd);
            _recHistory.ApplyConfigDirectory();
        };
        _historyWin.Closed += (_, _) => _historyWin = null;
        _historyWin.Show();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowMessages.WM_HOTKEY && _hotkeys is HotkeyService hs)
        {
            // 钩子内异常可能绕过 Dispatcher 兜底，这里单独捕获记录
            try
            {
                hs.OnHotkeyMessage(wParam.ToInt32());
            }
            catch (Exception ex)
            {
                Program.LogError("热键处理", ex);
            }
            handled = true;
        }
        else if (msg == WindowMessages.WM_CLIPBOARDUPDATE)
        {
            // 剪贴板内容变化：转发给剪贴板历史服务（内部有防抖与异常兜底）
            try
            {
                _clipboard.OnClipboardUpdate();
            }
            catch (Exception ex)
            {
                Program.LogError("剪贴板监听", ex);
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnHotkey(string action)
    {
        // 确保在 UI 线程执行
        Dispatcher.Invoke(() =>
        {
            switch (action)
            {
                case "capture": _capture.StartRegionCapture(); break;
                case "fullScreen": _capture.CaptureFullScreen(); break;
                case "pinClipboard": _capture.PinFromClipboard(); break;
                case "togglePins": _pin.ToggleAll(); break;
                case "clipboardHistory": ShowClipboardPopup(); break;
                case "recordToggle":
                    if (_recorder.IsRecording)
                        _recorder.Stop(discard: false);
                    else
                        StartWindowRecording();
                    break;
                case "recordPause":
                    if (_recorder.IsRecording)
                    {
                        if (_recorder.IsPaused) _recorder.Resume();
                        else _recorder.Pause();
                    }
                    break;
            }
        });
    }

    /// <summary>
    /// 在鼠标附近弹出剪贴板历史（Ctrl+Alt+V）。
    /// 弹出前记录当前前台窗口；面板非激活+低级钩子喂键（同 Win+V），目标全程不失焦，
    /// 选中条目后直接模拟 Ctrl+V 完成粘贴。
    /// </summary>
    private void ShowClipboardPopup()
    {
        if (_clipboardWin != null)
        {
            // 已打开则激活弹窗聚焦搜索框（可输入中文）
            _clipboardWin.FocusWindow();
            return;
        }

        // 弹窗显示前保存粘贴目标窗口（此时前台仍是用户原来的应用）
        var target = NativeMethods.GetForegroundWindow();
        // 记录目标当前焦点子窗口（通常即输入框）：粘贴时精确归还焦点，避免只激活顶层窗口而编辑焦点丢失
        var focusHwnd = target;
        try
        {
            var tid = NativeMethods.GetWindowThreadProcessId(target, out _);
            var gi = new NativeMethods.GUITHREADINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
            };
            if (tid != 0 && NativeMethods.GetGUIThreadInfo(tid, ref gi) && gi.HwndFocus != IntPtr.Zero)
                focusHwnd = gi.HwndFocus;
        }
        catch { /* 查询失败回退顶层窗口 */ }
        _clipboardWin = new ClipboardHistory.ClipboardWindow(_clipboard, target, focusHwnd);
        _clipboardWin.Closed += (_, _) => _clipboardWin = null;
        _clipboardWin.Show(); // 非激活显示：不夺走目标窗口焦点（同 Win+V），键盘由低级钩子喂入
    }

    // ---------- 录屏 ----------
    /// <summary>打开录制目标选择器，选定后开始录制该窗口</summary>
    private void StartWindowRecording()
    {
        if (_recorder.IsRecording)
        {
            _tray?.ShowBalloonTip("正在录制", "请先停止当前录制，再开始新的录制。", BalloonIcon.Warning);
            return;
        }

        var picker = new Recording.WindowPickerWindow();
        if (_historyWin != null)
            picker.Owner = _historyWin;
        picker.WindowPicked += cand =>
        {
            if (cand.IsScreen)
                _recorder.StartScreen(cand.ScreenBounds, cand.Title);
            else
                _recorder.Start(cand.Hwnd, cand.Title);
        };
        picker.Show();
    }

    /// <summary>录制开始：显示悬浮控制条</summary>
    private void ShowRecBar()
    {
        // 保险：重复开始时先关旧条
        _recBar?.Close();

        var bar = new Recording.RecordingBarWindow(_recorder);
        bar.StopRequested += () => _recorder.Stop(discard: false);
        bar.PauseToggleRequested += () =>
        {
            if (_recorder.IsPaused) _recorder.Resume();
            else _recorder.Pause();
        };
        bar.CancelRequested += () =>
        {
            if (MessageBox.Show("确定取消并丢弃本次录制？", "取消录制",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _recorder.Stop(discard: true);
        };
        bar.Closed += (_, _) =>
        {
            if (_recBar == bar) _recBar = null;
        };
        _recBar = bar;
        bar.Show();
    }

    /// <summary>录制结束：关闭控制条并气泡提示</summary>
    private void OnRecordingStopped(bool saved)
    {
        if (_recBar != null)
        {
            var bar = _recBar;
            _recBar = null;
            bar.Close();
        }
        if (saved)
            _tray?.ShowBalloonTip("录屏已保存",
                "可在主窗口「录屏」标签中查看，双击卡片即可播放。", BalloonIcon.Info);
    }

    // ---------- 系统托盘 ----------
    private void InitTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "TLSnipPin 截图贴图",
            // 优先使用嵌入的应用图标，加载失败退回程序内生成的简易图标
            Icon = LoadTrayIcon() ?? CreateDefaultIcon(),
        };

        var menu = new ContextMenu();

        var history = new MenuItem { Header = "截图历史", FontWeight = System.Windows.FontWeights.Bold };
        history.Click += (_, _) => ShowHistory();

        var capture = new MenuItem { Header = "截图 (F1)" };
        capture.Click += (_, _) => _capture.StartRegionCapture();

        var pinClipboard = new MenuItem { Header = "钉住剪贴板图片 (F2)" };
        pinClipboard.Click += (_, _) => _capture.PinFromClipboard();

        var full = new MenuItem { Header = "全屏截图 (F3)" };
        full.Click += (_, _) => _capture.CaptureFullScreen();

        var record = new MenuItem { Header = "录制窗口..." };
        record.Click += (_, _) => StartWindowRecording();

        var closePins = new MenuItem { Header = "关闭所有贴图" };
        closePins.Click += (_, _) => _pin.CloseAll();

        var openDir = new MenuItem { Header = "打开保存目录" };
        openDir.Click += (_, _) => OpenSaveDir();

        var settings = new MenuItem { Header = "设置..." };
        settings.Click += (_, _) => OpenSettings();

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(history);
        menu.Items.Add(new Separator());
        menu.Items.Add(capture);
        menu.Items.Add(pinClipboard);
        menu.Items.Add(full);
        menu.Items.Add(record);
        menu.Items.Add(new Separator());
        menu.Items.Add(closePins);
        menu.Items.Add(openDir);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _tray.ContextMenu = menu;
        // 左键单击托盘图标：打开截图历史主界面（X 关闭窗口后可从这里重新打开）
        // 截图请使用 F1 热键或右键菜单
        _tray.TrayLeftMouseDown += (_, _) => ShowHistory();
    }

    private void OpenSaveDir()
    {
        var dir = _config.Config.Save.Directory;
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    private void OpenSettings()
    {
        var win = new Settings.SettingsWindow(_config);
        // 保存后：热键可能已变更需重注册；历史目录可能已修改需迁移
        win.SettingsSaved += () =>
        {
            _hotkeys.RegisterAll(_hwnd);
            _historySvc.ApplyConfigDirectory();
        };
        win.Show();
        win.Activate();
    }

    private void ExitApp()
    {
        // 在窗口被系统关闭前持久化贴图会话
        _pin.PersistSession();
        _hotkeys.UnregisterAll();
        _tray?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 停止录制并等待编码收尾（保证 MP4 完整落盘）
        _recorder?.Stop(discard: false, wait: true);
        // 覆盖非托盘退出路径（如注销、任务管理器结束等触发 Closing 的场景）
        _pin?.PersistSession();
        _hotkeys?.UnregisterAll();
        _clipboard?.Stop();
        _tray?.Dispose();
        base.OnClosing(e);
    }

    /// <summary>
    /// 加载应用图标作为托盘图标（System.Drawing）。
    /// 单文件发布下 pack:// 嵌入资源解析不稳定，改为直接从进程 exe 提取
    /// （exe 的应用图标由 csproj ApplicationIcon 嵌入，发布单文件后依然可用）。
    /// </summary>
    private static Icon? LoadTrayIcon()
    {
        // 首选：从当前进程 exe 提取关联图标
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch { /* 落到资源加载 */ }

        // 退路：从嵌入资源读取
        try
        {
            var sri = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/logo.ico"));
            if (sri == null) return null;
            using var ms = new System.IO.MemoryStream();
            sri.Stream.CopyTo(ms);
            ms.Position = 0;
            return new Icon(ms, new System.Drawing.Size(32, 32));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 生成一个简易的托盘图标（蓝色方块 + 相机图案），作为嵌入图标加载失败的退路。
    /// </summary>
    private static Icon CreateDefaultIcon()
    {
        const int size = 32;
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.DodgerBlue);
            using var brush = new SolidBrush(System.Drawing.Color.White);
            // 简易"取景框"图案
            g.FillRectangle(brush, 8, 12, 16, 12);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.DodgerBlue, 2);
            g.DrawRectangle(pen, 10, 14, 12, 8);
        }
        IntPtr hIcon = bmp.GetHicon();
        var icon = (Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        bmp.Dispose();
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
