using System.IO;
using System.Text.Json;

namespace SnipPin.Core.Configuration;

/// <summary>应用配置根对象（与 config.json 对应）</summary>
public class AppConfig
{
    public GeneralConfig General { get; set; } = new();
    public HotkeyConfig Hotkeys { get; set; } = new();
    public SaveConfig Save { get; set; } = new();
    public CaptureConfig Capture { get; set; } = new();
    public PinConfig Pin { get; set; } = new();
    public HistoryConfig History { get; set; } = new();
    public ClipboardConfig Clipboard { get; set; } = new();
    public RecordingConfig Recording { get; set; } = new();
}

public class GeneralConfig
{
    /// <summary>开机自启</summary>
    public bool AutoStart { get; set; } = false;
    /// <summary>语言</summary>
    public string Language { get; set; } = "zh-CN";
    /// <summary>主题：system / light / dark</summary>
    public string Theme { get; set; } = "system";
}

public class HotkeyConfig
{
    /// <summary>区域截图</summary>
    public string Capture { get; set; } = "F1";
    /// <summary>钉住剪贴板图片</summary>
    public string PinClipboard { get; set; } = "F2";
    /// <summary>全屏截图</summary>
    public string FullScreen { get; set; } = "F3";
    /// <summary>显示/隐藏所有贴图</summary>
    public string TogglePins { get; set; } = "F4";
    /// <summary>开始/停止录制</summary>
    public string RecordToggle { get; set; } = "Ctrl+Alt+R";
    /// <summary>暂停/恢复录制</summary>
    public string RecordPause { get; set; } = "Ctrl+Alt+P";
    /// <summary>截图编辑时直接钉住（窗口内快捷键，非全局热键）</summary>
    public string PinInCapture { get; set; } = "Ctrl+P";
}

public class SaveConfig
{
    /// <summary>默认保存目录</summary>
    public string Directory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnipPin");
    /// <summary>命名模板，支持 {yyyy-MM-dd} {HH-mm-ss} {seq} 占位符</summary>
    public string NameTemplate { get; set; } = "截图_{yyyy-MM-dd}_{HH-mm-ss}";
    /// <summary>默认格式 png / jpg / bmp</summary>
    public string Format { get; set; } = "png";
    /// <summary>JPG 质量 1-100</summary>
    public int JpgQuality { get; set; } = 90;
}

public class CaptureConfig
{
    /// <summary>智能窗口识别吸附</summary>
    public bool WindowSnap { get; set; } = true;
    /// <summary>放大镜</summary>
    public bool Magnifier { get; set; } = true;
    /// <summary>遮罩透明度 0-1</summary>
    public double MaskOpacity { get; set; } = 0.4;
}

public class PinConfig
{
    /// <summary>默认始终置顶</summary>
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>默认透明度</summary>
    public double DefaultOpacity { get; set; } = 1.0;
    /// <summary>关闭时是否保留贴图会话（下次启动恢复）</summary>
    public bool RestoreSession { get; set; } = true;
}

public class HistoryConfig
{
    /// <summary>历史数据目录（图片 + 索引 + 归档压缩包）</summary>
    public string Directory { get; set; } =
        Path.Combine(Program.DataDir, "history");
    /// <summary>活跃条目上限：超过后最旧的按月归档到 archive_YYYY-MM.zip（不删除）；设为 0 表示永不归档</summary>
    public int ActiveLimit { get; set; } = 500;
    /// <summary>归档缓冲条数：超上限后先攒这么多条，攒满一次性批量归档（0 = 每超 1 条立即归档）</summary>
    public int ArchiveBuffer { get; set; } = 10;
}

public class ClipboardConfig
{
    /// <summary>是否启用剪贴板记录</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>弹出剪贴板历史的热键</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+V";
    /// <summary>活跃条目上限：超过后最旧的归档到压缩包（不删除）；设为 0 表示永不归档</summary>
    public int MaxEntries { get; set; } = 500;
    /// <summary>剪贴板数据目录（索引 + 图片 + 归档压缩包）</summary>
    public string Directory { get; set; } =
        Path.Combine(Program.DataDir, "clipboard");
}

public class RecordingConfig
{
    /// <summary>录屏数据目录（MP4 + 缩略图 + 索引）</summary>
    public string Directory { get; set; } =
        Path.Combine(Program.DataDir, "recordings");
    /// <summary>目标帧率（5-60，超出范围自动收敛）</summary>
    public int Fps { get; set; } = 30;
    /// <summary>H.264 平均码率（kbps）；0 = 按分辨率自动估算</summary>
    public int BitrateKbps { get; set; } = 0;
    /// <summary>是否把鼠标光标画进录屏画面</summary>
    public bool DrawCursor { get; set; } = true;
    /// <summary>录屏音源：none=不录声音 / system=系统声音 / microphone=麦克风</summary>
    public string AudioSource { get; set; } = "system";
    /// <summary>活跃条目上限：超过后最旧的按月归档到 archive_YYYY-MM.zip（不删除）；设为 0 表示永不归档</summary>
    public int ActiveLimit { get; set; } = 50;
    /// <summary>归档缓冲条数：超上限后先攒这么多条，攒满一次性批量归档（0 = 每超 1 条立即归档）</summary>
    public int ArchiveBuffer { get; set; } = 5;
}

/// <summary>
/// 配置服务：负责加载/保存 config.json。
/// 通过单例注入，业务层不直接读写文件。
/// </summary>
public class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(Program.DataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private ConfigService() { }

    /// <summary>当前配置实例</summary>
    public AppConfig Config { get; private set; } = new();

    /// <summary>加载配置；不存在或损坏则使用默认值</summary>
    public static ConfigService Load()
    {
        var svc = new ConfigService();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                svc.Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                             ?? new AppConfig();
            }
        }
        catch
        {
            // 配置损坏时回退默认值，避免启动崩溃
            svc.Config = new AppConfig();
        }
        return svc;
    }

    /// <summary>持久化到磁盘</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Program.DataDir);
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 保存失败不致命，仅记录（可接日志）
        }
    }
}
