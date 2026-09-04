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
        // 1. 逐屏捕获：每屏单独抓物理像素并按该屏 DPI 打包（Per-Monitor 适配的关键）
        var snaps = ScreenCapturer.CapturePerScreen();
        if (snaps.Count == 0) return;

        // 合成一张整屏物理像素图，用于框选后裁剪输出（清晰、不缩放）
        var fullPhysical = ScreenCapturer.ComposeFullPhysical(snaps);
        var (vx, vy, _, _) = Native.NativeMethods.GetVirtualScreen();

        // 2. 每屏各弹一个 overlay 遮罩（各自落在所在屏、用所在屏缩放），任一屏框选完成即汇总
        var overlays = new List<CaptureOverlayWindow>();
        bool handled = false;

        foreach (var snap in snaps)
        {
            var dipBounds = new Rect(
                snap.Origin.X / snap.Scale, snap.Origin.Y / snap.Scale,
                snap.Bitmap.PixelWidth / snap.Scale, snap.Bitmap.PixelHeight / snap.Scale);

            var overlay = new CaptureOverlayWindow(
                snap.Bitmap, dipBounds, snap.Scale,
                _config.Config.Capture.MaskOpacity,
                _config.Config.Capture.WindowSnap);

            overlay.RegionSelected += virtDip =>
            {
                if (handled) return;
                handled = true;

                // 虚拟屏 DIP → 物理像素（乘以该屏缩放系数）
                double s = overlay.Scale;
                var local = new Int32Rect(
                    (int)Math.Round(virtDip.X * s) - vx,
                    (int)Math.Round(virtDip.Y * s) - vy,
                    (int)Math.Round(virtDip.Width * s),
                    (int)Math.Round(virtDip.Height * s));

                var cropped = ScreenCapturer.Crop(fullPhysical, local);
                CloseAll();

                // 3. 打开标注编辑器（编辑完成后由用户选择输出方式），位置用物理像素
                OpenEditor(cropped, new Point(virtDip.X * s, virtDip.Y * s));
                _logger.LogInformation("区域截图完成：{W}x{H}", local.Width, local.Height);
            };

            overlay.Cancelled += () =>
            {
                if (handled) return;
                handled = true;
                CloseAll();
                _logger.LogInformation("截图已取消");
            };

            overlays.Add(overlay);
        }

        foreach (var o in overlays) o.ShowOverlay();

        void CloseAll()
        {
            foreach (var o in overlays)
            {
                try { o.Close(); } catch { /* 已关闭则忽略 */ }
            }
        }
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
