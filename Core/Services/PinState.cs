namespace SnipPin.Core.Services;

/// <summary>贴图会话状态（用于退出持久化与下次启动恢复）</summary>
public class PinState
{
    /// <summary>贴图位置</summary>
    public double Left { get; set; }
    public double Top { get; set; }
    /// <summary>缩放系数</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>旋转角度（度，90 步进）</summary>
    public double Rotation { get; set; }
    /// <summary>透明度 0.1-1.0</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>是否始终置顶</summary>
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>会话目录中的图片文件名</summary>
    public string ImageFile { get; set; } = "";
}
