namespace CWTrainer.Desktop.Qso;

/// <summary>
/// 一段电码的"按键时间轴"，1 毫秒一格。
///
/// 真实手键发出来的东西不是节拍器：每个点划时长有 ±5% 的抖动，
/// 划/点比在 2.8–3.2 之间浮动，字符间隔偶尔会拖长。所以这里不直接
/// 用固定公式排时间，而是逐元素加抖动，听起来才像"人发的"。
///
/// 语音（SSB）电台也用同一条时间轴，只是把"通电"解释成"在说话"，
/// 用音节节奏生成，听上去是含糊的人声而不是噪声门。
/// </summary>
public sealed class MorsePlan
{
    /// <summary>1ms 一格的按键状态。</summary>
    public required bool[] Key { get; init; }

    /// <summary>整段循环长度（毫秒），包含尾部静默。</summary>
    public required int CycleMs { get; init; }

    public required int Wpm { get; init; }

    public required string Text { get; init; }

    /// <summary>这段里最长的一个元素（毫秒），用于界面提示"发多快"。</summary>
    public required double LongestElementMs { get; init; }

    /// <summary>按 ITU 时序把文本排成时间轴。dit = 1200/WPM 毫秒。</summary>
    public static MorsePlan Build(string text, int wpm, Random random, int trailingGapMs = 2500)
    {
        wpm = Math.Clamp(wpm, 5, 60);

        double unit = 1200.0 / wpm;               // 1 个点的时长
        double Jitter(double percent) => 1 + (random.NextDouble() * 2 - 1) * percent;

        int capacity = (int)(text.Length * unit * 26) + trailingGapMs + 2000;
        var key = new bool[Math.Max(1024, capacity)];

        int pos = 0;
        double longest = 0;

        void Mark(double durationMs)
        {
            int ms = Math.Max(1, (int)Math.Round(durationMs));

            for (int i = 0; i < ms && pos + i < key.Length; i++)
                key[pos + i] = true;

            pos += ms;
            longest = Math.Max(longest, ms);
        }

        void Gap(double durationMs)
        {
            int ms = Math.Max(0, (int)Math.Round(durationMs));
            pos = Math.Min(key.Length, pos + ms);
        }

        bool firstChar = true;

        foreach (char raw in text)
        {
            if (raw == ' ')
            {
                Gap(unit * 7 * Jitter(0.06));
                firstChar = true;
                continue;
            }

            string? code = CodeOf(raw);

            if (code == null)
                continue;

            if (!firstChar)
                Gap(unit * 3 * Jitter(0.12));      // 字符间隔（新手常拖长，抖大一点）

            bool firstSymbol = true;

            foreach (char symbol in code)
            {
                if (!firstSymbol)
                    Gap(unit * 1 * Jitter(0.10));  // 符号间隔

                Mark(symbol == '.' ? unit * Jitter(0.05) : unit * 3 * Jitter(0.04));
                firstSymbol = false;
            }

            firstChar = false;
        }

        Gap(trailingGapMs);

        int cycle = Math.Clamp(pos, 1000, 120_000);

        if (cycle > key.Length)
            Array.Resize(ref key, cycle);

        return new MorsePlan
        {
            Key = key,
            CycleMs = cycle,
            Wpm = wpm,
            Text = text,
            LongestElementMs = longest
        };
    }

    /// <summary>SSB 的"说话"节奏：4–6 个音节/秒，成串出现，中间有换气停顿。</summary>
    public static MorsePlan BuildSpeech(int seed, int cycleMs = 12_000)
    {
        var random = new Random(seed);
        var key = new bool[cycleMs];
        int pos = 200;

        while (pos < cycleMs - 400)
        {
            // 一句话 1.5–4 秒，内部按 ~5 音节/秒 通断
            int phraseMs = 1500 + random.Next(2500);

            for (int i = 0; i < phraseMs && pos < cycleMs; i += 190)
            {
                int syllable = 90 + random.Next(110);
                int silence = 190 - syllable;

                for (int k = 0; k < syllable && pos + k < cycleMs; k++)
                    key[pos + k] = true;

                pos += syllable + silence;
            }

            pos += 500 + random.Next(1200);       // 换气
        }

        return new MorsePlan
        {
            Key = key,
            CycleMs = cycleMs,
            Wpm = 0,
            Text = "",
            LongestElementMs = 200
        };
    }

    private static string? CodeOf(char ch)
    {
        foreach (MorseEntry entry in MorseTable.All)
            if (entry.Symbol == char.ToUpperInvariant(ch))
                return entry.Code;

        return null;
    }
}
