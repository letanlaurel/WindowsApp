using System.Runtime.InteropServices;

namespace SnipPin.Core.Native;

/// <summary>虚拟键码（部分）</summary>
public static class VirtualKeys
{
    public const uint F1 = 0x70;
    public const uint F2 = 0x71;
    public const uint F3 = 0x72;
    public const uint F4 = 0x73;
}

/// <summary>RegisterHotKey 修饰键标志</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    /// <summary>避免与已按键重复触发</summary>
    NoRepeat = 0x4000,
}

/// <summary>Win32 消息</summary>
public static class WindowMessages
{
    public const int WM_HOTKEY = 0x0312;
}

/// <summary>
/// user32 / gdi32 的 P/Invoke 封装。
/// 集中放置，便于统一管理 Win32 互操作。
/// </summary>
internal static class NativeMethods
{
    // ---------- 全局热键 ----------
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- 屏幕/设备上下文（屏幕捕获用） ----------
    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    /// <summary>SRCCOPY 光栅操作码</summary>
    internal const int SRCCOPY = 0x00CC0020;

    /// <summary>创建空设备上下文（显示器 DC）</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    /// <summary>将像素从一个 DC 拷贝到另一个 DC（支持跨不同 DPI 显示器的物理像素拷贝）</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest,
        IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, int dwRop);

    // ---------- 系统度量（虚拟屏幕范围） ----------
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>获取跨所有显示器的虚拟屏幕矩形（物理像素）</summary>
    public static (int X, int Y, int Width, int Height) GetVirtualScreen()
    {
        return (
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    // ---------- DPI 感知 ----------
    /// <summary>获取线程当前 DPI 上下文</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetThreadDpiAwarenessContext();

    /// <summary>设置线程 DPI 上下文，返回旧值</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    /// <summary>查询某 DPI 上下文在指定窗口下的 DPI（x/y 相同，返回 x）</summary>
    [DllImport("user32.dll")]
    internal static extern int GetDpiFromDpiAwarenessContext(IntPtr dpiContext);

    // DPI_AWARENESS_CONTEXT 句柄常量
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new(-1);
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new(-2);
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new(-3);
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // ---------- 显示器枚举（逐屏捕获用） ----------
    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>MDT_EFFECTIVE_DPI</summary>
    internal const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>显示器信息：句柄 + 物理像素矩形 + 有效 DPI</summary>
    public readonly record struct MonitorInfo(IntPtr Handle, System.Windows.Rect Bounds, double Dpi);

    /// <summary>枚举所有显示器，返回各自的物理像素边界与 DPI（Per-Monitor 适配基础）</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            double dpi = 96.0;
            if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dx, out uint _) == 0 && dx > 0)
                dpi = dx;
            list.Add(new MonitorInfo(hMon,
                new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), dpi));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---------- 窗口枚举（智能窗口识别用） ----------
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);

    // 同一入口点的 int 输出重载（用于 DWMWA_CLOAKED 等 int 属性）
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);

    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// 枚举当前所有可见的顶层窗口矩形（屏幕物理坐标）。
    /// 排除：本进程窗口、最小化窗口、DWM 隐藏（cloak）窗口、零尺寸窗口。
    /// </summary>
    public static List<System.Windows.Rect> GetVisibleWindowRects()
    {
        var result = new List<System.Windows.Rect>();
        var (vx, vy, vw, vh) = GetVirtualScreen();
        var screen = new System.Windows.Rect(vx, vy, vw, vh);
        uint currentPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == currentPid) return true;

            // DWM 隐藏窗口（如挂起的 UWP）跳过
            if (DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return true;

            // 优先取扩展边界（含不可见阴影修正），失败则退回 GetWindowRect
            RECT r;
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
                return true;

            var rect = new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            if (rect.Width < 8 || rect.Height < 8) return true;
            if (!screen.IntersectsWith(rect)) return true;

            result.Add(rect);
            return true; // 继续枚举
        }, IntPtr.Zero);

        return result;
    }
}
