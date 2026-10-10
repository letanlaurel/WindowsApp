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
    public const int WM_CLIPBOARDUPDATE = 0x031D;
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

    // ---------- 剪贴板监听 / 鼠标 / 前台窗口 / 按键模拟（剪贴板历史用） ----------
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    /// <summary>获取鼠标物理屏幕坐标</summary>
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>获取当前前台窗口</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>把窗口设为前台（粘贴前恢复焦点用）</summary>
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>获取窗口物理屏幕矩形</summary>
    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>物理像素定位窗口（弹窗跟随鼠标，绕过 DPI 换算）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>keybd_event 按键标志：抬起</summary>
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>虚拟键：左/右 Ctrl 与 Alt（模拟 Ctrl+V 及释放残留修饰键）</summary>
    internal const byte VK_CONTROL = 0x11;
    internal const byte VK_MENU = 0x12;

    /// <summary>模拟一次按键（粘贴用：Ctrl+V）</summary>
    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>获取当前线程 ID（kernel32）</summary>
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    /// <summary>把两个线程的输入状态挂接在一起，可绕过系统的前台锁定限制（恢复粘贴目标焦点用）</summary>
    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>设置键盘焦点窗口（跨线程须先 AttachThreadInput）</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>修改窗口扩展样式（加 WS_EX_NOACTIVATE 用）</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>线程输入状态信息（含焦点/激活/插入符窗口，跨线程只读查询无需挂接）</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public IntPtr Flags, HwndActive, HwndFocus, HwndCapture, HwndMenuOwner, HwndMoveSize, HwndCaret;
        public RECT RcCaret;
    }

    /// <summary>查询指定线程的输入状态（用于记录/验证目标输入框的焦点窗口）</summary>
    [DllImport("user32.dll")]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    // ---------- 低级键盘/鼠标钩子（剪贴板弹窗不夺焦输入，同 Win+V 机制） ----------

    /// <summary>低级键盘钩子：回调跑在安装钩子的线程（须有消息循环），无需注入 DLL</summary>
    internal const int WH_KEYBOARD_LL = 13;
    /// <summary>低级鼠标钩子：检测点击面板外部以关闭</summary>
    internal const int WH_MOUSE_LL = 14;
    /// <summary>钩子事件代码：有输入事件待处理</summary>
    internal const int HC_ACTION = 0;
    /// <summary>低级钩子标志：事件由 SendInput/keybd_event 注入（自己的 Ctrl+V 须放行）</summary>
    internal const uint LLKHF_INJECTED = 0x00000010;

    /// <summary>低级钩子回调签名</summary>
    internal delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>安装低级钩子（hMod 传当前进程模块句柄，线程参数 0 表示全局）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>卸载低级钩子</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>把事件传给钩子链的下一环</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>取当前进程模块句柄（装全局低级钩子用）</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>低级键盘事件数据</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>低级鼠标事件数据</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>键盘消息常量（低级钩子的 wParam）</summary>
    internal const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    /// <summary>鼠标按下消息（含非客户区变体，点外部关面板用）</summary>
    internal const uint WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    internal const uint WM_NCLBUTTONDOWN = 0x00A1, WM_NCRBUTTONDOWN = 0x00A4, WM_NCMBUTTONDOWN = 0x00A7;
    /// <summary>鼠标按下激活消息：返回 MA_NOACTIVATE 让点击不激活窗口</summary>
    internal const uint WM_MOUSEACTIVATE = 0x0021;
    /// <summary>WM_MOUSEACTIVATE 返回值：不激活也不丢弃点击</summary>
    internal const int MA_NOACTIVATE = 3;

    /// <summary>虚拟键（钩子分流 + 按键翻译用；VK_CONTROL/VK_MENU 已在上方定义）</summary>
    internal const byte VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10,
        VK_ESCAPE = 0x1B, VK_HOME = 0x24, VK_END = 0x23,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3,
        VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    /// <summary>查询虚拟键物理按下状态（装钩子时取修饰键初始状态；键被钩子吞掉后不再更新）</summary>
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    /// <summary>取本线程键盘状态快照（翻译按键前按修饰键状态修正）</summary>
    [DllImport("user32.dll")]
    internal static extern bool GetKeyboardState(byte[] lpKeyState);

    /// <summary>取指定线程的键盘布局（按键翻译用目标布局）</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>虚拟键码 → Unicode 字符（窗口线程里调用，勿在钩子回调里调：会改死键状态）</summary>
    [DllImport("user32.dll")]
    internal static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] char[] pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    /// <summary>取坐标下的窗口句柄（判断鼠标点击是否落在本进程窗口上）</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

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

    // ---------- 录屏（窗口捕获 / 光标 / DIB 画布 / 窗口图标） ----------
    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDC(IntPtr hWnd);

    /// <summary>PrintWindow 标志：DWM 完整内容渲染（被遮挡时仍可捕获窗口内容）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>创建 DIB 段：GDI 绘制与内存读取共用同一块像素</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    internal const uint DIB_RGB_COLORS = 0;

    /// <summary>绘制光标（注意：由 user32 而非 gdi32 导出）</summary>
    [DllImport("user32.dll")]
    internal static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    internal const uint DI_NORMAL = 0x0003;

    internal const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")]
    internal static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    internal static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    internal const int GWL_EXSTYLE = -20;
    internal const int GCLP_HICONSM = -34;
    internal const int GCLP_HICON = -14;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;

    internal const uint WM_GETICON = 0x007F;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        /// <summary>调色板占位（32bpp 不使用）</summary>
        public uint bmiColors;
    }

    /// <summary>初始化当前线程 COM（录屏编码线程用）；返回 S_OK(0)/S_FALSE(1)/RPC_E_CHANGED_MODE</summary>
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    internal const uint COINIT_MULTITHREADED = 0x0;

    /// <summary>提高系统定时器精度到 1ms（帧间隔等待用），须与 timeEndPeriod 成对</summary>
    [DllImport("winmm.dll")]
    internal static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    internal static extern uint timeEndPeriod(uint period);

    /// <summary>重设光标坐标（同坐标重设可触发 WM_MOUSEMOVE，用于刷新悬停状态）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    /// <summary>把窗口从屏幕捕获中排除（截图/录屏都拍不到，Win10 2004+）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
}
