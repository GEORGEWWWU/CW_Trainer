using System.Text;

namespace CWTrainer.Desktop;

/// <summary>
/// 摩斯码字符表。
/// </summary>
public static class MorseTable
{
    public const char Unknown = '¿';

    private static readonly Dictionary<string, char> Map = new(StringComparer.Ordinal)
    {
        [".-"] = 'A',
        ["-..."] = 'B',
        ["-.-."] = 'C',
        ["-.."] = 'D',
        ["."] = 'E',
        ["..-."] = 'F',
        ["--."] = 'G',
        ["...."] = 'H',
        [".."] = 'I',
        [".---"] = 'J',
        ["-.-"] = 'K',
        [".-.."] = 'L',
        ["--"] = 'M',
        ["-."] = 'N',
        ["---"] = 'O',
        [".--."] = 'P',
        ["--.-"] = 'Q',
        [".-."] = 'R',
        ["..."] = 'S',
        ["-"] = 'T',
        ["..-"] = 'U',
        ["...-"] = 'V',
        [".--"] = 'W',
        ["-..-"] = 'X',
        ["-.--"] = 'Y',
        ["--.."] = 'Z',

        ["-----"] = '0',
        [".----"] = '1',
        ["..---"] = '2',
        ["...--"] = '3',
        ["....-"] = '4',
        ["....."] = '5',
        ["-...."] = '6',
        ["--..."] = '7',
        ["---.."] = '8',
        ["----."] = '9',

        [".-.-.-"] = '.',
        ["--..--"] = ',',
        ["..--.."] = '?',
        [".----."] = '\'',
        ["-.-.--"] = '!',
        ["-..-."] = '/',
        ["-.--."] = '(',
        ["-.--.-"] = ')',
        [".-..."] = '&',
        ["---..."] = ':',
        ["-.-.-."] = ';',
        ["-...-"] = '=',
        [".-.-."] = '+',
        ["-....-"] = '-',
        ["..--.-"] = '_',
        [".-..-."] = '"',
        ["...-..-"] = '$',
        [".--.-."] = '@'
    };

    public static bool TryDecode(string code, out char symbol)
        => Map.TryGetValue(code, out symbol);

    public static char Decode(string code)
        => Map.TryGetValue(code, out char symbol) ? symbol : Unknown;
}

/// <summary>
/// 一次解码输出：要么是一个字符，要么是一个单词间隔。
/// </summary>
public readonly record struct MorseCommit(char Symbol, string Code, bool IsWordSpace);

/// <summary>
/// 摩斯解码状态机。
///
/// 设计要点：
/// 1. 时间单位 unit 直接从固件给出的元素时长推导（点=1 单位，划=3 单位），
///    不再使用界面滑块上的 WPM 猜测，因此设备改了速度也不会解错。
/// 2. 字符间隔和单词间隔分别判定，并且只在“静音时间”满足条件时提交，
///    绝不每收到一个元素就立刻解码。
/// 3. 判定的时间基准是“元素结束时刻”，不是“元素开始时刻”。
///    旧实现从元素开始起算 3 个单位，而划本身就是 3 个单位，于是划一结束
///    就被切成一个字符 —— 这是 CQ CQ 被解成 TWAW 的直接原因。
/// 4. 所有方法都不碰 UI，时间由调用方传入，便于单元测试。
/// </summary>
public sealed class MorseDecoder
{
    /// <summary>字符间隔阈值（单位）。标准字符间隔是 3 单位，元素间隔是 1 单位。</summary>
    public double CharacterGapUnits { get; set; } = 2.0;

    /// <summary>单词间隔阈值（单位）。标准单词间隔是 7 单位。</summary>
    public double WordGapUnits { get; set; } = 6.0;

    public int MaxTextLength { get; set; } = 2000;

    private readonly StringBuilder _symbols = new();
    private readonly StringBuilder _text = new();
    private readonly List<MorseCommit> _commits = new();

    private uint _unitUs = 80000;   // 默认 15 WPM
    private bool _hasMark;
    private long _lastMarkEndMs;
    private bool _anyOutput;
    private bool _lastWasSpace;

    public string Text => _text.ToString();

    /// <summary>当前还没提交成字符的点划序列，例如 ". - . -"。</summary>
    public string PendingSymbols => _symbols.ToString();

    public uint UnitUs => _unitUs;

    public IReadOnlyList<MorseCommit> Commits => _commits;

    public void ClearCommits() => _commits.Clear();

    public void SetWpm(int wpm)
    {
        if (wpm is >= 5 and <= 60)
            _unitUs = (uint)(1200000 / wpm);
    }

    public void Reset()
    {
        _symbols.Clear();
        _text.Clear();
        _commits.Clear();
        _hasMark = false;
        _lastMarkEndMs = 0;
        _anyOutput = false;
        _lastWasSpace = false;
    }

    /// <summary>
    /// 收到一个元素（点或划）。
    /// </summary>
    /// <param name="isDit">true = 点，false = 划。</param>
    /// <param name="durationUs">固件报告的元素时长（微秒）。</param>
    /// <param name="deriveUnit">是否用该元素时长校准时间单位。
    /// 自动键（Iambic）由固件生成精确时序，可以校准；直键由人控制，不能校准。</param>
    /// <param name="localMarkStartMs">本元素开始的本地时刻（事件到达时刻）。</param>
    /// <param name="localMarkEndMs">本元素结束的本地时刻。</param>
    public void PushElement(
        bool isDit,
        uint durationUs,
        bool deriveUnit,
        long localMarkStartMs,
        long localMarkEndMs)
    {
        if (durationUs == 0)
            durationUs = _unitUs;

        if (deriveUnit)
        {
            uint measured = isDit ? durationUs : durationUs / 3;
            if (measured > 0)
                _unitUs = measured;
        }

        // 与上一个元素之间的静音时间，用“本次开始 - 上次结束”计算。
        if (_hasMark)
        {
            long gapMs = localMarkStartMs - _lastMarkEndMs;
            double gapUnits = gapMs * 1000.0 / _unitUs;

            if (gapUnits >= WordGapUnits)
            {
                CommitCharacter();
                EmitWordSpace();
            }
            else if (gapUnits >= CharacterGapUnits)
            {
                CommitCharacter();
            }
        }

        _symbols.Append(isDit ? '.' : '-');
        _hasMark = true;
        _lastMarkEndMs = localMarkEndMs;
    }

    /// <summary>
    /// 推进时间。由定时器周期性调用，负责提交“最后一个字符”和“最后一个单词间隔”。
    /// </summary>
    public void Tick(long localNowMs)
    {
        if (!_hasMark)
            return;

        long silenceMs = localNowMs - _lastMarkEndMs;
        if (silenceMs < 0)
            return;

        uint charGapMs = (uint)Math.Round(_unitUs * CharacterGapUnits / 1000.0);
        uint wordGapMs = (uint)Math.Round(_unitUs * WordGapUnits / 1000.0);

        if (_symbols.Length > 0 && silenceMs >= charGapMs)
            CommitCharacter();

        if (_symbols.Length == 0 && _anyOutput && !_lastWasSpace && silenceMs >= wordGapMs)
            EmitWordSpace();
    }

    private void CommitCharacter()
    {
        if (_symbols.Length == 0)
            return;

        string code = _symbols.ToString();
        _symbols.Clear();

        char symbol = MorseTable.Decode(code);
        _text.Append(symbol);
        TrimText();

        _commits.Add(new MorseCommit(symbol, code, false));
        _anyOutput = true;
        _lastWasSpace = false;
    }

    private void EmitWordSpace()
    {
        if (!_anyOutput || _lastWasSpace)
            return;

        _text.Append(' ');
        TrimText();

        _commits.Add(new MorseCommit(' ', string.Empty, true));
        _lastWasSpace = true;
    }

    private void TrimText()
    {
        int overflow = _text.Length - MaxTextLength;
        if (overflow > 0)
            _text.Remove(0, overflow);
    }
}
