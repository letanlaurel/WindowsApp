using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using RapidOcrNet;
using SkiaSharp;
using SnipPin.Ocr;

namespace SnipPin.Core.Services;

/// <summary>
/// OCR 文字识别服务：RapidOcrNet + PP-OCRv5 mobile 中文模型，本地离线推理。
/// 模型随程序分发（exe 旁 models\zh 四件套），首次识别时懒加载引擎（约 300ms），
/// 应用启动零开销；识别全程在后台线程，完成后弹出可编辑结果浮窗。
/// </summary>
public class OcrService : IOcrService
{
    private readonly ILogger<OcrService> _logger;
    private readonly object _lock = new();
    private RapidOcr? _engine;   // 懒加载引擎（首次识别时初始化）
    private bool _initFailed;    // 初始化失败标记（避免每次调用都重试拖慢）

    /// <summary>模型目录（开发时 bin 旁、发布后 exe 旁的 models\zh）</summary>
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models", "zh");

    private const string DetFile = "ch_PP-OCRv5_det_mobile.onnx";
    private const string ClsFile = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";
    private const string RecFile = "ch_PP-OCRv5_rec_mobile.onnx";
    private const string KeysFile = "ppocrv5_dict.txt";

    public OcrService(ILogger<OcrService> logger) => _logger = logger;

    public bool IsAvailable => File.Exists(Path.Combine(ModelDir, DetFile)) &&
                               File.Exists(Path.Combine(ModelDir, ClsFile)) &&
                               File.Exists(Path.Combine(ModelDir, RecFile)) &&
                               File.Exists(Path.Combine(ModelDir, KeysFile));

    public async void ExtractAndShow(BitmapSource image)
    {
        var win = new OcrResultWindow();
        win.Show();
        if (!IsAvailable)
        {
            win.ShowError($"缺少 OCR 模型文件（{ModelDir}），请重新安装程序");
            _logger.LogWarning("OCR 模型缺失：{Dir}", ModelDir);
            return;
        }
        try
        {
            var lines = await RecognizeLinesAsync(image);
            win.ShowResult(string.Join(Environment.NewLine, lines.Select(l => l.Text)));
        }
        catch (Exception ex)
        {
            Program.LogError("OCR", ex);
            win.ShowError(ex.Message);
        }
    }

    /// <summary>识别并返回文本行（含逐字框，源图像素坐标，供图上按字选择）</summary>
    public async Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(BitmapSource image)
    {
        // PNG 编码须在 UI 线程完成（位图可能未 Freeze，跨线程访问不安全）
        var png = EncodePng(image);
        return await Task.Run(() =>
        {
            var engine = EnsureEngine();
            using var bmp = SKBitmap.Decode(png);
            if (bmp == null) throw new InvalidOperationException("图像解码失败");

            var result = engine.Detect(bmp, MakeOptions(bmp.Width, bmp.Height));
            var lines = new List<OcrTextLine>(result.TextBlocks.Count());
            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrEmpty(block.Text) || block.BoxPoints == null) continue;
                lines.Add(ToLine(block));
            }

            // 低分行二次识别（小字行在整图缩放后送入识别器分辨率不足）
            RetryLowScoreLines(engine, bmp, lines);

            _logger.LogInformation("OCR 完成：{Lines} 行", lines.Count);
            return (IReadOnlyList<OcrTextLine>)lines;
        });
    }

    /// <summary>一次识别参数：PythonCompat(原始分辨率)+50px 白边；LimitSideLen 按图自适应（实测见 OcrPoC）：
    /// 小/中图(长边≤1200) 736（会放大小图，利于 det）；大图(>1200) 1600（少缩放保住小字）</summary>
    private static RapidOcrOptions MakeOptions(int width, int height) => RapidOcrOptions.PythonCompat with
    {
        Padding = 50,
        LimitSideLen = Math.Max(width, height) > 1200 ? 1600 : 736,
        ReturnWordBox = true,
        ReturnSingleCharBox = true,
    };

    /// <summary>二次识别的行置信度阈值（低于此值触发裁剪放大重识别）</summary>
    private const float RetryScoreThreshold = 0.75f;

    /// <summary>
    /// 低分行二次识别：整图缩放后小字行送入识别器的分辨率不足，识别置信度低甚至错字
    /// （实测 12px「单聊」整图识别成「雌真」0.52）。裁剪该行外扩区域放大到行高 ~72px 重跑，
    /// 新分数明显更高才替换（同区域实测 → 「单聊」1.00）。
    /// </summary>
    private static void RetryLowScoreLines(RapidOcr engine, SKBitmap src, List<OcrTextLine> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Score >= RetryScoreThreshold) continue;

            // 裁剪框：外扩 20% 宽 + 40% 高，钳制在图内
            var b = line.Box;
            double padX = b.Width * 0.2 + 8, padY = b.Height * 0.4 + 4;
            int x1 = Math.Max(0, (int)Math.Floor(b.X - padX));
            int y1 = Math.Max(0, (int)Math.Floor(b.Y - padY));
            int x2 = Math.Min(src.Width, (int)Math.Ceiling(b.Right + padX));
            int y2 = Math.Min(src.Height, (int)Math.Ceiling(b.Bottom + padY));
            if (x2 - x1 < 4 || y2 - y1 < 4) continue;

            using var subset = new SKBitmap();
            if (!src.ExtractSubset(subset, new SKRectI(x1, y1, x2, y2)) || subset.Width == 0)
                continue;

            // 放大至行高 ~72px（限 2.5~5 倍；2 倍实测不够），经 PNG 中转保持与主路径一致
            double scale = Math.Clamp(72.0 / Math.Max(1, b.Height), 2.5, 5.0);
            int w2 = (int)(subset.Width * scale), h2 = (int)(subset.Height * scale);
            using var scaled = RenderScaled(subset, w2, h2);
            if (scaled == null) continue;

            // 二次识别参数：小图已放大，LimitSideLen 用 400（实测 86ms/行 且「单聊」1.00；
            // 736 会把小图再放大到 900ms/行 且识别失败）
            var retry = engine.Detect(scaled, RapidOcrOptions.PythonCompat with
            {
                Padding = 50,
                LimitSideLen = 400,
                ReturnWordBox = true,
                ReturnSingleCharBox = true,
            });

            // 取与原行中心距离最近的结果行（裁剪区可能含相邻文字）
            var origCenter = new System.Windows.Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            OcrTextLine? best = null;
            double bestDist = double.MaxValue;
            foreach (var block in retry.TextBlocks)
            {
                if (string.IsNullOrEmpty(block.Text) || block.BoxPoints == null) continue;
                var cand = ToLine(block);
                var center = new System.Windows.Point(
                    x1 + (cand.Box.X + cand.Box.Width / 2) / scale,
                    y1 + (cand.Box.Y + cand.Box.Height / 2) / scale);
                double dist = (center - origCenter).Length;
                if (dist < bestDist) { bestDist = dist; best = cand; }
            }
            // 新分数须高出 0.05 以上才替换，防止波动导致换错
            if (best == null || best.Score <= line.Score + 0.05f) continue;

            // 坐标映射回原图：cropOrigin + 放大图坐标 / scale
            System.Windows.Rect Map(System.Windows.Rect r) => new(
                x1 + r.X / scale, y1 + r.Y / scale, r.Width / scale, r.Height / scale);
            lines[i] = best with
            {
                Box = Map(best.Box),
                Words = best.Words?.Select(w => w with { Box = Map(w.Box) }).ToList(),
            };
        }
    }

    /// <summary>位图放大（SKSurface 绘制缩放 → PNG 中转 → 解码，与主识别路径一致）</summary>
    private static SKBitmap? RenderScaled(SKBitmap src, int w, int h)
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            surface.Canvas.DrawBitmap(src, new SKRect(0, 0, w, h));
            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            return SKBitmap.Decode(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TextBlock → OcrTextLine（含逐字框与行置信度）</summary>
    private static OcrTextLine ToLine(RapidOcrNet.TextBlock block)
    {
        var words = block.WordResults?
            .Where(w => !string.IsNullOrEmpty(w.Text) && w.BoxPoints != null)
            .Select(w => new OcrWord(w.Text, ToRect(w.BoxPoints)))
            .ToList();
        return new OcrTextLine(block.Text, ToRect(block.BoxPoints), words, block.CharScores?.Average() ?? 1f);
    }

    /// <summary>四角点 → 轴对齐外接矩形（OCR 框基本水平；容错任意顺序）</summary>
    private static System.Windows.Rect ToRect(SKPointI[] pts)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new System.Windows.Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    /// <summary>懒加载引擎（双检锁；初始化失败记录日志并抛出）</summary>
    private RapidOcr EnsureEngine()
    {
        if (_engine != null) return _engine;
        lock (_lock)
        {
            if (_engine != null) return _engine;
            if (_initFailed) throw new InvalidOperationException("OCR 引擎初始化失败（详见 error.log）");
            try
            {
                var engine = new RapidOcr();
                engine.InitModels(
                    detPath: Path.Combine(ModelDir, DetFile),
                    clsPath: Path.Combine(ModelDir, ClsFile),
                    recPath: Path.Combine(ModelDir, RecFile),
                    keysPath: Path.Combine(ModelDir, KeysFile));
                _engine = engine;
                _logger.LogInformation("OCR 引擎初始化完成：{Dir}", ModelDir);
                return _engine;
            }
            catch (Exception ex)
            {
                _initFailed = true; // 初始化失败不再重试，避免每次调用都卡在坏模型上
                Program.LogError("OCR 初始化", ex);
                throw;
            }
        }
    }

    /// <summary>WPF 位图 → PNG 字节（保留全部像素，交给 SkiaSharp 解码）</summary>
    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
