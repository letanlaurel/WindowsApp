using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnipPin.Core.Configuration;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>
/// 录屏服务：后台线程按配置帧率抓取目标窗口画面，
/// 经 PrintWindow（DWM 完整内容渲染，被遮挡也能捕获）绘制到 DIB 画布，
/// 再由 Mp4Encoder 编码为 H.264 MP4（可选 WASAPI 采集系统声音/麦克风混入 AAC 音轨）。
/// 支持窗口移动/缩放跟随、可选光标绘制、暂停（音视频时间轴同步扣除暂停段）。
/// 单路录制：同一时间仅一个目标窗口。
/// </summary>
public class RecordingService
{
    private readonly ConfigService _config;
    private readonly RecordingHistoryService _history;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _discardRequested;
    // 暂停支持：UI 线程仅置请求标志，录制线程统一处理时钟/编码器（Mp4Encoder 非线程安全）
    private volatile bool _pauseRequested;
    private volatile bool _paused;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private string? _targetTitle;
    /// <summary>全屏模式的目标屏幕区域（物理像素）；null = 窗口模式</summary>
    private NativeMethods.RECT? _screenBounds;

    /// <summary>是否正在录制</summary>
    public bool IsRecording { get; private set; }

    /// <summary>录制是否处于暂停状态（暂停期间时长与视频均不推进）</summary>
    public bool IsPaused => _paused;

    /// <summary>已录制时长（录制中实时增长）</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>当前录制目标窗口标题</summary>
    public string? TargetTitle => _targetTitle;

    /// <summary>开始录制（UI 线程调用，立即返回，录制在后台线程进行）</summary>
    public event Action<string>? Started;

    /// <summary>录制结束（已回调到 UI 线程）；参数 true=已保存，false=已取消/失败</summary>
    public event Action<bool>? Stopped;

    /// <summary>录制出错（已回调到 UI 线程）；参数为错误描述</summary>
    public event Action<string>? Error;

    /// <summary>录制已暂停（已回调到 UI 线程）</summary>
    public event Action? Paused;

    /// <summary>录制已恢复（已回调到 UI 线程）</summary>
    public event Action? Resumed;

    public RecordingService(ConfigService config, RecordingHistoryService history)
    {
        _config = config;
        _history = history;
    }

    /// <summary>开始录制指定窗口</summary>
    public void Start(IntPtr hwnd, string title)
    {
        if (IsRecording) return;
        if (!NativeMethods.IsWindow(hwnd)) return;
        StartInternal(hwnd, null, title);
    }

    /// <summary>开始录制指定屏幕区域（物理像素），title 形如「全屏 · 显示器 1」</summary>
    public void StartScreen(System.Windows.Rect screenBounds, string title)
    {
        if (IsRecording) return;
        if (screenBounds.Width < 32 || screenBounds.Height < 32) return;
        var rect = new NativeMethods.RECT
        {
            Left = (int)Math.Floor(screenBounds.X),
            Top = (int)Math.Floor(screenBounds.Y),
            Right = (int)Math.Ceiling(screenBounds.Right),
            Bottom = (int)Math.Ceiling(screenBounds.Bottom),
        };
        StartInternal(IntPtr.Zero, rect, title);
    }

    private void StartInternal(IntPtr hwnd, NativeMethods.RECT? screenBounds, string title)
    {
        var dir = _config.Config.Recording.Directory;
        Directory.CreateDirectory(dir);

        // 目标文件名（同秒冲突时追加序号）
        var path = Path.Combine(dir, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}_{i}.mp4");

        _stopRequested = false;
        _discardRequested = false;
        _pauseRequested = false;
        _paused = false;
        _targetTitle = title;
        _screenBounds = screenBounds;
        _clock.Restart();
        IsRecording = true;

        _thread = new Thread(() => RecordLoop(hwnd, title, path))
        {
            IsBackground = true,
            Name = "TLSnipPin 录屏",
        };
        _thread.Start();
        Started?.Invoke(title);
    }

    /// <summary>停止录制；wait=true 时阻塞等待编码收尾（退出应用时用）</summary>
    public void Stop(bool discard = false, bool wait = false)
    {
        if (!IsRecording) return;
        _discardRequested = discard;
        _stopRequested = true;
        if (wait)
            _thread?.Join(8000);
    }

    /// <summary>暂停录制（录制线程在下一帧生效，经 Paused 事件确认）</summary>
    public void Pause()
    {
        if (IsRecording) _pauseRequested = true;
    }

    /// <summary>恢复录制（未暂停时调用无副作用）</summary>
    public void Resume()
    {
        _pauseRequested = false;
    }

    // ---------------- 录制线程 ----------------
    private void RecordLoop(IntPtr hwnd, string title, string path)
    {
        var cfg = _config.Config.Recording;
        int fps = Math.Clamp(cfg.Fps <= 0 ? 30 : cfg.Fps, 5, 60);
        bool drawCursor = cfg.DrawCursor;
        string audioSource = (cfg.AudioSource ?? "none").Trim().ToLowerInvariant();
        // 全屏模式：直接 BitBlt 屏幕区域；窗口模式：PrintWindow 优先
        var screenRect = _screenBounds;
        bool screenMode = screenRect.HasValue;
        bool saved = false;
        string? thumbPath = null;
        int width = 0, height = 0;

        // 提高系统定时器精度，让 Thread.Sleep(1) 的帧间隔等待足够准
        NativeMethods.timeBeginPeriod(1);

        // Media Foundation 需要 COM 初始化（S_OK/S_FALSE 都算成功；模式冲突则跳过注销）
        int cohr = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
        bool comInited = cohr is 0 or 1;

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, canvas = IntPtr.Zero, oldCanvas = IntPtr.Zero;
        IntPtr bits = IntPtr.Zero;
        // 窗口缩放时的临时画布（尺寸变化才创建），统一在 finally 释放
        IntPtr tempDc = IntPtr.Zero, tempBmp = IntPtr.Zero, oldTemp = IntPtr.Zero;
        // 编码器在 finally 中收尾（try/catch 保护，避免收尾异常替换主异常）
        Mp4Encoder? encoder = null;
        // 音频采集器在 finally 中先于编码器收尾（drain 剩余包后停线程）
        AudioCapturer? audio = null;

        try
        {
            var rect = screenMode ? screenRect!.Value : GetLiveRect(hwnd);
            // H.264 编码器要求宽高为偶数（奇数会被拒绝：MF_E_INVALIDMEDIATYPE），
            // 画布向下取偶（最多裁掉 1 像素，无感知）；奇数窗口经临时画布等比适配
            width = (rect.Right - rect.Left) & ~1;
            height = (rect.Bottom - rect.Top) & ~1;
            if (width < 32 || height < 32)
                throw new InvalidOperationException("目标区域尺寸过小，无法录制");

            // DIB 画布：GDI 直接绘制、托管侧直接读同一块像素内存（顶向下 BGRA）
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // 负高 = 顶向下，与 MF RGB32 行序一致
                    biPlanes = 1,
                    biBitCount = 32,
                },
            };
            canvas = NativeMethods.CreateDIBSection(screenDc, ref bmi, NativeMethods.DIB_RGB_COLORS,
                out bits, IntPtr.Zero, 0);
            if (canvas == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("无法创建录制画布（CreateDIBSection）");
            oldCanvas = NativeMethods.SelectObject(memDc, canvas);

            int stride = width * 4;
            int frameLen = stride * height;
            var frame = new byte[frameLen];

            // 音频采集器先行初始化（端点混音格式决定编码器音频流参数）；
            // 失败降级为纯视频（无声卡/无设备等情况不应让整个录屏失败）。
            // 时钟委托引用 encoder 局部变量：采集线程启动时 encoder 已创建完毕
            if (audioSource is "system" or "microphone")
            {
                try
                {
                    audio = new AudioCapturer(
                        audioSource == "microphone" ? AudioCaptureMode.Microphone : AudioCaptureMode.System,
                        () => encoder!.Now100ns);
                }
                catch (Exception ex)
                {
                    Program.LogError("录屏音频初始化", ex);
                    ReportError($"无法录制声音（{ex.Message}），本次仅录制画面。");
                }
            }

            encoder = new Mp4Encoder(path, width, height, fps, ResolveBitrate(width, height, fps),
                audio != null
                    ? new Mp4Encoder.AudioStreamOptions(audio.SampleRate, audio.Channels, ResolveAudioBitrate(audio.Channels))
                    : null);
            audio?.Start();

            // ---- 每帧绘制：全屏直接 BitBlt 屏幕区域；窗口 PrintWindow 优先，尺寸变化走临时画布缩放 ----
            int tempW = 0, tempH = 0;

            void Draw()
            {
                if (!screenMode && NativeMethods.IsIconic(hwnd))
                    return; // 最小化：保留上一帧画面（时长仍按时间戳推进）

                var live = screenMode ? screenRect!.Value : GetLiveRect(hwnd);
                int lw = Math.Max(1, live.Right - live.Left);
                int lh = Math.Max(1, live.Bottom - live.Top);

                if (screenMode)
                {
                    // 全屏模式：整块屏幕区域拷入画布（悬浮条已通过 WDA 从捕获中排除）
                    NativeMethods.BitBlt(memDc, 0, 0, width, height,
                        screenDc, live.Left, live.Top, NativeMethods.SRCCOPY);
                }
                else if (lw == width && lh == height)
                {
                    if (!NativeMethods.PrintWindow(hwnd, memDc, NativeMethods.PW_RENDERFULLCONTENT))
                    {
                        var wdc = NativeMethods.GetWindowDC(hwnd);
                        if (wdc != IntPtr.Zero)
                        {
                            NativeMethods.BitBlt(memDc, 0, 0, width, height, wdc, 0, 0, NativeMethods.SRCCOPY);
                            NativeMethods.ReleaseDC(hwnd, wdc);
                        }
                    }
                }
                else
                {
                    // 窗口尺寸已变化：先按实际尺寸画到临时画布，再等比缩放回固定尺寸
                    // （H.264 流的分辨率不可中途改变）
                    if (tempBmp == IntPtr.Zero || tempW != lw || tempH != lh)
                    {
                        if (oldTemp != IntPtr.Zero) NativeMethods.SelectObject(tempDc, oldTemp);
                        if (tempBmp != IntPtr.Zero) NativeMethods.DeleteObject(tempBmp);
                        if (tempDc == IntPtr.Zero) tempDc = NativeMethods.CreateCompatibleDC(screenDc);
                        var tb = new NativeMethods.BITMAPINFO
                        {
                            bmiHeader = new NativeMethods.BITMAPINFOHEADER
                            {
                                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                                biWidth = lw,
                                biHeight = -lh,
                                biPlanes = 1,
                                biBitCount = 32,
                            },
                        };
                        tempBmp = NativeMethods.CreateDIBSection(screenDc, ref tb, NativeMethods.DIB_RGB_COLORS,
                            out _, IntPtr.Zero, 0);
                        oldTemp = tempBmp == IntPtr.Zero ? IntPtr.Zero : NativeMethods.SelectObject(tempDc, tempBmp);
                        tempW = lw;
                        tempH = lh;
                    }
                    if (tempBmp != IntPtr.Zero)
                    {
                        if (!NativeMethods.PrintWindow(hwnd, tempDc, NativeMethods.PW_RENDERFULLCONTENT))
                        {
                            var wdc = NativeMethods.GetWindowDC(hwnd);
                            if (wdc != IntPtr.Zero)
                            {
                                NativeMethods.BitBlt(tempDc, 0, 0, lw, lh, wdc, 0, 0, NativeMethods.SRCCOPY);
                                NativeMethods.ReleaseDC(hwnd, wdc);
                            }
                        }
                        NativeMethods.StretchBlt(memDc, 0, 0, width, height, tempDc, 0, 0, lw, lh, NativeMethods.SRCCOPY);
                    }
                }

                // 光标绘制（按当前窗口位置换算，缩放时等比映射）
                if (drawCursor)
                {
                    var ci = new NativeMethods.CURSORINFO
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>(),
                    };
                    if (NativeMethods.GetCursorInfo(ref ci)
                        && (ci.flags & NativeMethods.CURSOR_SHOWING) != 0
                        && ci.hCursor != IntPtr.Zero)
                    {
                        int cx = ci.ptScreenPos.X, cy = ci.ptScreenPos.Y;
                        if (cx >= live.Left - 32 && cy >= live.Top - 32
                            && cx < live.Right + 32 && cy < live.Bottom + 32)
                        {
                            int sx = (int)((long)(cx - live.Left) * width / lw);
                            int sy = (int)((long)(cy - live.Top) * height / lh);
                            NativeMethods.DrawIconEx(memDc, sx, sy, ci.hCursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
                        }
                    }
                }
            }

            // ---- 帧循环：固定节奏采集 + 墙钟时间戳编码 ----
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long period = System.Diagnostics.Stopwatch.Frequency / fps;
            long next = sw.ElapsedTicks;
            bool thumbSaved = false;
            bool audioFaultReported = false;

            // 取走采集器产出的音频包写入编码器（时间戳已按统一时钟生成）
            void DrainAudio()
            {
                if (audio == null) return;
                while (audio.TryDequeue(out var pkt))
                    encoder.WriteAudioSample(pkt.Data, pkt.Ts100ns, pkt.Dur100ns);
                // 采集线程异常（设备移除等）：提示一次并停止取包，视频继续
                if (!audioFaultReported && audio.FaultMessage is { } msg)
                {
                    audioFaultReported = true;
                    ReportError($"{msg}，视频将继续录制。");
                }
            }

            while (true)
            {
                // 暂停/恢复切换（UI 线程仅置标志，此处统一停走计时与编码器时钟）
                if (_pauseRequested != _paused)
                {
                    _paused = _pauseRequested;
                    if (_paused)
                    {
                        _clock.Stop();
                        encoder.Pause();
                        audio?.SetPaused(true);
                        FireOnUi(() => Paused?.Invoke());
                    }
                    else
                    {
                        _clock.Start();
                        encoder.Resume();
                        audio?.SetPaused(false);
                        FireOnUi(() => Resumed?.Invoke());
                    }
                }
                if (_paused && !_stopRequested)
                {
                    Thread.Sleep(30); // 暂停中：不采集不编码
                    continue;
                }

                Draw();
                Marshal.Copy(bits, frame, 0, frameLen);
                encoder.WriteFrame(frame);
                DrainAudio();

                if (!thumbSaved)
                {
                    thumbPath = SaveThumb(frame, width, height, stride, path);
                    thumbSaved = true;
                }

                if (_stopRequested) break;
                if (!screenMode && !NativeMethods.IsWindow(hwnd)) break; // 目标窗口已关闭：保存已录内容

                long now;
                while ((now = sw.ElapsedTicks) < next && !_stopRequested)
                    Thread.Sleep(1);
                next += period;
                if (next <= now) next = now + 1; // 落后过多则重置基准，避免追帧风暴
            }

            saved = !_discardRequested;
        }
        catch (Exception ex)
        {
            // 主异常单独记录（编码收尾的连锁异常不应把它吞掉）
            Program.LogError("录屏", ex);
            ReportError($"录屏失败：{ex.Message}");
            saved = false;
        }
        finally
        {
            // 音频收尾三段式：drain 现有包 → 停采集线程（内部先追平墙钟并继续入队尾包）
            // → 再 drain 尾包，保证音轨与视频同时长结束
            if (audio != null)
            {
                try
                {
                    if (encoder != null)
                        while (audio.TryDequeue(out var pkt))
                            encoder.WriteAudioSample(pkt.Data, pkt.Ts100ns, pkt.Dur100ns);
                }
                catch (Exception ex)
                {
                    Program.LogError("录屏音频收尾", ex);
                }
                try { audio.Dispose(); }
                catch (Exception ex) { Program.LogError("录屏音频释放", ex); }
                try
                {
                    if (encoder != null)
                        while (audio.TryDequeue(out var pkt))
                            encoder.WriteAudioSample(pkt.Data, pkt.Ts100ns, pkt.Dur100ns);
                }
                catch (Exception ex)
                {
                    Program.LogError("录屏音频尾包", ex);
                }
            }

            // 编码收尾：即使失败也不能让异常传播（会替换上面的主异常）
            if (encoder != null)
            {
                try { encoder.Dispose(); }
                catch (Exception dex) { Program.LogError("录屏收尾", dex); }
            }

            // 释放临时画布与主画布的 GDI 资源
            if (oldTemp != IntPtr.Zero) NativeMethods.SelectObject(tempDc, oldTemp);
            if (tempBmp != IntPtr.Zero) NativeMethods.DeleteObject(tempBmp);
            if (tempDc != IntPtr.Zero) NativeMethods.DeleteDC(tempDc);
            if (oldCanvas != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldCanvas);
            if (canvas != IntPtr.Zero) NativeMethods.DeleteObject(canvas);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

            if (comInited) NativeMethods.CoUninitialize();
            NativeMethods.timeEndPeriod(1);

            if (saved)
            {
                try
                {
                    _history.Add(path, thumbPath, title,
                        (int)Math.Round(_clock.Elapsed.TotalSeconds), width, height);
                }
                catch (Exception ex)
                {
                    Program.LogError("录屏历史登记", ex);
                }
            }
            else
            {
                // 丢弃：清理半成品与缩略图
                TryDeleteFile(path);
                if (thumbPath != null) TryDeleteFile(thumbPath);
            }

            _clock.Stop();
            _paused = false;
            _pauseRequested = false;
            IsRecording = false;
            FireOnUi(() => Stopped?.Invoke(saved));
        }
    }

    /// <summary>取窗口实时物理矩形（优先 DWM 扩展边界，排除不可见阴影）</summary>
    private static NativeMethods.RECT GetLiveRect(IntPtr hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var r, Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && r.Right > r.Left && r.Bottom > r.Top)
            return r;
        NativeMethods.GetWindowRect(hwnd, out r);
        return r;
    }

    /// <summary>估算 H.264 平均码率（约 0.08bpp，800kbps~12Mbps）；配置显式指定则优先</summary>
    private int ResolveBitrate(int w, int h, int fps)
    {
        int cfgKbps = _config.Config.Recording.BitrateKbps;
        if (cfgKbps > 0) return cfgKbps * 1000;
        long bps = (long)w * h * fps * 8 / 100;
        return (int)Math.Clamp(bps, 800_000, 12_000_000);
    }

    /// <summary>AAC 平均码率：立体声 128kbps / 单声道 96kbps</summary>
    private static int ResolveAudioBitrate(int channels) => channels >= 2 ? 128_000 : 96_000;

    /// <summary>把首帧保存为缩略图（与 MP4 同名 .png，宽度收窄到卡片 2 倍）</summary>
    private static string? SaveThumb(byte[] bgra, int w, int h, int stride, string mp4Path)
    {
        try
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
            double scale = Math.Min(1.0, 432.0 / w);
            BitmapSource thumb = bmp;
            if (scale < 1.0)
            {
                var tb = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                tb.Freeze();
                thumb = tb;
            }

            var path = Path.ChangeExtension(mp4Path, ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumb));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
            return path;
        }
        catch (Exception ex)
        {
            Program.LogError("录屏缩略图", ex);
            return null;
        }
    }

    private static void FireOnUi(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.BeginInvoke(action);
            else
                action();
        }
        catch
        {
            // 应用关闭期间调度失败可忽略
        }
    }

    private void ReportError(string message) => FireOnUi(() => Error?.Invoke(message));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
