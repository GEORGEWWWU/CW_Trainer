namespace CWTrainer.Desktop.Qso;

/// <summary>发射/接收模式。这里只做 HF 常用的三种。</summary>
public enum EmissionMode
{
    Cw,
    Usb,
    Lsb
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

    /// <summary>信号强度（无衰落时的中值，dBm）。</summary>
    public required double LevelDbm { get; init; }

    public double FadePhase { get; init; }
    public double FadeRate { get; init; }
    public double FadeDepthDb { get; init; }

    /// <summary>缓慢的漂移（Hz），模拟对方 VFO 与传播变化。</summary>
    public double DriftHz { get; set; }

    public double CurrentLevelDbm(double seconds)
    {
        double fade = FadeDepthDb * Math.Sin(FadePhase + seconds * FadeRate);
        return LevelDbm + fade;
    }
}

/// <summary>
/// 通联模式的"接收机"模型。
///
/// 它不做真实的射频仿真，只产生三样东西：
///   1. 一条按 dBm 分布的频谱（用来画瀑布和频谱线）；
///   2. 当前调谐频率上的接收电平（用来驱动 S 表）；
///   3. 调谐点落在哪个电台上（用来显示呼号等信息）。
///
/// 数值锚点取自真实电台的惯例：HF 段 S9 = -73 dBm，每格 6 dB，+20/+40/+60 以上继续线性。
/// </summary>
public sealed class QsoSimulator
{
    public const double S9Dbm = -73.0;
    public const double SUnitDb = 6.0;

    /// <summary>接收机带宽（Hz）：CW 500、SSB 2400，与真机默认值接近。</summary>
    public const double CwBandwidthHz = 500.0;
    public const double SsbBandwidthHz = 2400.0;

    private static readonly string[] Callsigns =
    {
        "BG6ABC", "BY1AA", "JA1XYZ", "K1ABC", "W7QQ", "DL2XX",
        "UA9QQ", "VK3YY", "F5XX", "PY2ZZ", "HL5QQ", "ZS6AA",
        "I2ABC", "OH2XX", "SP9QQ", "JE1ABC"
    };

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

    private readonly Random _random = new(20261109);
    private readonly Dictionary<string, List<Station>> _stations = new();
    private readonly double[] _noiseWobble = new double[64];

    private long _frequencyHz = 14_025_000;
    private EmissionMode _mode = EmissionMode.Cw;
    private double _seconds;
    private double _sMeterDbm;

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

    public BandInfo Band => FindBand(_frequencyHz);

    public double Seconds => _seconds;

    /// <summary>S 表读数（dBm，带表针的惯性）。</summary>
    public double SMeterDbm => _sMeterDbm;

    /// <summary>当前调谐点上的信号电平（瞬时，未平滑）。</summary>
    public double SignalDbm => ComputeLevel(_frequencyHz, _mode);

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

        // S 表：上升快、回落慢，像真实表的机械惯性
        double target = SignalDbm;
        double rate = target > _sMeterDbm ? 0.55 : 0.12;
        _sMeterDbm += (target - _sMeterDbm) * Math.Clamp(rate * dtSeconds * 8, 0, 1);
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

    /// <summary>
    /// 生成一根频谱：从左到右 binCount 个点，中心是当前调谐频率。
    /// 返回值单位 dBm。
    /// </summary>
    /// <summary>当前波段内的全部电台，按强度排序，供电台列表使用。</summary>
    public IReadOnlyList<Station> StationsInBand =>
        StationsOf(Band).OrderByDescending(s => s.CurrentLevelDbm(_seconds)).ToList();

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

        return result;
    }

    private List<Station> StationsOf(BandInfo band)
        => _stations.TryGetValue(band.Name, out List<Station>? list) ? list : new List<Station>();

    private List<Station> CreateStations(BandInfo band)
    {
        var list = new List<Station>();
        var used = new HashSet<string>();
        int count = 4 + _random.Next(4);
        long span = band.EndHz - band.StartHz;

        for (int i = 0; i < count; i++)
        {
            // 低端放 CW，中高端放 SSB —— 和真实波段习惯一致
            bool cw = _random.NextDouble() < 0.55;
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
                call = Callsigns[_random.Next(Callsigns.Length)];
            }
            while (!used.Add(call) && ++guard < 20);

            list.Add(new Station
            {
                FreqHz = freq,
                Mode = mode,
                Call = call,
                LevelDbm = level,
                FadePhase = _random.NextDouble() * Math.PI * 2,
                FadeRate = 0.15 + _random.NextDouble() * 0.5,
                FadeDepthDb = 1.5 + _random.NextDouble() * 3.5
            });
        }

        _ = span;
        return list;
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
    private static double FilterAttenuation(Station station, long frequencyHz, EmissionMode mode)
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

    private static double ShapeCw(double offset)
    {
        double half = CwBandwidthHz / 2;
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
