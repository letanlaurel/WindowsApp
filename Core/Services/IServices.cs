using System.Windows.Media.Imaging;

namespace SnipPin.Core.Services;

/// <summary>全局热键服务</summary>
public interface IHotkeyService
{
    /// <summary>按当前配置注册所有热键（重复调用会先注销旧的）</summary>
    void RegisterAll(IntPtr windowHandle);
    /// <summary>注销全部热键</summary>
    void UnregisterAll();
}

/// <summary>截图服务：触发并协调一次截图流程</summary>
public interface ICaptureService
{
    /// <summary>开始区域截图（弹出遮罩，框选后进入标注）</summary>
    void StartRegionCapture();
    /// <summary>全屏截图</summary>
    void CaptureFullScreen();
    /// <summary>钉住剪贴板中的图片</summary>
    void PinFromClipboard();
}

/// <summary>贴图服务：管理所有悬浮贴图窗</summary>
public interface IPinService
{
    /// <summary>钉住一张图，返回贴图窗</summary>
    void Pin(BitmapSource image, System.Windows.Point? position = null);
    /// <summary>当前所有贴图窗</summary>
    IReadOnlyList<PinWindow> Pins { get; }
    /// <summary>关闭全部贴图</summary>
    void CloseAll();
    /// <summary>显示/隐藏所有贴图</summary>
    void ToggleAll();
    /// <summary>从磁盘恢复上次会话的贴图</summary>
    void RestoreSession();
    /// <summary>把当前贴图持久化到磁盘（退出/关闭全部时调用）</summary>
    void PersistSession();
}

/// <summary>存储服务：保存图片、写剪贴板</summary>
public interface IStorageService
{
    /// <summary>按配置保存图片到本地，返回完整路径</summary>
    string SaveToFile(BitmapSource image);
    /// <summary>复制到剪贴板</summary>
    void CopyToClipboard(BitmapSource image);
}

/// <summary>OCR 识别出的单字/单词（Box 为源图像素坐标的轴对齐外接矩形）</summary>
public sealed record OcrWord(string Text, System.Windows.Rect Box);

/// <summary>OCR 识别出的一行文字（Box 为源图像素坐标；Words 为逐个字框；Score 为行平均置信度）</summary>
public sealed record OcrTextLine(string Text, System.Windows.Rect Box,
    IReadOnlyList<OcrWord>? Words = null, float Score = 1f);

/// <summary>OCR 文字识别服务（本地离线推理，无需联网）</summary>
public interface IOcrService
{
    /// <summary>模型文件是否齐备（缺失时仍可调用，会在结果窗中提示）</summary>
    bool IsAvailable { get; }
    /// <summary>识别图中全部文字并弹出结果浮窗（异步执行，不阻塞调用方）</summary>
    void ExtractAndShow(BitmapSource image);
    /// <summary>识别并返回文本行（含位置，供在图上渲染可交互文字块）</summary>
    Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(BitmapSource image);
}
