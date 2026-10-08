using System.Runtime.InteropServices;

namespace SnipPin.Core.Native;

// ---------------- WASAPI 互操作（录屏音频采集：系统声音 loopback / 麦克风） ----------------
// 接口方法顺序与 GUID 均已对照 SDK 26100 mmdeviceapi.h / audioclient.h 核实。

/// <summary>音频端点枚举器（IID 已对照 SDK 26100 mmdeviceapi.h 核实）</summary>
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    // 注册/注销设备变更回调：本应用不使用，仅保 vtable 槽位
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

/// <summary>音频端点设备（IID 已对照 SDK 26100 mmdeviceapi.h 核实）</summary>
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IAudioClient iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr props);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

/// <summary>音频客户端：初始化采集/渲染会话（IID 与方法顺序已对照 SDK 26100 audioclient.h 核实）</summary>
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
        IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, out IAudioCaptureClient service);
}

/// <summary>采集客户端：按包读取音频数据（IID 已对照 SDK 26100 audioclient.h 核实）。
/// SDK 26100 起尾部新增 GetFrequency，本应用不调用故不声明（前 3 槽不受影响）。</summary>
[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint framesRead);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

/// <summary>音频波形格式（共享模式混音格式；布局对照 SDK 26100 mmreg.h）</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    /// <summary>标准 PCM（整数样本）</summary>
    public const ushort WAVE_FORMAT_PCM = 0x0001;
    /// <summary>IEEE float 样本（共享模式混音格式的常见形态）</summary>
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    /// <summary>可扩展格式：真实格式在 cbSize 后的 SubFormat GUID</summary>
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
}

/// <summary>WASAPI 常量与创建函数（GUID 与数值已对照 SDK 26100 核实）</summary>
internal static class Wasapi
{
    internal static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    internal static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    internal static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    /// <summary>EDataFlow.eRender（渲染端点 = 扬声器，loopback 采集用）</summary>
    internal const int eRender = 0;
    /// <summary>EDataFlow.eCapture（捕获端点 = 麦克风）</summary>
    internal const int eCapture = 1;
    /// <summary>ERole.eConsole（默认多媒体角色）</summary>
    internal const int eConsole = 0;

    /// <summary>AUDCLNT_SHAREMODE_SHARED（共享模式，loopback 仅支持共享）</summary>
    internal const int AUDCLNT_SHAREMODE_SHARED = 0;
    /// <summary>把渲染端点初始化为 loopback 采集（系统声音）</summary>
    internal const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    /// <summary>缓冲区为静音（数据无效，应写零）</summary>
    internal const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    /// <summary>CLSCTX_ALL（Activate 激活进程内/本地/远程服务器）</summary>
    internal const uint CLSCTX_ALL = 0x17;

    [DllImport("ole32.dll", PreserveSig = true)]
    internal static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsContext,
        ref Guid iid, out IMMDeviceEnumerator obj);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>HRESULT 失败时抛出带上下文的异常（与 MediaFoundation.Check 同风格）</summary>
    internal static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} 失败（hr=0x{hr:X8}）");
    }
}
