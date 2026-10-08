using System.Runtime.InteropServices;

namespace SnipPin.Core.Native;

// ---------------- COM 接口（vtable 顺序严格对齐 SDK，不可调整） ----------------

/// <summary>属性集合接口（IMFMediaType / IMFSample 的基接口）。
/// 方法顺序已对照 SDK 26100 mfobjects.h 的 IMFAttributesVtbl C 结构核实（30 个虚方法）。</summary>
[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    // ---- 1-15 读取方法：本应用未使用，仅按 SDK 顺序占据 vtable 槽位 ----
    [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(ref Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64(ref Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(ref Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbBlobSize);
    [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppUnk);
    // ---- 16-24：设置方法 ----
    [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64(ref Guid guidKey, long unValue);
    [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, int cbBufSize);
    // ---- 25-30：未使用，占位 ----
    [PreserveSig] int SetUnknown(ref Guid guidKey, IntPtr pUnk);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

/// <summary>媒体类型（仅使用继承自 IMFAttributes 的方法）（IID 已对照 SDK 26100 mfobjects.h 核实）</summary>
[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
}

/// <summary>内存媒体缓冲区（IID 已对照 SDK 26100 mfobjects.h 核实）</summary>
[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(int cbCurrentLength);
    [PreserveSig] int GetMaxLength(out int pcbMaxLength);
}

/// <summary>媒体采样（一帧的容器）。
/// 注意：采用扁平声明（不使用托管继承）——实测 .NET 对 InterfaceIsIUnknown 继承接口
/// 的自有方法槽位计算会错位，须将基接口 IMFAttributes 的 30 个方法与本接口 14 个方法
/// 按真实 vtable 线性排列（已对照 SDK 26100 IMFSampleVtbl 核实，共 47 槽）。</summary>
[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // ---- IMFAttributes 基类段（槽 4-33）----
    [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(ref Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64(ref Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(ref Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbBlobSize);
    [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppUnk);
    [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64(ref Guid guidKey, long unValue);
    [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, int cbBufSize);
    [PreserveSig] int SetUnknown(ref Guid guidKey, IntPtr pUnk);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    // ---- IMFSample 自有段（槽 34-47，本应用实际使用的部分）----
    [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(int dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long hnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long hnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out int cBuffers);
    [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex(int dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
}

/// <summary>Sink Writer：逐帧写入并编码为 MP4 文件（IID 与方法顺序已对照 SDK 26100 mfreadwrite.h 核实）</summary>
[ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out int pdwStreamIndex);
    [PreserveSig] int SetInputMediaType(int dwStreamIndex, IMFMediaType pType, IMFAttributes? pEncodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int dwStreamIndex, IMFSample pSample);
    [PreserveSig] int SendStreamTick(int dwStreamIndex, long llTimestamp);
    /// <summary>占位：SinkWriter 自 Win8 起在 SendStreamTick 与 NotifyEndOfSegment 之间插入的方法，仅保 vtable 槽位</summary>
    [PreserveSig] int PlaceMarker(int dwStreamIndex, long llTickTimestamp, long llSequenceNumber, IntPtr pContext);
    [PreserveSig] int NotifyEndOfSegment(int dwStreamIndex);
    [PreserveSig] int Flush(int dwStreamIndex);
    /// <summary>SDK 原名 Finalize，改名避免与 object.Finalize 混淆（vtable 位置不受名称影响）</summary>
    [PreserveSig] int FinalizeWrite();
}

/// <summary>
/// Media Foundation 最小互操作面：仅本应用录屏编码所需
/// （创建函数 + 常量 GUID + HRESULT 检查）。
/// </summary>
internal static class MediaFoundation
{
    internal const int MF_VERSION = 0x20070;

    // ---------- 属性键（GUID 已对照 SDK 26100 mfapi.h / mfreadwrite.idl 核实） ----------
    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    // ---------- 音频属性键（GUID 已对照 SDK 26100 mfapi.h 核实） ----------
    internal static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    internal static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    internal static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    internal static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    internal static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    // ---------- 主类型 / 子类型 ----------
    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");   // WAVE_FORMAT_MPEG_HEAAC
    internal static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71"); // WAVE_FORMAT_IEEE_FLOAT

    /// <summary>MFVideoInterlace_Progressive（逐行扫描）</summary>
    internal const int MFVideoInterlace_Progressive = 2;

    // ---------- 创建函数 ----------
    [DllImport("mfplat.dll")]
    internal static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, int cInitialSize);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateMediaType(out IMFMediaType ppMFMediaType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    internal static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
        IntPtr pByteStream,
        IMFAttributes pAttributes,
        out IMFSinkWriter ppSinkWriter);

    // ---------- 辅助 ----------
    /// <summary>HRESULT 失败时抛出带上下文的异常</summary>
    internal static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} 失败（hr=0x{hr:X8}）");
    }

    /// <summary>设置 UINT32 属性（如码率/扫描模式/行距）</summary>
    internal static void SetU32(this IMFAttributes attrs, in Guid key, int value)
    {
        var k = key;
        Check(attrs.SetUINT32(ref k, value), "SetUINT32");
    }

    /// <summary>设置 UINT64 属性（如帧率/尺寸/宽高比打包值）</summary>
    internal static void SetU64(this IMFAttributes attrs, in Guid key, long value)
    {
        var k = key;
        Check(attrs.SetUINT64(ref k, value), "SetUINT64");
    }

    internal static void SetGuid(this IMFAttributes attrs, in Guid key, in Guid value)
    {
        var k = key;
        var v = value;
        Check(attrs.SetGUID(ref k, ref v), "SetGUID");
    }
}
