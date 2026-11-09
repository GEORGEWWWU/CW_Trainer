namespace CWTrainer.Desktop.Qso;

/// <summary>发射/接收模式。这里只做 HF 常用的三种。</summary>
public enum EmissionMode
{
    Cw,
    Usb,
    Lsb
}

/// <summary>滤波器档位。真机一般给 2–3 档，CW 窄到 250 Hz、SSB 宽到 3 kHz。</summary>
public enum FilterSlot
{
    Narrow,
    Medium,
    Wide
}

/// <summary>一个业余波段。</summary>
public sealed record BandInfo(string Name, long StartHz, long EndHz, double NoiseDbm)
{
    public long CenterHz => (StartHz + EndHz) / 2;
    public bool Contains(long hz) => hz >= StartHz && hz <= EndHz;

    /// <summary>CW 段一般在波段低端，这里取低端 80 kHz 作为 CW 活动区。</summary>
    public long CwSegmentEndHz => Math.Min(StartHz + 80_000, EndHz);
}

/// <summary>模拟出来的一个电台信号。</summary>
public sealed class Station
{
    public required long FreqHz { get; init; }
    public required EmissionMode Mode { get; init; }
    public required string Call { get; init; }
    public required string Country { get; init; }

    /// <summary>信号强度（无衰落时的中值，dBm）。</summary>
    public required double LevelDbm { get; init; }

    public double FadePhase { get; init; }
    public double FadeRate { get; init; }
    public double FadeDepthDb { get; init; }

    /// <summary>缓慢的漂移（Hz），模拟对方 VFO 与传播变化。</summary>
    public double DriftHz { get; set; }

    /// <summary>拍发速度（WPM）。SSB 电台为 0。</summary>
    public int Wpm { get; init; }

    /// <summary>它正在发的内容（CW）或说话节奏（SSB）。</summary>
    public required MorsePlan Plan { get; init; }

    public string Message { get; init; } = "";

    /// <summary>音频侧唯一标识：呼号 + 频率，重调谐时不会串台。</summary>
    public string VoiceId => $"{Call}@{FreqHz}";

    public double CurrentLevelDbm(double seconds)
    {
        double fade = FadeDepthDb * Math.Sin(FadePhase + seconds * FadeRate);
        return LevelDbm + fade;
    }
}

/// <summary>瀑布上的一根发射柱：我们自己的信号在什么时候按下、什么时候松开。</summary>
public readonly record struct TxWindow(double StartSeconds, double EndSeconds);

/// <summary>
/// 通联模式的"接收机"模型。
///
/// 它不做真实的射频仿真，只产生四样东西：
///   1. 一条按 dBm 分布的频谱（用来画瀑布和频谱线，含我们自己发射时的那根柱子）；
///   2. 当前调谐频率上的接收电平（用来驱动 S 表）；
///   3. 调谐点落在哪个电台上（用来显示呼号等信息）；
///   4. 一帧音频场景（底噪音色 + 各台 CW 音的频率/音量/按键时间轴）。
///
/// 数值锚点取自真实电台的惯例：HF 段 S9 = -73 dBm，每格 6 dB。
/// 底噪按 ITU-R P.372 的规律排：160m 最高、10m 最低，相邻波段约差 2–4 dB。
/// </summary>
public sealed class QsoSimulator
{
    public const double S9Dbm = -73.0;
    public const double SUnitDb = 6.0;

    /// <summary>默认滤波器宽度（Hz）。CW 500、SSB 2400，与真机默认值一致。</summary>
    public const double CwBandwidthHz = 500.0;
    public const double SsbBandwidthHz = 2400.0;

    public static readonly BandInfo[] Bands =
    {
        new("160m", 1_800_000, 2_000_000, -96),
        new("80m", 3_500_000, 3_900_000, -100),
        new("40m", 7_000_000, 7_200_000, -104),
        new("30m", 10_100_000, 10_150_000, -107),
        new("20m", 14_000_000, 14_350_000, -110),
        new("17m", 18_068_000, 18_168_000, -112),
        new("15m", 21_000_000, 21_450_000, -114),
        new("12m", 24_890_000, 24_990_000, -115),
        new("10m", 28_000_000, 29_700_000, -116)
    };

    /// <summary>
    /// 各波段的底噪"音色"。
    /// LowEmphasis 越大，低频的隆隆声越重；Crackle 是静电炸裂的频率和幅度。
    /// 160m/80m 夏天的静电像爆豆，20m 以上基本只剩细嘶声。
    /// </summary>
    private static readonly Dictionary<string, NoiseProfile> NoiseProfiles = new()
    {
        ["160m"] = new(0.66, 0.88, 2.6, 0.60),
        ["80m"] = new(0.58, 0.78, 1.8, 0.48),
        ["40m"] = new(0.50, 0.64, 1.1, 0.36),
        ["30m"] = new(0.46, 0.54, 0.60, 0.27),
        ["20m"] = new(0.42, 0.42, 0.30, 0.19),
        ["17m"] = new(0.38, 0.34, 0.18, 0.14),
        ["15m"] = new(0.35, 0.28, 0.12, 0.10),
        ["12m"] = new(0.32, 0.24, 0.07, 0.07),
        ["10m"] = new(0.29, 0.18, 0.04, 0.05)
    };

    private readonly Random _random = new(20261109);
    private readonly Dictionary<string, List<Station>> _stations = new();
    private readonly double[] _noiseWobble = new double[64];
    private readonly List<TxWindow> _txWindows = new();

    private long _frequencyHz = 14_025_000;
    private EmissionMode _mode = EmissionMode.Cw;
    private FilterSlot _filter = FilterSlot.Medium;
    private double _seconds;
    private double _sMeterDbm;
    private double _txUntilSeconds = double.MinValue;
    private double _txStartSeconds = double.MinValue;
    private bool _txOpen;

    public QsoSimulator()
    {
        foreach (BandInfo band in Bands)
            _stations[band.Name] = CreateStations(band);

        for (int i = 0; i < _noiseWobble.Length; i++)
            _noiseWobble[i] = _random.NextDouble() * 2 - 1;

        _sMeterDbm = FindBand(_frequencyHz).NoiseDbm;
    }

    public long FrequencyHz
    {
        get => _frequencyHz;
        set => _frequencyHz = Math.Clamp(value, 30_000, 30_000_000);
    }

    public EmissionMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public FilterSlot Filter
    {
        get => _filter;
        set => _filter = value;
    }

    public BandInfo Band => FindBand(_frequencyHz);

    public double Seconds => _seconds;

    /// <summary>当前滤波器宽度（Hz）。</summary>
    public double BandwidthHz => _mode switch
    {
        EmissionMode.Cw => _filter switch
        {
            FilterSlot.Narrow => 250,
            FilterSlot.Wide => 1000,
            _ => CwBandwidthHz
        },
        _ => _filter switch
        {
            FilterSlot.Narrow => 1800,
            FilterSlot.Wide => 3000,
            _ => SsbBandwidthHz
        }
    };

    /// <summary>S 表读数（dBm，带表针的惯性）。</summary>
    public double SMeterDbm => _sMeterDbm;

    /// <summary>当前调谐点上的信号电平（瞬时，未平滑）。</summary>
    public double SignalDbm => ComputeLevel(_frequencyHz, _mode);

    /// <summary>正在发射（我们自己的信号占着瀑布中心）。</summary>
    public bool IsTransmitting => _txOpen || _seconds < _txUntilSeconds;

    /// <summary>当前调谐点捕获到的电台（信噪比够高才算）。</summary>
    public Station? TunedStation
    {
        get
        {
            Station? best = null;
            double bestLevel = double.MinValue;

            foreach (Station station in StationsOf(Band))
            {
                double att = FilterAttenuation(station, _frequencyHz, _mode);

                if (att < -6)
                    continue;

                double level = station.CurrentLevelDbm(_seconds) + att;

                if (level > bestLevel)
                {
                    bestLevel = level;
                    best = station;
                }
            }

            // 至少高出噪声底 6 dB 才算"抄到了"
            return bestLevel > NoiseDbm + 6 ? best : null;
        }
    }

    /// <summary>该波段的噪声底（dBm），带缓慢起伏。</summary>
    public double NoiseDbm
    {
        get
        {
            BandInfo band = Band;
            double wobble = Math.Sin(_seconds * 0.35) * 1.5 + Math.Sin(_seconds * 0.11 + 1.7) * 1.0;
            return band.NoiseDbm + wobble;
        }
    }

    public void Tick(double dtSeconds)
    {
        _seconds += dtSeconds;

        foreach (List<Station> list in _stations.Values)
            foreach (Station station in list)
                station.DriftHz = Math.Sin(_seconds * 0.21 + station.FadePhase) * 12.0;

        // 表针惯性：起得快、落得慢，和 AGC 的"慢落快起"一致
        double target = SignalDbm;
        double tau = target > _sMeterDbm ? 0.03 : 0.28;
        _sMeterDbm += (target - _sMeterDbm) * (1 - Math.Exp(-dtSeconds / tau));

        _txWindows.RemoveAll(w => _seconds - w.EndSeconds > 4.0);
    }

    /// <summary>
    /// 报告一次发射。durationMs 是这次点或划的实际长度 —— 来自固件的 ELEMENT 事件，
    /// 所以瀑布上的柱子有多长，就是手按了多久。
    /// </summary>
    public void NotifyTx(double durationMs)
    {
        if (durationMs <= 0)
            return;

        double end = _seconds + durationMs / 1000.0;

        // 连续的发报（间隔小于 3 个点）并成一段，别在瀑布上碎成一堆小点
        if (_txWindows.Count > 0)
        {
            TxWindow last = _txWindows[^1];

            if (_seconds - last.EndSeconds < 0.09)
            {
                _txWindows[^1] = last with { EndSeconds = end };
                _txStartSeconds = Math.Min(_txStartSeconds, _seconds);
                _txUntilSeconds = Math.Max(_txUntilSeconds, end);
                return;
            }
        }

        _txStartSeconds = _seconds;
        _txUntilSeconds = end;
        _txWindows.Add(new TxWindow(_seconds, end));
    }

    /// <summary>最近几秒的发射窗口，按"距现在多少毫秒"给瀑布用。</summary>
    public List<(double StartAgeMs, double EndAgeMs)> TxAges()
    {
        var list = new List<(double, double)>(_txWindows.Count);

        foreach (TxWindow window in _txWindows)
            list.Add(((_seconds - window.EndSeconds) * 1000,
                      (_seconds - window.StartSeconds) * 1000));

        // 直键按住不放时，柱子要一直长到"现在"
        if (_txOpen)
            list.Add((0, (_seconds - _txStartSeconds) * 1000));

        return list;
    }

    /// <summary>直键按下：固件只报 TONE,ON，长度要等松开才知道。</summary>
    public void NotifyTxDown()
    {
        if (_txOpen)
            return;

        _txOpen = true;
        _txStartSeconds = _seconds;
        _txUntilSeconds = double.MaxValue;
    }

    /// <summary>直键松开：把这一段的实际时长记进瀑布。</summary>
    public void NotifyTxUp()
    {
        if (!_txOpen)
            return;

        _txOpen = false;
        double end = Math.Max(_txStartSeconds + 0.005, _seconds);

        _txUntilSeconds = end;
        _txWindows.Add(new TxWindow(_txStartSeconds, end));
    }

    /// <summary>把 dBm 换成 S 刻度（0 = S0，9 = S9，之后每 10 dB 加一格）。</summary>
    public static double DbmToSUnits(double dbm) => (dbm - S9Dbm) / SUnitDb + 9.0;

    public static string FormatSUnits(double dbm)
    {
        double s = DbmToSUnits(dbm);

        if (s >= 9)
        {
            double over = (s - 9) * SUnitDb;
            return over < 1 ? "S9" : $"S9+{over:0}";
        }

        return s <= 0 ? "S0" : $"S{s:0}";
    }

    /// <summary>dBm 换算成 50Ω 上的微伏（S9 = 50 µV）。</summary>
    public static double DbmToMicrovolts(double dbm)
    {
        double watts = Math.Pow(10, dbm / 10.0) / 1000.0;
        return Math.Sqrt(watts * 50.0) * 1e6;
    }

    /// <summary>当前波段内的全部电台，按强度排序，供电台列表使用。</summary>
    public IReadOnlyList<Station> StationsInBand =>
        StationsOf(Band).OrderByDescending(s => s.CurrentLevelDbm(_seconds)).ToList();

    /// <summary>
    /// 生成一帧音频场景：底噪音色 + 通带内所有能听到的信号。
    /// 用侧音做参考频率，偏调时信号音高会跟着变，这就是"调准"的听觉反馈。
    /// </summary>
    public AudioScene BuildAudioScene(double masterVolume, bool noiseOn, int pitchHz, bool muted)
    {
        double noise = NoiseDbm;
        double bandwidth = BandwidthHz;
        double volume = Math.Clamp(masterVolume, 0, 1);
        var voices = new List<AudioVoice>(4);

        foreach (Station station in StationsOf(Band))
        {
            double att = FilterAttenuation(station, _frequencyHz, _mode);

            if (att < -24)
                continue;

            double level = station.CurrentLevelDbm(_seconds) + att;
            double snr = level - noise;

            if (snr < 2)
                continue;

            double audioHz = pitchHz + (station.FreqHz - _frequencyHz);

            if (station.Mode == EmissionMode.Cw && (audioHz < 220 || audioHz > 1600))
                continue;

            double gain = station.Mode == EmissionMode.Cw
                ? 0.05 + 0.32 * Math.Clamp((snr - 4) / 32, 0, 1)
                : 0.04 + 0.16 * Math.Clamp((snr - 4) / 32, 0, 1);

            voices.Add(new AudioVoice
            {
                Id = station.VoiceId,
                Kind = station.Mode == EmissionMode.Cw ? VoiceKind.Cw : VoiceKind.Ssb,
                AudioHz = audioHz,
                Gain = gain * volume,
                Key = station.Plan.Key,
                CycleMs = station.Plan.CycleMs
            });
        }

        // 只留最强的四路，多了就是一团糊
        if (voices.Count > 4)
            voices = voices.OrderByDescending(v => v.Gain).Take(4).ToList();

        NoiseProfile profile = NoiseProfiles.TryGetValue(Band.Name, out NoiseProfile? p)
            ? p
            : new NoiseProfile(0.4, 0.4, 0.2, 0.15);

        return new AudioScene
        {
            Noise = profile with { Gain = noiseOn ? profile.Gain * volume : 0 },
            Voices = voices,
            CenterHz = pitchHz,
            BandwidthHz = bandwidth,
            SsbWide = _mode != EmissionMode.Cw,
            Muted = muted || volume <= 0
        };
    }

    /// <summary>
    /// 生成一根频谱：从左到右 binCount 个点，中心是当前调谐频率。
    /// 返回值单位 dBm。
    /// </summary>
    public double[] BuildSpectrum(int binCount, double spanHz)
    {
        var result = new double[binCount];
        double noise = NoiseDbm;
        List<Station> stations = StationsOf(Band);

        // 噪声底：慢起伏 + 逐点抖动 + 三点平滑，避免看起来像电视机雪花
        double tilt = Math.Sin(_seconds * 0.27) * 0.8;

        for (int i = 0; i < binCount; i++)
        {
            double r = _random.NextDouble() * 2 - 1;
            int wobbleIndex = (i * 7 + (int)(_seconds * 5)) % _noiseWobble.Length;
            result[i] = noise + tilt + r * 3.2 + _noiseWobble[wobbleIndex] * 1.2;
        }

        for (int i = 1; i < binCount - 1; i++)
            result[i] = (result[i - 1] + result[i] * 2 + result[i + 1]) * 0.25;

        double hzPerBin = spanHz / binCount;

        foreach (Station station in stations)
        {
            double stationHz = station.FreqHz + station.DriftHz;
            double level = station.CurrentLevelDbm(_seconds);

            // 信号在瀑布上的宽度：CW 窄、SSB 宽
            double sigma = station.Mode == EmissionMode.Cw ? 90.0 : 900.0;
            double centerBin = (stationHz - _frequencyHz) / hzPerBin + binCount / 2.0;

            int from = (int)Math.Floor(centerBin - sigma * 3 / hzPerBin);
            int to = (int)Math.Ceiling(centerBin + sigma * 3 / hzPerBin);

            for (int i = Math.Max(0, from); i <= Math.Min(binCount - 1, to); i++)
            {
                double offsetHz = (i - centerBin) * hzPerBin;
                double gauss = Math.Exp(-(offsetHz * offsetHz) / (2 * sigma * sigma));
                double value = level + 20 * Math.Log10(Math.Max(gauss, 1e-6));

                if (value > result[i])
                    result[i] = value;
            }
        }

        // 自己发射时，中心那根柱子顶到表头之上；宽度就是 CW 键控后的带宽
        if (IsTransmitting)
        {
            double centerBin = binCount / 2.0;
            double sigma = Math.Max(1.2, 55.0 / hzPerBin);
            int from = (int)Math.Floor(centerBin - sigma * 3);
            int to = (int)Math.Ceiling(centerBin + sigma * 3);

            for (int i = Math.Max(0, from); i <= Math.Min(binCount - 1, to); i++)
            {
                double offsetHz = (i - centerBin) * hzPerBin;
                double gauss = Math.Exp(-(offsetHz * offsetHz) / (2 * sigma * sigma));
                double value = -28 + 20 * Math.Log10(Math.Max(gauss, 1e-6));

                if (value > result[i])
                    result[i] = value;
            }
        }

        return result;
    }

    private List<Station> StationsOf(BandInfo band)
        => _stations.TryGetValue(band.Name, out List<Station>? list) ? list : new List<Station>();

    private List<Station> CreateStations(BandInfo band)
    {
        var list = new List<Station>();
        var used = new HashSet<string>();
        int count = 5 + _random.Next(4);
        bool local = band.StartHz < 10_000_000;   // 低波段只通附近国家

        for (int i = 0; i < count; i++)
        {
            // 低端放 CW，中高端放 SSB —— 和真实波段习惯一致
            bool cw = _random.NextDouble() < 0.6;
            long low = cw ? band.StartHz + 2_000 : band.CwSegmentEndHz + 3_000;
            long high = cw ? band.CwSegmentEndHz : band.EndHz - 3_000;

            if (high <= low)
                low = band.StartHz + 1_000;

            long freq = low + (long)(_random.NextDouble() * Math.Max(1, high - low));
            freq -= freq % 10;   // 真实电台很少停在整 1 Hz 上

            EmissionMode mode = cw
                ? EmissionMode.Cw
                : (_random.NextDouble() < 0.5 ? EmissionMode.Usb : EmissionMode.Lsb);

            double level = cw
                ? -95 + _random.NextDouble() * 32
                : -88 + _random.NextDouble() * 30;

            string call;
            int guard = 0;

            do
            {
                call = CallsignFactory.Next(_random, local);
            }
            while (!used.Add(call) && ++guard < 40);

            // 拍发速度：多数 18–30，少数新手很慢、高手很快
            int wpm = mode == EmissionMode.Cw
                ? (_random.NextDouble() < 0.15 ? 10 + _random.Next(6) : 18 + _random.Next(13))
                : 0;

            (string message, MorsePlan plan) = BuildTraffic(call, mode, wpm, freq);

            list.Add(new Station
            {
                FreqHz = freq,
                Mode = mode,
                Call = call,
                Country = CallsignFactory.CountryOf(call),
                LevelDbm = level,
                FadePhase = _random.NextDouble() * Math.PI * 2,
                FadeRate = 0.15 + _random.NextDouble() * 0.5,
                FadeDepthDb = 1.5 + _random.NextDouble() * 3.5,
                Wpm = wpm,
                Message = message,
                Plan = plan
            });
        }

        return list;
    }

    /// <summary>
    /// 生成这个台"正在发什么"。真实 CW 段上绝大多数是三种：
    /// 呼叫 CQ、回答别人的 CQ（报信号报告）、以及 QRL? 问频率是否占用。
    /// </summary>
    private (string Message, MorsePlan Plan) BuildTraffic(
        string call, EmissionMode mode, int wpm, long freq)
    {
        if (mode != EmissionMode.Cw)
            return ("语音通联", MorsePlan.BuildSpeech(_random.Next()));

        string other = CallsignFactory.Next(_random, freq < 10_000_000);
        double roll = _random.NextDouble();

        string message = roll switch
        {
            < 0.55 => $"CQ CQ CQ DE {call} {call} K",
            < 0.78 => $"{other} DE {call} R UR 5NN 5NN BK",
            < 0.90 => $"QRL? QRL? DE {call}",
            _ => $"TEST TEST DE {call}"
        };

        // 每次发完停一会儿，像真人一样留出对方回答的时间
        int gap = 3000 + _random.Next(5000);

        return (message, MorsePlan.Build(message, wpm, _random, gap));
    }

    private static BandInfo FindBand(long hz)
    {
        foreach (BandInfo band in Bands)
            if (band.Contains(hz))
                return band;

        // 不在业余段内：按频率就近归到一个"波段"，只用于显示
        BandInfo nearest = Bands[0];

        foreach (BandInfo band in Bands)
            if (Math.Abs(band.CenterHz - hz) < Math.Abs(nearest.CenterHz - hz))
                nearest = band;

        return nearest;
    }

    /// <summary>当前调谐点的接收电平：取所有电台经滤波器衰减后的最大值，下限是噪声底。</summary>
    private double ComputeLevel(long frequencyHz, EmissionMode mode)
    {
        double level = NoiseDbm;

        foreach (Station station in StationsOf(Band))
        {
            double att = FilterAttenuation(station, frequencyHz, mode);

            if (att < -80)
                continue;

            double value = station.CurrentLevelDbm(_seconds) + att;

            if (value > level)
                level = value;
        }

        return level;
    }

    /// <summary>
    /// 接收滤波器的选择性：模式对上、频率落在通带内衰减为 0，出带后快速下滑。
    /// 模式不对（比如用 USB 收 CW）额外压 40 dB，和真机一样抄不到。
    /// </summary>
    private double FilterAttenuation(Station station, long frequencyHz, EmissionMode mode)
    {
        double offset = frequencyHz - station.FreqHz;

        double att = mode switch
        {
            EmissionMode.Cw => ShapeCw(offset),
            EmissionMode.Usb => ShapeSsb(offset, upper: true),
            EmissionMode.Lsb => ShapeSsb(offset, upper: false),
            _ => -120
        };

        if (station.Mode != mode)
            att -= 40;

        return att;
    }

    private double ShapeCw(double offset)
    {
        double half = BandwidthHz / 2;
        double d = Math.Abs(offset);

        if (d <= half)
            return 0;

        return -Math.Min(120, (d - half) / half * 60 + 3);
    }

    private static double ShapeSsb(double offset, bool upper)
    {
        // 通带：载波上方 300–2700 Hz（USB），LSB 镜像
        double audio = upper ? offset : -offset;

        if (audio >= 300 && audio <= 2700)
            return 0;

        if (audio < 300)
            return -Math.Min(120, (300 - audio) / 60.0 * 12 + 3);

        return -Math.Min(120, (audio - 2700) / 60.0 * 12 + 3);
    }
}
