using System.Windows.Interop;
using SnipPin.Core.Configuration;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>
/// 全局热键服务：基于 Win32 RegisterHotKey。
/// 由 MainWindow 提供窗口句柄与 WndProc 钩子（WM_HOTKEY）。
/// </summary>
public class HotkeyService : IHotkeyService
{
    private readonly ConfigService _config;
    private IntPtr _hwnd;

    // 热键 ID -> 动作名
    private readonly Dictionary<int, string> _idToAction = new();
    private int _nextId = 1;

    /// <summary>热键触发事件（参数为动作名：capture / pinClipboard / fullScreen / togglePins）</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>热键注册失败（多被其他程序占用），参数为热键文本</summary>
    public event Action<string>? RegistrationFailed;

    public HotkeyService(ConfigService config)
    {
        _config = config;
    }

    public void RegisterAll(IntPtr windowHandle)
    {
        _hwnd = windowHandle;
        UnregisterAll();
        _idToAction.Clear();

        var hk = _config.Config.Hotkeys;
        Register(hk.Capture, "capture");
        Register(hk.PinClipboard, "pinClipboard");
        Register(hk.FullScreen, "fullScreen");
        Register(hk.TogglePins, "togglePins");
    }

    public void UnregisterAll()
    {
        foreach (var id in _idToAction.Keys)
            NativeMethods.UnregisterHotKey(_hwnd, id);
        _idToAction.Clear();
    }

    private void Register(string hotkeyText, string action)
    {
        if (string.IsNullOrWhiteSpace(hotkeyText)) return;
        if (!TryParse(hotkeyText, out var modifiers, out var vk))
            return; // 解析失败则跳过该热键

        int id = _nextId++;
        bool ok = NativeMethods.RegisterHotKey(_hwnd, id, (uint)(modifiers | HotkeyModifiers.NoRepeat), vk);
        if (ok)
        {
            _idToAction[id] = action;
        }
        else
        {
            // 注册失败（多被其他程序占用）：记录日志并通知 UI
            Program.LogError("热键注册", $"{hotkeyText} 注册失败（可能被其他程序占用）");
            RegistrationFailed?.Invoke(hotkeyText);
        }
    }

    /// <summary>
    /// 解析单个按键文本为虚拟键码。
    /// 支持：F1-F24、字母 A-Z、数字 0-9（及 WPF 风格 D0-D9）。
    /// </summary>
    private static bool TryParseKey(string text, out uint vk)
    {
        vk = 0;
        text = text.Trim();

        // 功能键 F1-F24
        if (text.StartsWith("f", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text[1..], out int f) && f >= 1 && f <= 24)
        {
            vk = (uint)(VirtualKeys.F1 + f - 1);
            return true;
        }

        // WPF 风格数字 D0-D9
        if (text.Length == 2 && text[0] is 'D' or 'd' && char.IsDigit(text[1]))
            text = text[1..2];

        // 单个字母 / 数字直接对应虚拟键码（'A'=0x41 '0'=0x30）
        if (text.Length == 1)
        {
            char c = char.ToUpperInvariant(text[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }
        return false;
    }

    /// <summary>由 MainWindow 的 WndProc 转发 WM_HOTKEY 到此</summary>
    public void OnHotkeyMessage(int id)
    {
        if (_idToAction.TryGetValue(id, out var action))
            HotkeyPressed?.Invoke(action);
    }

    /// <summary>
    /// 解析热键文本（如 "F1"、"Ctrl+Shift+F3"、"Ctrl+Alt+A"）为修饰键 + 虚拟键码。
    /// </summary>
    private static bool TryParse(string text, out HotkeyModifiers modifiers, out uint vk)
    {
        modifiers = HotkeyModifiers.None;
        vk = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= HotkeyModifiers.Control; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "win": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (TryParseKey(part, out uint key))
                    {
                        vk = key;
                    }
                    else
                    {
                        return false; // 无法识别的键
                    }
                    break;
            }
        }
        return vk != 0;
    }
}
