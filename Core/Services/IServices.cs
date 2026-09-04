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
