using System.Runtime.InteropServices;

namespace CWTrainer.Desktop;

/// <summary>一个可选的播放设备。Index = -1 表示跟随 Windows 当前默认设备。</summary>
public sealed record AudioDevice(int Index, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// 电脑端 CW 音源。直接用 winmm 的 waveOut 接口，不引入 NAudio 等第三方库，
/// 这样才能指定输出设备；<see cref="AudioDevice.Index"/> = -1 时使用 WAVE_MAPPER，
/// 由系统在打开设备的那一刻解析成当前默认设备，因此天然具备「跟随系统」的能力。
///
/// 为了在系统默认设备被切换后能自动跟上，设备在静音一段时间后会被主动关闭，
/// 下一次发声重新打开 —— 此时 WAVE_MAPPER 会拿到新的默认设备。
/// </summary>
public sealed class AudioOutput : IDisposable
{
    private const int SampleRate = 44100;
    private const int BitsPerSample = 16;
    private const ushort Channels = 1;

    // 多久没有声音就把设备关掉（毫秒）。关了之后「跟随系统」才会重新解析默认设备。
    private const int IdleCloseMs = 4000;

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WaveFormatPcm = 1;
    private const uint WhdrDone = 0x00000001;
    private const uint WhdrPrepared = 0x00000002;
    private const uint WhdrBeginLoop = 0x00000004;
    private const uint WhdrEndLoop = 0x00000008;
    private const uint InfiniteLoops = 0xFFFFFFFF;

    private readonly object _lock = new();
    private readonly List<Buffer> _buffers = new();
    private readonly Timer _idleTimer;
    private readonly Action<string> _log;

    private IntPtr _handle = IntPtr.Zero;
    private int _deviceIndex = -1;
    private long _lastPlayMs;

    public AudioOutput(Action<string> log)
    {
        _log = log;
        _idleTimer = new Timer(_ => CloseIfIdle(), null, 1000, 1000);
    }

    /// <summary>当前选择的设备（-1 = 跟随系统）。</summary>
    public int DeviceIndex => _deviceIndex;

    /// <summary>枚举系统播放设备，第 0 项是「跟随系统」。</summary>
    public List<AudioDevice> GetDevices()
    {
        var list = new List<AudioDevice>
        {
            new(-1, "跟随系统")
        };

        try
        {
            uint count = waveOutGetNumDevs();

            for (uint i = 0; i < count; i++)
            {
                var caps = new WAVEOUTCAPS();
                if (waveOutGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf<WAVEOUTCAPS>()) == 0)
                {
                    string name = (caps.szPname ?? string.Empty).Trim();
                    if (name.Length == 0)
                        name = $"设备 {i + 1}";

                    list.Add(new AudioDevice((int)i, name));
                }
            }
        }
        catch (Exception ex)
        {
            _log($"枚举音频设备失败：{ex.Message}");
        }

        return list;
    }

    /// <summary>切换输出设备，下一次发声时生效。</summary>
    public void SelectDevice(int index)
    {
        lock (_lock)
        {
            if (_deviceIndex == index)
                return;

            _deviceIndex = index;
            CloseLocked();
        }
    }

    /// <summary>播放一个点或划。</summary>
    public void PlayTone(int durationMs, int frequencyHz, int volumePercent)
    {
        if (durationMs <= 0 || volumePercent <= 0)
            return;

        lock (_lock)
        {
            if (!EnsureOpenLocked())
                return;

            SweepLocked();

            IntPtr data = BuildPcm(durationMs, frequencyHz, volumePercent, loop: false, out int length);
            WriteLocked(data, length, loop: false);
            _lastPlayMs = Environment.TickCount64;
        }
    }

    /// <summary>直键模式：持续发声，直到 <see cref="Stop"/>。</summary>
    public void StartTone(int frequencyHz, int volumePercent)
    {
        if (volumePercent <= 0)
            return;

        lock (_lock)
        {
            if (!EnsureOpenLocked())
                return;

            waveOutReset(_handle);
            SweepLocked();

            // 0.5 秒无缝循环，避免缓冲区接缝产生爆音
            IntPtr data = BuildPcm(500, frequencyHz, volumePercent, loop: true, out int length);
            WriteLocked(data, length, loop: true);
            _lastPlayMs = Environment.TickCount64;
        }
    }

    /// <summary>立即停止发声（不影响已选择设备）。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero)
                return;

            waveOutReset(_handle);
            SweepLocked();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();

        lock (_lock)
        {
            CloseLocked();
        }
    }

    // ---------- 内部实现 ----------

    private void CloseIfIdle()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero)
                return;

            if (Environment.TickCount64 - _lastPlayMs < IdleCloseMs)
                return;

            CloseLocked();
        }
    }

    private bool EnsureOpenLocked()
    {
        if (_handle != IntPtr.Zero)
            return true;

        var format = new WAVEFORMATEX
        {
            wFormatTag = (ushort)WaveFormatPcm,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (ushort)(Channels * BitsPerSample / 8),
            nAvgBytesPerSec = (uint)(SampleRate * Channels * BitsPerSample / 8),
            cbSize = 0
        };

        uint deviceId = _deviceIndex < 0 ? WaveMapper : (uint)_deviceIndex;
        int result = waveOutOpen(out IntPtr handle, deviceId, ref format,
                                 IntPtr.Zero, IntPtr.Zero, 0);

        if (result != 0)
        {
            // 指定设备打不开（被拔掉/被独占）时退回默认设备，保证还能发声
            if (_deviceIndex >= 0)
            {
                _log($"音频设备打不开（错误码 {result}），已退回系统默认设备。");
                _deviceIndex = -1;
                result = waveOutOpen(out handle, WaveMapper, ref format,
                                     IntPtr.Zero, IntPtr.Zero, 0);
            }

            if (result != 0)
            {
                _log($"打开音频设备失败，错误码 {result}。");
                return false;
            }
        }

        _handle = handle;
        _lastPlayMs = Environment.TickCount64;
        return true;
    }

    private void WriteLocked(IntPtr data, int length, bool loop)
    {
        int headerSize = Marshal.SizeOf<WAVEHDR>();
        IntPtr header = Marshal.AllocHGlobal(headerSize);

        var hdr = new WAVEHDR
        {
            lpData = data,
            dwBufferLength = (uint)length,
            dwFlags = loop ? WhdrBeginLoop | WhdrEndLoop : 0,
            dwLoops = loop ? InfiniteLoops : 0
        };

        Marshal.StructureToPtr(hdr, header, false);

        if (waveOutPrepareHeader(_handle, header, (uint)headerSize) != 0)
        {
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            _log("准备音频缓冲区失败。");
            return;
        }

        if (waveOutWrite(_handle, header, (uint)headerSize) != 0)
        {
            waveOutUnprepareHeader(_handle, header, (uint)headerSize);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            _log("写入音频缓冲区失败。");
            return;
        }

        _buffers.Add(new Buffer { Header = header, Data = data });
    }

    /// <summary>回收已经播完的缓冲区。</summary>
    private void SweepLocked()
    {
        if (_handle == IntPtr.Zero)
            return;

        int headerSize = Marshal.SizeOf<WAVEHDR>();

        for (int i = _buffers.Count - 1; i >= 0; i--)
        {
            Buffer buffer = _buffers[i];
            var hdr = Marshal.PtrToStructure<WAVEHDR>(buffer.Header);

            if ((hdr.dwFlags & WhdrDone) == 0)
                continue;

            waveOutUnprepareHeader(_handle, buffer.Header, (uint)headerSize);
            Marshal.FreeHGlobal(buffer.Header);
            Marshal.FreeHGlobal(buffer.Data);
            _buffers.RemoveAt(i);
        }
    }

    private void CloseLocked()
    {
        if (_handle == IntPtr.Zero)
            return;

        waveOutReset(_handle);
        SweepLocked();

        foreach (Buffer buffer in _buffers)
        {
            waveOutUnprepareHeader(_handle, buffer.Header, (uint)Marshal.SizeOf<WAVEHDR>());
            Marshal.FreeHGlobal(buffer.Header);
            Marshal.FreeHGlobal(buffer.Data);
        }

        _buffers.Clear();

        waveOutClose(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>生成一段 16bit 单声道 PCM。</summary>
    private static IntPtr BuildPcm(
        int durationMs, int frequencyHz, int volumePercent, bool loop, out int length)
    {
        int frequency = Math.Clamp(frequencyHz, 100, 3000);
        int sampleCount = Math.Max(1, SampleRate * durationMs / 1000);

        if (loop)
        {
            // 让循环长度正好是整数个周期，接缝处不会跳变
            double periodSamples = SampleRate / (double)frequency;
            int periods = Math.Max(1, (int)Math.Round(sampleCount / periodSamples));
            sampleCount = (int)Math.Round(periods * periodSamples);
        }

        length = sampleCount * 2;
        IntPtr data = Marshal.AllocHGlobal(length);

        double amplitude = Math.Clamp(volumePercent, 0, 100) / 100.0 * 0.32;
        int fade = loop ? 0 : Math.Min(SampleRate / 300, sampleCount / 2);   // 约 3ms 淡入淡出

        var samples = new short[sampleCount];
        double step = 2.0 * Math.PI * frequency / SampleRate;

        for (int i = 0; i < sampleCount; i++)
        {
            double envelope = 1.0;

            if (fade > 0)
            {
                if (i < fade)
                    envelope = i / (double)fade;
                else if (i >= sampleCount - fade)
                    envelope = Math.Min(envelope, (sampleCount - 1 - i) / (double)fade);
            }

            double value = Math.Sin(step * i) * amplitude * envelope;
            samples[i] = (short)Math.Clamp((int)(value * short.MaxValue),
                                           short.MinValue, short.MaxValue);
        }

        Marshal.Copy(samples, 0, data, sampleCount);
        return data;
    }

    private sealed class Buffer
    {
        public IntPtr Header;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern int waveOutOpen(
        out IntPtr hwo, uint uDeviceID, ref WAVEFORMATEX pwfx,
        IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern int waveOutClose(IntPtr hwo);

    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCaps(IntPtr uDeviceID, ref WAVEOUTCAPS pwoc, uint cbwoc);
}
