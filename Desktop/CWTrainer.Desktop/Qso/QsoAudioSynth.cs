namespace CWTrainer.Desktop.Qso;

/// <summary>一个双二阶滤波器（RBJ Audio EQ Cookbook 的低通 / 高通 / 带通）。</summary>
internal sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public void LowPass(double sampleRate, double frequency, double q)
    {
        (double cos, double alpha, double a0) = Common(sampleRate, frequency, q);
        _b0 = (1 - cos) / 2 / a0;
        _b1 = (1 - cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public void HighPass(double sampleRate, double frequency, double q)
    {
        (double cos, double alpha, double a0) = Common(sampleRate, frequency, q);
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public void BandPass(double sampleRate, double frequency, double q)
    {
        (double cos, double alpha, double a0) = Common(sampleRate, frequency, q);
        _b0 = alpha / a0;
        _b1 = 0;
        _b2 = -alpha / a0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public double Process(double x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;

        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;

        return y;
    }

    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0;
    }

    private static (double Cos, double Alpha, double A0) Common(
        double sampleRate, double frequency, double q)
    {
        frequency = Math.Clamp(frequency, 20, sampleRate * 0.45);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * Math.Max(0.1, q));

        return (cos, alpha, 1 + alpha);
    }
}

public enum VoiceKind
{
    Cw,
    Ssb
}

/// <summary>
/// 一个"听得见的信号"：音频频率、增益，以及 1ms 分辨率的按键时间轴。
/// PhaseMs 由引擎按首次出现的时间算出来，保证重算场景时节奏不会重置。
/// </summary>
public sealed class AudioVoice
{
    public required string Id { get; init; }
    public required VoiceKind Kind { get; init; }
    public required double AudioHz { get; init; }
    public required double Gain { get; init; }
    public required bool[] Key { get; init; }
    public required int CycleMs { get; init; }

    /// <summary>这段电码在音频时钟上的起始时刻（毫秒），由引擎维护。</summary>
    public double PhaseMs { get; init; }
}

/// <summary>一个波段的底噪"音色"。低波段低频重、静电多；高波段几乎只有细嘶声。</summary>
public sealed record NoiseProfile(double Gain, double LowEmphasis, double CracklePerSec, double CrackleAmp)
{
    public static readonly NoiseProfile Silent = new(0, 0, 0, 0);
}

/// <summary>交给音频引擎的一帧"场景快照"。</summary>
public sealed class AudioScene
{
    public NoiseProfile Noise { get; init; } = NoiseProfile.Silent;
    public IReadOnlyList<AudioVoice> Voices { get; init; } = Array.Empty<AudioVoice>();
    public double CenterHz { get; init; } = 700;
    public double BandwidthHz { get; init; } = 500;
    public bool SsbWide { get; init; }
    public bool Muted { get; init; }
}

/// <summary>
/// 接收机的音频合成器：底噪 + 若干路 CW 音 / SSB 人声。
///
/// 底噪不是"一段低通白噪声"：
///   1. 白噪声先混一点粉噪声（低频更重），模拟大气噪声的低频抬升；
///   2. 再进接收机滤波器 —— CW 用中心频率 = 侧音、Q = 侧音/带宽 的带通，
///      SSB 用 300–2700 Hz 的带通。于是 500 Hz 滤波器下的噪声比 2.4 kHz
///      低约 7 dB，听感也从"嘶"变成"嗡"，和真机一致；
///   3. 叠加随机静电炸裂（QRN），低波段每秒一两次、高波段几乎为零。
///
/// 每一路 CW 音的音高 = 侧音 + 与调谐频率的偏差，所以偏调时听到的是拍频，
/// 落在滤波器边缘会自然衰减 —— 这正是"调准"这个动作的听觉反馈。
/// </summary>
public sealed class QsoAudioSynth
{
    public const int SampleRate = 44100;

    private readonly Biquad _hp = new();
    private readonly Biquad _hp2 = new();
    private readonly Biquad _lp = new();
    private readonly Biquad _bp = new();
    private readonly Biquad _bp2 = new();

    private readonly Biquad[] _voiceBand = { new(), new(), new() };

    private readonly Random _random = new(97531);

    private readonly double _attack = 1 - Math.Exp(-1.0 / (0.0025 * SampleRate));
    private readonly double _release = 1 - Math.Exp(-1.0 / (0.006 * SampleRate));

    private readonly double[] _phase = new double[16];
    private readonly double[] _envelope = new double[16];
    private readonly double[] _gain = new double[16];
    private readonly double[] _voiceNoise = new double[16];

    private double _pink;
    private double _crackleEnvelope;
    private double _crackleTimer;
    private double _master;

    private AudioScene? _applied;

    public long SampleIndex { get; private set; }

    /// <summary>当前音频时钟（毫秒），引擎用它对齐各路信号的按键时间轴。</summary>
    public double ElapsedMs => SampleIndex * 1000.0 / SampleRate;

    public void ResetClock()
    {
        SampleIndex = 0;
        _master = 0;
        _pink = 0;
        _crackleEnvelope = 0;
        _crackleTimer = 0;

        _hp.Reset();
        _hp2.Reset();
        _lp.Reset();
        _bp.Reset();
        _bp2.Reset();

        foreach (Biquad band in _voiceBand)
            band.Reset();

        Array.Clear(_phase);
        Array.Clear(_envelope);
        Array.Clear(_gain);
        Array.Clear(_voiceNoise);
    }

    public void Render(float[] buffer, int count, AudioScene scene)
    {
        Configure(scene);

        NoiseProfile noise = scene.Muted ? NoiseProfile.Silent : scene.Noise;
        double masterTarget = scene.Muted ? 0 : 1;

        double dt = 1.0 / SampleRate;

        // 静电炸裂的衰减时间常数随波段走：低波段尾巴长
        double crackleTau = 0.035 + noise.LowEmphasis * 0.09;
        double crackleDecay = Math.Exp(-dt / crackleTau);

        IReadOnlyList<AudioVoice> voices = scene.Muted ? Array.Empty<AudioVoice>() : scene.Voices;

        for (int i = 0; i < count; i++)
        {
            double sample = 0;

            // ---- 底噪 ----
            if (noise.Gain > 0)
            {
                double white = _random.NextDouble() * 2 - 1;

                // 一阶低通得到粉噪声分量（-6dB/oct 的近似）
                _pink += (white - _pink) * 0.035;

                double mixed = white * (1 - 0.75 * noise.LowEmphasis) + _pink * 2.2 * noise.LowEmphasis;

                // 静电：泊松式随机触发，指数衰减
                _crackleTimer -= dt;

                if (_crackleTimer <= 0 && noise.CracklePerSec > 0)
                {
                    _crackleEnvelope = noise.CrackleAmp * (0.35 + _random.NextDouble() * 0.9);
                    _crackleTimer = -Math.Log(Math.Max(1e-6, _random.NextDouble())) / noise.CracklePerSec;
                }

                _crackleEnvelope *= crackleDecay;

                double burst = _crackleEnvelope * (white + (_random.NextDouble() * 2 - 1));

                double shaped = scene.SsbWide
                    ? _lp.Process(_hp2.Process(_hp.Process(mixed + burst)))
                    : _bp2.Process(_bp.Process(mixed + burst));

                sample += shaped * noise.Gain;
            }

            // ---- 各路信号 ----
            for (int v = 0; v < voices.Count && v < 16; v++)
            {
                AudioVoice voice = voices[v];

                double ms = SampleIndex * 1000.0 / SampleRate;
                long pos = (long)(ms - voice.PhaseMs) % voice.CycleMs;

                if (pos < 0)
                    pos += voice.CycleMs;

                bool keyed = voice.Key[(int)pos];

                double target = keyed ? 1 : 0;
                _envelope[v] += (target - _envelope[v]) * (keyed ? _attack : _release);
                double env = _envelope[v];

                // 增益缓慢跟随：场景是 4 Hz 重算的，直接跳变会有"咔"声
                if (_gain[v] != voice.Gain)
                    _gain[v] += (voice.Gain - _gain[v]) * 0.0025;

                if (env < 1e-5 && !keyed)
                {
                    _phase[v] = 0;
                    continue;
                }

                if (voice.Kind == VoiceKind.Cw)
                {
                    _phase[v] += 2 * Math.PI * voice.AudioHz / SampleRate;

                    if (_phase[v] > 2 * Math.PI)
                        _phase[v] -= 2 * Math.PI;

                    // 极轻的二次谐波，比纯正弦的"电音"更像真收发机的拍频
                    double tone = Math.Sin(_phase[v]) + 0.045 * Math.Sin(2 * _phase[v]);
                    sample += tone * env * _gain[v];
                }
                else
                {
                    // SSB：噪声过三个共振峰，做出"含糊人声"的听感
                    double white = _random.NextDouble() * 2 - 1;
                    _voiceNoise[v] += (white - _voiceNoise[v]) * 0.5;

                    double formants =
                        _voiceBand[0].Process(_voiceNoise[v]) * 1.0 +
                        _voiceBand[1].Process(_voiceNoise[v]) * 0.55 +
                        _voiceBand[2].Process(_voiceNoise[v]) * 0.30;

                    sample += formants * env * _gain[v] * 2.2;
                }
            }

            _master += (masterTarget - _master) * (1 - Math.Exp(-dt / 0.012));

            double output = Math.Tanh(sample * _master * 1.15) * 0.85;

            buffer[i] = (float)output;
            SampleIndex++;
        }
    }

    private void Configure(AudioScene scene)
    {
        if (ReferenceEquals(_applied, scene))
            return;

        _applied = scene;

        if (scene.SsbWide)
        {
            // 两级高通（24 dB/oct）：真 SSB 滤波器在 300 Hz 以下是死的，
            // 单级二阶只削 12 dB/oct，低频会有多余的隆隆声
            _hp.HighPass(SampleRate, 320, 0.7);
            _hp2.HighPass(SampleRate, 320, 0.9);
            _lp.LowPass(SampleRate, 2750, 0.7);
        }
        else
        {
            double center = Math.Clamp(scene.CenterHz, 300, 1500);

            // 两级同样的带通级联：裙边更陡，噪声带宽才真正跟着滤波器宽度走。
            // 单级二阶在 500 Hz 上的等效噪声带宽有 780 Hz，级联后接近标称值。
            double q = Math.Clamp(center / Math.Max(120, scene.BandwidthHz) * 1.35, 0.7, 8);

            _bp.BandPass(SampleRate, center, q);
            _bp2.BandPass(SampleRate, center, q);
        }

        // SSB 人声的三个共振峰
        _voiceBand[0].BandPass(SampleRate, 520, 2.2);
        _voiceBand[1].BandPass(SampleRate, 1250, 2.6);
        _voiceBand[2].BandPass(SampleRate, 2350, 3.0);
    }
}
