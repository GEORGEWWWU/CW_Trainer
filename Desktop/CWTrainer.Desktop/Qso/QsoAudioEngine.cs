using System.Runtime.InteropServices;

namespace CWTrainer.Desktop.Qso;

/// <summary>
/// 通联模式的接收机音频引擎。
///
/// 和练习模式不同，这里不能用"一段循环 PCM"糊弄：底噪要一直在，
/// 虚拟电台的 CW 音要按自己的节奏通断，调谐时音高还要连续变化。
/// 所以开一条生成线程，把 1 024 帧的小块连续喂给 waveOut，
/// 每块都按"当前场景快照"实时合成。
///
/// 线程模型：界面线程只做 <see cref="Update"/>（发布不可变快照），
/// 音频线程只读快照、只写自己的缓冲，两边不加锁。
/// </summary>
public sealed class QsoAudioEngine : IDisposable
{
    private const int SampleRate = QsoAudioSynth.SampleRate;
    private const int BlockSamples = 1024;      // 约 23 ms
    private const int BlockCount = 4;

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WaveFormatPcm = 1;
    private const uint WhdrDone = 0x00000001;

    private readonly QsoAudioSynth _synth = new();
    private readonly object _gate = new();
    private readonly List<Block> _blocks = new();
    private readonly Dictionary<string, double> _startTimes = new();
    private readonly Action<string> _log;
    private readonly float[] _mix = new float[BlockSamples];
    private readonly short[] _pcm = new short[BlockSamples];

    private volatile AudioScene _scene = new();
    private volatile bool _running;
    private Thread? _thread;
    private IntPtr _handle = IntPtr.Zero;

    

    public QsoAudioEngine(Action<string> log)
    {
        _log = log;
    }

    /// <summary>音频时钟（毫秒）。界面用它给瀑布上的发射柱定位。</summary>
    public double NowMs => _synth.ElapsedMs;

    /// <summary>Inspect the same station key/speech plans used by the synthesizer.</summary>
    public bool HasRecentStationSignal(int lookbackMs)
    {
        if (!_running || _handle == IntPtr.Zero)
            return false;

        AudioScene scene = _scene;
        long endMs = (long)_synth.ElapsedMs;
        foreach (AudioVoice voice in scene.Voices)
        {
            long end = endMs - (long)Math.Ceiling(voice.PhaseMs);
            long start = Math.Max(0, end - lookbackMs);
            for (long ms = end - 1; ms >= start; ms--)
            {
                if (voice.Key[(int)(ms % voice.CycleMs)])
                    return true;
            }
        }

        return false;
    }

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _synth.ResetClock();
        _startTimes.Clear();

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "QSO audio",
            Priority = ThreadPriority.AboveNormal
        };

        _thread.Start();
    }

    public void Stop()
    {
        if (!_running)
            return;

        _running = false;

        try
        {
            _thread?.Join(500);
        }
        catch (Exception ex)
        {
            _log($"停止音频线程出错：{ex.Message}");
        }

        _thread = null;

        lock (_gate)
        {
            CloseLocked();
        }
    }

    /// <summary>发布一帧场景。会为每路信号固定一个起始时刻，重算场景时节奏不会跳。</summary>
    public void Update(AudioScene scene)
    {
        double now = _synth.ElapsedMs;

        var voices = new List<AudioVoice>(scene.Voices.Count);
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (AudioVoice voice in scene.Voices)
        {
            live.Add(voice.Id);

            if (!_startTimes.TryGetValue(voice.Id, out double start))
            {
                start = now;
                _startTimes[voice.Id] = start;
            }

            voices.Add(new AudioVoice
            {
                Id = voice.Id,
                Kind = voice.Kind,
                AudioHz = voice.AudioHz,
                Gain = voice.Gain,
                Key = voice.Key,
                CycleMs = voice.CycleMs,
                PhaseMs = start
            });
        }

        // 索引稳定，避免音量平滑状态在相邻帧之间错位
        voices.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        foreach (string id in _startTimes.Keys.ToList())
            if (!live.Contains(id))
                _startTimes.Remove(id);

        _scene = new AudioScene
        {
            Noise = scene.Noise,
            Voices = voices,
            CenterHz = scene.CenterHz,
            BandwidthHz = scene.BandwidthHz,
            SsbWide = scene.SsbWide,
            Muted = scene.Muted
        };
    }

    public void Dispose() => Stop();

    // ---------- 音频线程 ----------

    private void Run()
    {
        while (_running)
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero && !OpenLocked())
                {
                    Thread.Sleep(400);
                    continue;
                }

                FeedLocked();
            }

            Thread.Sleep(4);
        }
    }

    /// <summary>把已经播完的块重新填满再送回去。</summary>
    private void FeedLocked()
    {
        int headerSize = Marshal.SizeOf<WAVEHDR>();

        foreach (Block block in _blocks)
        {
            var header = Marshal.PtrToStructure<WAVEHDR>(block.Header);

            if ((header.dwFlags & WhdrDone) == 0)
                continue;

            // 系统用完的头必须先 Unprepare 再重写。
            // 直接整块 StructureToPtr 会把驱动内部的链表指针一起覆盖，
            // 实测表现为堆损坏（0xC0000374）直接崩进程。
            waveOutUnprepareHeader(_handle, block.Header, (uint)headerSize);

            _synth.Render(_mix, BlockSamples, _scene);
            ToPcm(_mix, _pcm, BlockSamples);
            Marshal.Copy(_pcm, 0, block.Data, BlockSamples);

            var again = new WAVEHDR
            {
                lpData = block.Data,
                dwBufferLength = (uint)(BlockSamples * sizeof(short)),
                dwFlags = 0,
                dwLoops = 0
            };

            Marshal.StructureToPtr(again, block.Header, false);

            if (waveOutPrepareHeader(_handle, block.Header, (uint)headerSize) != 0)
                return;

            waveOutWrite(_handle, block.Header, (uint)headerSize);
        }
    }

    /// <summary>
    /// 合成出来的是 float（-1..1），waveOut 吃的是 16bit PCM。
    /// 注意 Marshal.Copy(float[]) 一次拷 4 字节/样本，缓冲区必须按 float 算，
    /// 而 dwBufferLength 按输出字节（2 字节/样本）算 —— 两者混用就是堆溢出。
    /// </summary>
    private static void ToPcm(float[] mix, short[] pcm, int count)
    {
        for (int i = 0; i < count; i++)
            pcm[i] = (short)Math.Clamp((int)(mix[i] * short.MaxValue), short.MinValue, short.MaxValue);
    }

    private bool OpenLocked()
    {
        var format = new WAVEFORMATEX
        {
            wFormatTag = (ushort)WaveFormatPcm,
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = SampleRate * 2,
            cbSize = 0
        };

        int result = waveOutOpen(out IntPtr handle, WaveMapper, ref format,
                                 IntPtr.Zero, IntPtr.Zero, 0);

        if (result != 0)
        {
            _log($"通联音频设备打不开（{result}），稍后重试。");
            return false;
        }

        _handle = handle;
        _blocks.Clear();

        int headerSize = Marshal.SizeOf<WAVEHDR>();

        for (int i = 0; i < BlockCount; i++)
        {
            IntPtr data = Marshal.AllocHGlobal(BlockSamples * sizeof(short));
            IntPtr header = Marshal.AllocHGlobal(headerSize);

            _synth.Render(_mix, BlockSamples, _scene);
            ToPcm(_mix, _pcm, BlockSamples);
            Marshal.Copy(_pcm, 0, data, BlockSamples);

            var hdr = new WAVEHDR
            {
                lpData = data,
                dwBufferLength = (uint)(BlockSamples * sizeof(short)),
                dwFlags = 0
            };

            Marshal.StructureToPtr(hdr, header, false);

            if (waveOutPrepareHeader(_handle, header, (uint)headerSize) != 0 ||
                waveOutWrite(_handle, header, (uint)headerSize) != 0)
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
                _log("通联音频缓冲区准备失败。");
                break;
            }

            _blocks.Add(new Block { Header = header, Data = data });
        }

        return true;
    }

    private void CloseLocked()
    {
        if (_handle == IntPtr.Zero)
            return;

        waveOutReset(_handle);

        int headerSize = Marshal.SizeOf<WAVEHDR>();

        foreach (Block block in _blocks)
        {
            waveOutUnprepareHeader(_handle, block.Header, (uint)headerSize);
            Marshal.FreeHGlobal(block.Header);
            Marshal.FreeHGlobal(block.Data);
        }

        _blocks.Clear();
        waveOutClose(_handle);
        _handle = IntPtr.Zero;
    }

    private sealed class Block
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
}
