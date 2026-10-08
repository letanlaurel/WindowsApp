using System.Diagnostics;
using System.Runtime.InteropServices;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>
/// MP4（H.264 + 可选 AAC）编码器封装：基于 Media Foundation Sink Writer。
/// 输入 BGRA32 顶向下像素逐帧写入视频流；可选 float32 PCM 音频流。
/// 非线程安全：所有方法必须在创建它的录制线程上调用。
/// </summary>
internal sealed class Mp4Encoder : IDisposable
{
    /// <summary>音频流参数（输入 float32 交错 PCM，编码为 AAC）</summary>
    internal sealed record AudioStreamOptions(int SampleRate, int Channels, int BitrateBps);

    private readonly IMFSinkWriter _writer;
    private readonly int _frameLen;
    private readonly long _nominal100ns;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _last100ns;
    private bool _finalized;
    // 暂停支持：把暂停期间的真实墙钟从时间轴中扣除，视频时长不含暂停段
    private long _pausedTotal100ns;
    private long _pauseStart100ns;
    private readonly int _audioBlockAlign;

    /// <summary>视频流索引（当前恒为 0）</summary>
    internal int StreamIndex { get; }

    /// <summary>音频流索引；无音频流时为 -1</summary>
    internal int AudioStreamIndex { get; }

    /// <summary>当前编码时钟（100ns，已扣除暂停段）：音视频共用的统一时间轴</summary>
    internal long Now100ns => RawClock100ns() - _pausedTotal100ns;

    /// <param name="path">输出 MP4 路径（不能已存在）</param>
    /// <param name="fps">目标帧率</param>
    /// <param name="bitrateBps">平均码率（bit/s）</param>
    /// <param name="audio">音频流参数；null = 仅视频</param>
    public Mp4Encoder(string path, int width, int height, int fps, int bitrateBps, AudioStreamOptions? audio = null)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("H.264 要求宽高均为偶数（调用方负责取整）");

        _frameLen = width * height * 4;
        _nominal100ns = 10_000_000L / Math.Max(fps, 1);

        MediaFoundation.Check(MediaFoundation.MFStartup(MediaFoundation.MF_VERSION, 0), "MFStartup");

        IMFSinkWriter? writer = null;
        try
        {
            // 关闭写入节流：实时录制不需要按时间戳排队延迟
            IMFAttributes attrs = null!;
            MediaFoundation.Check(MediaFoundation.MFCreateAttributes(out attrs, 1), "MFCreateAttributes");
            attrs.SetU32(MediaFoundation.MF_SINK_WRITER_DISABLE_THROTTLING, 1);

            MediaFoundation.Check(
                MediaFoundation.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out writer),
                "创建 MP4 写入器");
            Release(attrs);

            // 输出类型：H.264
            IMFMediaType outType = null!;
            MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out outType), "MFCreateMediaType(输出)");
            outType.SetGuid(MediaFoundation.MF_MT_MAJOR_TYPE, MediaFoundation.MFMediaType_Video);
            outType.SetGuid(MediaFoundation.MF_MT_SUBTYPE, MediaFoundation.MFVideoFormat_H264);
            outType.SetU64(MediaFoundation.MF_MT_FRAME_SIZE, ((long)(uint)width << 32) | (uint)height);
            outType.SetU64(MediaFoundation.MF_MT_FRAME_RATE, ((long)(uint)fps << 32) | 1u);
            outType.SetU64(MediaFoundation.MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1u); // 1:1，MP4 sink 必需
            outType.SetU32(MediaFoundation.MF_MT_INTERLACE_MODE, MediaFoundation.MFVideoInterlace_Progressive);
            outType.SetU32(MediaFoundation.MF_MT_AVG_BITRATE, bitrateBps);
            MediaFoundation.Check(writer.AddStream(outType, out int stream), "AddStream");
            StreamIndex = stream;
            Release(outType);

            // 输入类型：RGB32（与 GDI 的 BGRA 字节序同构，框架自动插入颜色转换器）
            IMFMediaType inType = null!;
            MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out inType), "MFCreateMediaType(输入)");
            inType.SetGuid(MediaFoundation.MF_MT_MAJOR_TYPE, MediaFoundation.MFMediaType_Video);
            inType.SetGuid(MediaFoundation.MF_MT_SUBTYPE, MediaFoundation.MFVideoFormat_RGB32);
            inType.SetU64(MediaFoundation.MF_MT_FRAME_SIZE, ((long)(uint)width << 32) | (uint)height);
            inType.SetU64(MediaFoundation.MF_MT_FRAME_RATE, ((long)(uint)fps << 32) | 1u);
            inType.SetU32(MediaFoundation.MF_MT_INTERLACE_MODE, MediaFoundation.MFVideoInterlace_Progressive);
            inType.SetU32(MediaFoundation.MF_MT_DEFAULT_STRIDE, width * 4);
            MediaFoundation.Check(writer.SetInputMediaType(stream, inType, null), "SetInputMediaType");
            Release(inType);

            // 音频流（可选）：AAC 输出 + float32 PCM 输入（帧内会话完成编码协商）
            if (audio != null)
            {
                if (audio.Channels is not (1 or 2))
                    throw new ArgumentException("AAC 音频流仅支持单声道/立体声", nameof(audio));

                _audioBlockAlign = audio.Channels * 4;
                IMFMediaType aOut = null!;
                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out aOut), "MFCreateMediaType(音频输出)");
                aOut.SetGuid(MediaFoundation.MF_MT_MAJOR_TYPE, MediaFoundation.MFMediaType_Audio);
                aOut.SetGuid(MediaFoundation.MF_MT_SUBTYPE, MediaFoundation.MFAudioFormat_AAC);
                aOut.SetU32(MediaFoundation.MF_MT_AUDIO_SAMPLES_PER_SECOND, audio.SampleRate);
                aOut.SetU32(MediaFoundation.MF_MT_AUDIO_NUM_CHANNELS, audio.Channels);
                aOut.SetU32(MediaFoundation.MF_MT_AVG_BITRATE, audio.BitrateBps);
                MediaFoundation.Check(writer.AddStream(aOut, out int audioStream), "AddStream(音频)");
                AudioStreamIndex = audioStream;

                IMFMediaType aIn = null!;
                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out aIn), "MFCreateMediaType(音频输入)");
                aIn.SetGuid(MediaFoundation.MF_MT_MAJOR_TYPE, MediaFoundation.MFMediaType_Audio);
                aIn.SetGuid(MediaFoundation.MF_MT_SUBTYPE, MediaFoundation.MFAudioFormat_Float);
                aIn.SetU32(MediaFoundation.MF_MT_AUDIO_SAMPLES_PER_SECOND, audio.SampleRate);
                aIn.SetU32(MediaFoundation.MF_MT_AUDIO_NUM_CHANNELS, audio.Channels);
                aIn.SetU32(MediaFoundation.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
                aIn.SetU32(MediaFoundation.MF_MT_AUDIO_BLOCK_ALIGNMENT, _audioBlockAlign);
                aIn.SetU32(MediaFoundation.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, audio.SampleRate * _audioBlockAlign);
                MediaFoundation.Check(writer.SetInputMediaType(audioStream, aIn, null), "SetInputMediaType(音频)");
                Release(aIn);
                Release(aOut);
            }
            else
            {
                AudioStreamIndex = -1;
            }

            MediaFoundation.Check(writer.BeginWriting(), "BeginWriting");

            _writer = writer;
            writer = null;
        }
        finally
        {
            if (writer != null) Release(writer);
        }
    }

    /// <summary>写入一帧（BGRA32 顶向下，长度须等于 width*height*4）</summary>
    /// <summary>暂停时间戳时钟：暂停期间不写帧，恢复后时长不虚增</summary>
    public void Pause() => _pauseStart100ns = RawClock100ns();

    /// <summary>恢复时间戳时钟：把暂停时长从时间轴中扣除</summary>
    public void Resume()
    {
        if (_pauseStart100ns != 0)
        {
            _pausedTotal100ns += RawClock100ns() - _pauseStart100ns;
            _pauseStart100ns = 0;
        }
    }

    private long RawClock100ns() => _clock.ElapsedTicks * 10_000_000L / Stopwatch.Frequency;

    public void WriteFrame(byte[] bgra)
    {
        if (bgra.Length != _frameLen)
            throw new ArgumentException("帧数据长度与画布尺寸不符", nameof(bgra));

        IMFMediaBuffer? buffer = null;
        IMFSample? sample = null;
        try
        {
            MediaFoundation.Check(MediaFoundation.MFCreateMemoryBuffer(_frameLen, out buffer), "MFCreateMemoryBuffer");
            MediaFoundation.Check(MediaFoundation.MFCreateSample(out sample), "MFCreateSample");

            buffer.Lock(out IntPtr ptr, out _, out _);
            Marshal.Copy(bgra, 0, ptr, _frameLen);
            buffer.Unlock();
            MediaFoundation.Check(buffer.SetCurrentLength(_frameLen), "SetCurrentLength");

            MediaFoundation.Check(sample.AddBuffer(buffer), "AddBuffer");

            // 按采集墙钟打时间戳：偶发掉帧时仍保持总时长准确
            long now = RawClock100ns() - _pausedTotal100ns;
            long dur = _last100ns == 0 ? _nominal100ns : now - _last100ns;
            if (dur <= 0) dur = _nominal100ns;
            _last100ns = now;

            MediaFoundation.Check(sample.SetSampleTime(now), "SetSampleTime");
            MediaFoundation.Check(sample.SetSampleDuration(dur), "SetSampleDuration");
            MediaFoundation.Check(_writer.WriteSample(StreamIndex, sample), "WriteSample");
        }
        finally
        {
            if (sample != null) Release(sample);
            if (buffer != null) Release(buffer);
        }
    }

    /// <summary>写入一段音频（float32 交错，长度须为块对齐整数倍；时间戳由采集方按统一时钟生成）</summary>
    public void WriteAudioSample(byte[] data, long ts100ns, long dur100ns)
    {
        if (AudioStreamIndex < 0)
            throw new InvalidOperationException("编码器未启用音频流");
        if (data.Length == 0 || data.Length % _audioBlockAlign != 0)
            throw new ArgumentException("音频数据长度须为块对齐整数倍", nameof(data));

        IMFMediaBuffer? buffer = null;
        IMFSample? sample = null;
        try
        {
            MediaFoundation.Check(MediaFoundation.MFCreateMemoryBuffer(data.Length, out buffer), "MFCreateMemoryBuffer(音频)");
            MediaFoundation.Check(MediaFoundation.MFCreateSample(out sample), "MFCreateSample(音频)");

            buffer.Lock(out IntPtr ptr, out _, out _);
            Marshal.Copy(data, 0, ptr, data.Length);
            buffer.Unlock();
            MediaFoundation.Check(buffer.SetCurrentLength(data.Length), "SetCurrentLength(音频)");

            MediaFoundation.Check(sample.AddBuffer(buffer), "AddBuffer(音频)");
            MediaFoundation.Check(sample.SetSampleTime(ts100ns), "SetSampleTime(音频)");
            MediaFoundation.Check(sample.SetSampleDuration(dur100ns), "SetSampleDuration(音频)");
            MediaFoundation.Check(_writer.WriteSample(AudioStreamIndex, sample), "WriteSample(音频)");
        }
        finally
        {
            if (sample != null) Release(sample);
            if (buffer != null) Release(buffer);
        }
    }

    /// <summary>收尾：完成 MP4 索引写入并释放（不抛异常，失败仅记日志）</summary>
    public void Dispose()
    {
        if (_finalized) return;
        _finalized = true;
        try
        {
            int hr = _writer.FinalizeWrite();
            if (hr < 0)
                Program.LogError("录屏编码收尾", $"SinkWriter Finalize 失败（hr=0x{hr:X8}）");
        }
        finally
        {
            Release(_writer);
            MediaFoundation.MFShutdown();
        }
    }

    private static void Release(object comObject)
    {
        try { Marshal.ReleaseComObject(comObject); }
        catch { /* 已释放则忽略 */ }
    }
}
