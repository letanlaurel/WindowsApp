using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SnipPin.Core.Annotations;
using SnipPin.Core.Configuration;

namespace SnipPin.Core.Services;

/// <summary>
/// 截图服务：协调一次完整截图流程（遮罩 -> 框选 -> 标注 -> 输出）。
/// </summary>
public class CaptureService : ICaptureService
{
    private readonly IPinService _pinService;
    private readonly IStorageService _storage;
    private readonly ConfigService _config;
    private readonly HistoryService _history;
    private readonly ILogger<CaptureService> _logger;

    public CaptureService(
        IPinService pinService,
        IStorageService storage,
        ConfigService config,
        HistoryService history,
        ILogger<CaptureService> logger)
    {
        _pinService = pinService;
        _storage = storage;
        _config = config;
        _history = history;
        _logger = logger;
    }

    public void StartRegionCapture()
    {
        // 1. 抓取整个虚拟屏幕作为背景
        var screen = ScreenCapturer.CaptureVirtualScreen();
        var (vx, vy, _, _) = Native.NativeMethods.GetVirtualScreen();

        // 2. 弹出框选遮罩窗，用户框选后进入标注编辑
        //    遮罩透明度与窗口吸附开关来自配置
        var overlay = new CaptureOverlayWindow(
            screen, new Point(vx, vy),
            _config.Config.Capture.MaskOpacity,
            _config.Config.Capture.WindowSnap);

        overlay.RegionSelected += rect =>
        {
            // rect 为屏幕坐标（物理像素），换算到位图内坐标
            var local = new Int32Rect(
                (int)(rect.X - vx),
                (int)(rect.Y - vy),
                (int)rect.Width,
                (int)rect.Height);

            var cropped = ScreenCapturer.Crop(screen, local);

            // 3. 打开标注编辑器（编辑完成后由用户选择输出方式）
            OpenEditor(cropped, new Point(rect.X, rect.Y));
            _logger.LogInformation("区域截图完成：{W}x{H} @({X},{Y})", rect.Width, rect.Height, rect.X, rect.Y);
        };

        overlay.Cancelled += () => _logger.LogInformation("截图已取消");
        overlay.ShowOverlay();
    }

    /// <summary>打开标注编辑窗，并接好输出动作</summary>
    private void OpenEditor(BitmapSource cropped, Point screenPos)
    {
        var editor = new AnnotationEditorWindow(cropped)
        {
            Left = screenPos.X,
            Top = screenPos.Y,
        };

        editor.Confirmed += img =>
        {
            _storage.CopyToClipboard(img);
            _history.Add(img, "完成");
            _logger.LogInformation("已复制到剪贴板");
        };
        editor.PinRequested += img =>
        {
            _pinService.Pin(img, screenPos);
            _history.Add(img, "钉住");
        };
        editor.SaveRequested += img =>
        {
            var path = _storage.SaveToFile(img);
            _history.Add(img, "保存", path);
            _logger.LogInformation("已保存：{Path}", path);
        };
        editor.CopyRequested += img => _storage.CopyToClipboard(img);
        editor.Cancelled += () => _logger.LogInformation("标注编辑已取消");

        editor.Show();
    }

    public void CaptureFullScreen()
    {
        var screen = ScreenCapturer.CaptureVirtualScreen();
        _storage.CopyToClipboard(screen);
        _pinService.Pin(screen);
        _history.Add(screen, "全屏");
        _logger.LogInformation("全屏截图完成");
    }

    public void PinFromClipboard()
    {
        if (!Clipboard.ContainsImage())
        {
            _logger.LogInformation("剪贴板中没有图片");
            return;
        }
        var img = Clipboard.GetImage();
        if (img != null)
        {
            img.Freeze();
            _pinService.Pin(img);
        }
    }
}
