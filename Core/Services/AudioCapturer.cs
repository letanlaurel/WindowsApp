using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>音频采集来源：系统声音（loopback）/ 麦克风</summary>
internal enum AudioCaptureMode
{
    /// <summary>默认渲染端点（扬声器）loopback：录系统播放的声音</summary>
    System,
    /// <summary>默认捕获端点（麦克风）</summary>
    Microphone,
}

/// <summary>一段已打好时间戳的音频数据（float32 交错，时间戳与视频共用编码时钟）</summary>
internal readonly record struct AudioPacket(byte[] Data, long Ts100ns, long Dur100ns);

/// <summary>
/// WASAPI 音频采集器：独立线程轮询读取默认端点，
/// 输出 float32 交错 PCM 包到线程安全队列（由录制线程取走写入编码器）。
/// 时间戳按采集顺序推进并用注入的编码时钟对齐（暂停段自动跳过、无声期补静音），
/// 保证音轨与视频轨共用同一时间轴。初始化在构造方线程完成（须已 CoInitialize MTA）。
/// </summary>
internal sealed class AudioCapturer : IDisposable
{
    /// <summary>轮询间隔：兼顾包粒度与 CPU 占用（缓冲区 100ms 足以兜住抖动）</summary>
    private const int PollMs = 8;
    /// <summary>无声超过该时长后补静音包，保持音轨连续（部分系统无声时 loopback 不出包）</summary>
    private const long SilenceGap100ns = 40 * 10_000L;
    /// <summary>补静音的单包时长</summary>
    private const long SilenceDur100ns = 20 * 10_000L;

    private readonly Func<long> _clock100ns;
    private readonly ConcurrentQueue<AudioPacket> _queue = new();
    private readonly Thread _thread;
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly IMMDevice _device;
    private readonly IMMDeviceEnumerator _enumerator;

    // 源格式解析结果（输出统一 float32、最多 2 声道，与 AAC 编码器能力对齐）
    private readonly int _srcChannels;
    private readonly int _srcBytesPerSample;
    private readonly bool _srcIsFloat;

    private volatile bool _stopRequested;
    private volatile bool _paused;
    private volatile bool _finishing; // 收尾模式：时间轴追平墙钟后自行退出
    private bool _resumeReset; // 仅采集线程访问：恢复后须重置时间基准
    private long _nextTs;      // 仅采集线程访问：下一个包的写入时间戳

    /// <summary>输出采样率（Hz，来自端点混音格式）</summary>
    internal int SampleRate { get; }
    /// <summary>输出声道数（1 或 2）</summary>
    internal int Channels { get; }
    /// <summary>采集线程异常原因；非空表示音频已中断（视频不受影响）</summary>
    internal string? FaultMessage { get; private set; }

    /// <param name="mode">采集来源</param>
    /// <param name="clock100ns">编码时钟（100ns，已扣除暂停段），供音频包打时间戳对齐视频</param>
    /// <exception cref="InvalidOperationException">无音频设备或初始化失败</exception>
    public AudioCapturer(AudioCaptureMode mode, Func<long> clock100ns)
    {
        _clock100ns = clock100ns;

        var clsid = Wasapi.CLSID_MMDeviceEnumerator;
        var iidEnum = Wasapi.IID_IMMDeviceEnumerator;
        Wasapi.Check(Wasapi.CoCreateInstance(ref clsid, IntPtr.Zero, Wasapi.CLSCTX_ALL,
            ref iidEnum, out _enumerator), "创建音频设备枚举器");

        try
        {
            Wasapi.Check(_enumerator.GetDefaultAudioEndpoint(
                mode == AudioCaptureMode.System ? Wasapi.eRender : Wasapi.eCapture,
                Wasapi.eConsole, out _device), "获取默认音频端点");

            var iidClient = Wasapi.IID_IAudioClient;
            Wasapi.Check(_device.Activate(ref iidClient, Wasapi.CLSCTX_ALL, IntPtr.Zero,
                out _client!), "激活音频客户端");

            Wasapi.Check(_client.GetMixFormat(out IntPtr fmtPtr), "GetMixFormat");
            try
            {
                var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(fmtPtr);
                SampleRate = (int)Math.Clamp(fmt.nSamplesPerSec, 8000, 96000);
                _srcChannels = Math.Max(1, (int)fmt.nChannels);
                Channels = Math.Min(_srcChannels, 2);

                // 判定样本编码：EXTENSIBLE 须读尾部 SubFormat 的 Data1（3=float / 1=PCM）
                ushort tag = fmt.wFormatTag;
                if (tag == WAVEFORMATEX.WAVE_FORMAT_EXTENSIBLE && fmt.cbSize >= 22)
                {
                    int subData1 = Marshal.ReadInt32(fmtPtr, 18 + 2 + 4);
                    tag = (ushort)subData1;
                }
                _srcIsFloat = tag == WAVEFORMATEX.WAVE_FORMAT_IEEE_FLOAT;
                int bits = fmt.wBitsPerSample is 16 or 24 or 32 ? fmt.wBitsPerSample : 0;
                if (bits == 0 || (!(_srcIsFloat || tag == WAVEFORMATEX.WAVE_FORMAT_PCM)))
                    throw new InvalidOperationException($"不支持的音频格式（tag=0x{fmt.wFormatTag:X4}, bits={fmt.wBitsPerSample}）");
                _srcBytesPerSample = bits / 8;

                // 系统声音走 loopback；共享模式 + 200ms 缓冲（容忍轮询抖动），轮询读取
                // （loopback 与事件回调的组合在部分系统不触发事件，故不用事件驱动）
                uint flags = mode == AudioCaptureMode.System ? Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK : 0;
                Wasapi.Check(_client.Initialize(Wasapi.AUDCLNT_SHAREMODE_SHARED, flags,
                    200 * 10_000L, 0, fmtPtr, IntPtr.Zero), "初始化音频采集");
            }
            finally
            {
                Wasapi.CoTaskMemFree(fmtPtr);
            }

            var iidCapture = Wasapi.IID_IAudioCaptureClient;
            Wasapi.Check(_client.GetService(ref iidCapture, out _capture!), "获取音频采集接口");
        }
        catch
        {
            // 构造失败须回收已激活的 COM 对象，再向上抛
            Release(_capture);
            Release(_client);
            Release(_device);
            Release(_enumerator);
            throw;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TLSnipPin 音频采集",
        };
    }

    /// <summary>启动采集线程</summary>
    public void Start() => _thread.Start();

    /// <summary>暂停/恢复（UI 触发，录制线程转发）：暂停期间丢弃数据，恢复后重置时间基准</summary>
    public void SetPaused(bool paused) => _paused = paused;

    /// <summary>取出一个待编码的音频包（录制线程调用）</summary>
    public bool TryDequeue(out AudioPacket packet) => _queue.TryDequeue(out packet);

    public void Dispose()
    {
        // 收尾模式：先让采集线程把时间轴补齐到当前墙钟（静音补齐，上限 500ms），
        // 保证音轨与视频轨同时长结束，再退出线程
        _finishing = true;
        try { if (_thread.IsAlive) _thread.Join(1000); } catch { }
        _stopRequested = true;
        try { if (_thread.IsAlive) _thread.Join(2000); } catch { }
        Release(_capture);
        Release(_client);
        Release(_device);
        Release(_enumerator);
    }

    // ---------------- 采集线程 ----------------
    private void Loop()
    {
        // COM 已随录制线程以 MTA 初始化，本线程同为 MTA，可跨线程使用其 COM 对象；
        // 但仍需本线程自己的初始化标记（与 CoUninitialize 成对）
        int cohr = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
        bool comInited = cohr is 0 or 1;
        try
        {
            Wasapi.Check(_client.Start(), "启动音频采集");
            _nextTs = _clock100ns();

            while (!_stopRequested)
            {
                if (_paused)
                {
                    if (_finishing) break; // 收尾中无需追平（时钟已扣除暂停段）
                    _resumeReset = true;
                    Thread.Sleep(20);
                    continue;
                }
                if (_resumeReset)
                {
                    _nextTs = _clock100ns(); // 时钟已扣除暂停段，恢复点即新基准
                    _resumeReset = false;
                }

                long now = _clock100ns();

                // 内循环取空当前缓冲的全部包：攒包时逐包读完，避免缓冲溢出丢数据
                bool gotPacket = false;
                while (_capture.GetNextPacketSize(out uint frames) >= 0 && frames > 0)
                {
                    if (_capture.GetBuffer(out IntPtr data, out uint read, out uint flags,
                            out _, out _) < 0)
                        break;
                    if (read > 0)
                    {
                        Enqueue(Convert(data, read, (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0), read);
                        gotPacket = true;
                    }
                    _capture.ReleaseBuffer(read);
                }

                // 无声期补静音：保证音轨时间轴连续（时长与视频对齐）
                if (!gotPacket && now - _nextTs > SilenceGap100ns)
                    Enqueue(new byte[SilenceFrames() * Channels * 4], (uint)SilenceFrames());

                // 收尾模式：时间轴已追平墙钟即可退出
                if (_finishing && _nextTs >= now - SilenceGap100ns)
                    break;

                Thread.Sleep(PollMs);
            }
        }
        catch (Exception ex)
        {
            // 音频中断不影响视频：记录原因，录制线程 drain 时上报
            FaultMessage = $"音频采集中断：{ex.Message}";
            Program.LogError("录屏音频", ex);
        }
        finally
        {
            try { _client.Stop(); } catch { }
            if (comInited) NativeMethods.CoUninitialize();
        }
    }

    /// <summary>按时间戳顺序入队并推进时间轴</summary>
    private void Enqueue(byte[] data, uint frames)
    {
        long dur = frames * 10_000_000L / SampleRate;
        _queue.Enqueue(new AudioPacket(data, _nextTs, dur));
        _nextTs += dur;
    }

    private int SilenceFrames() => (int)(SampleRate * SilenceDur100ns / 10_000_000L);

    /// <summary>把端点原始样本转成输出格式（float32、≤2 声道；静音包直接给零数组）</summary>
    private byte[] Convert(IntPtr data, uint frames, bool silent)
    {
        int outLen = (int)frames * Channels * 4;
        var result = new byte[outLen];
        if (silent) return result;

        int srcStride = _srcChannels * _srcBytesPerSample;
        if (_srcIsFloat && _srcBytesPerSample == 4 && _srcChannels == Channels)
        {
            // 同构 float32：整块直拷
            Marshal.Copy(data, result, 0, (int)frames * srcStride);
            return result;
        }

        // 异构（多声道抽取 / 整数转浮点）：逐帧逐声道转换
        var src = new byte[(int)frames * srcStride];
        Marshal.Copy(data, src, 0, src.Length);
        var floats = MemoryMarshal.Cast<byte, float>(result);
        int f = 0;
        for (int i = 0; i < frames; i++)
        {
            int frameBase = i * srcStride;
            for (int c = 0; c < Channels; c++)
                floats[f++] = ReadSample(src, frameBase + c * _srcBytesPerSample);
        }
        return result;
    }

    /// <summary>按源编码读取单个样本并归一化到 [-1, 1] 浮点</summary>
    private float ReadSample(byte[] src, int off)
    {
        if (_srcIsFloat)
            return BitConverter.ToSingle(src, off);
        return _srcBytesPerSample switch
        {
            1 => (src[off] - 128) / 128f,                                        // 8 位无符号
            2 => BitConverter.ToInt16(src, off) / 32768f,                         // 16 位
            3 => (src[off] | src[off + 1] << 8 | src[off + 2] << 16) switch       // 24 位小端
            {
                int s when (s & 0x800000) != 0 => (s - 0x1000000) / 8388608f,
                int s => s / 8388608f,
            },
            4 => BitConverter.ToInt32(src, off) / 2147483648f,                    // 32 位
            _ => 0f,
        };
    }

    private static void Release(object? comObject)
    {
        try { if (comObject != null) Marshal.ReleaseComObject(comObject); }
        catch { /* 已释放则忽略 */ }
    }
}
