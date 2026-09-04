using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using SnipPin.Core.Configuration;

namespace SnipPin.Core.Services;

/// <summary>
/// 贴图服务：创建并管理所有悬浮贴图窗（PinWindow），并负责贴图会话持久化。
/// 会话目录：%AppData%\SnipPin\session（图片 + session.json）。
/// </summary>
public class PinService : IPinService
{
    private readonly ConfigService _config;
    private readonly HistoryService _history; // 贴图标注完成时生成截图历史
    private readonly List<PinWindow> _pins = new();

    private static readonly string SessionDir = Path.Combine(Program.DataDir, "session");
    private static readonly string SessionFile = Path.Combine(SessionDir, "session.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PinService(ConfigService config, HistoryService history)
    {
        _config = config;
        _history = history;
    }

    public IReadOnlyList<PinWindow> Pins => _pins;

    public void Pin(BitmapSource image, Point? position = null)
    {
        var win = new PinWindow(image, _config.Config.Pin, _history);

        if (position.HasValue)
        {
            win.Left = position.Value.X;
            win.Top = position.Value.Y;
        }

        win.Closed += (_, _) => _pins.Remove(win);
        _pins.Add(win);
        win.Show();
    }

    public void CloseAll()
    {
        // 先复制列表，避免 Closed 回调修改集合
        foreach (var win in _pins.ToArray())
            win.Close();
        _pins.Clear();
        // 用户主动清空视为放弃会话
        PersistSession();
    }

    public void ToggleAll()
    {
        // 若有任意一个可见，则全部隐藏；否则全部显示
        bool anyVisible = _pins.Any(p => p.IsVisible);
        foreach (var win in _pins)
        {
            if (anyVisible) win.Hide();
            else win.Show();
        }
    }

    // ---------------- 会话持久化 ----------------
    public void RestoreSession()
    {
        if (!_config.Config.Pin.RestoreSession || !File.Exists(SessionFile))
            return;

        try
        {
            var states = JsonSerializer.Deserialize<List<PinState>>(File.ReadAllText(SessionFile));
            if (states == null) return;

            foreach (var st in states)
            {
                var path = Path.Combine(SessionDir, st.ImageFile);
                if (!File.Exists(path)) continue;

                // 从文件加载（OnLoad 立即解码，避免文件被锁）
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.EndInit();
                img.Freeze();

                var win = new PinWindow(img, _config.Config.Pin, _history);
                win.RestoreState(st);
                win.Left = st.Left;
                win.Top = st.Top;
                win.Closed += (_, _) => _pins.Remove(win);
                _pins.Add(win);
                win.Show();
            }
        }
        catch
        {
            // 会话损坏则放弃恢复（不阻断启动）
        }
    }

    public void PersistSession()
    {
        try
        {
            // 重建会话目录
            if (Directory.Exists(SessionDir))
                Directory.Delete(SessionDir, true);

            // 用户关闭了"保留会话"则只清理不写入
            if (!_config.Config.Pin.RestoreSession)
                return;

            Directory.CreateDirectory(SessionDir);

            var states = new List<PinState>();
            for (int i = 0; i < _pins.Count; i++)
            {
                var win = _pins[i];
                var file = $"pin_{i}.png";
                // 保存合成后的图片（含标注），确保标注随会话保留
                if (!SavePng(win.GetCompositedImage(), Path.Combine(SessionDir, file)))
                    continue;

                states.Add(new PinState
                {
                    Left = win.Left,
                    Top = win.Top,
                    Scale = win.CurrentScale,
                    Rotation = win.CurrentRotation,
                    Opacity = win.Opacity,
                    AlwaysOnTop = win.Topmost,
                    ImageFile = file,
                });
            }

            File.WriteAllText(SessionFile, JsonSerializer.Serialize(states, JsonOptions));
        }
        catch
        {
            // 持久化失败不致命
        }
    }

    /// <summary>保存 PNG 到指定路径</summary>
    private static bool SavePng(BitmapSource image, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
