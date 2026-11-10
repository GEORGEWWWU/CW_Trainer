using System.Text;

namespace CWTrainer.Desktop;

/// <summary>电码表里的一行。</summary>
public sealed record MorseEntry(char Symbol, string Code)
{
    /// <summary>字符本身，绑定用。</summary>
    public string SymbolText => Symbol.ToString();

    /// <summary>点划显示，用 · 和 — 便于和界面预览保持一致。</summary>
    public string CodeText => string.Join(' ', Code.Select(c => c == '.' ? "·" : "—"));
}

/// <summary>
/// 摩斯码字符表。Entries 保持展示顺序，字典由它生成，保证两者一致。
/// </summary>
public static class MorseTable
{
    public const char Unknown = '¿';

    private static readonly MorseEntry[] Entries =
    {
        new('A', ".-"),    new('B', "-..."),  new('C', "-.-."),  new('D', "-.."),
        new('E', "."),     new('F', "..-."),  new('G', "--."),   new('H', "...."),
        new('I', ".."),    new('J', ".---"),  new('K', "-.-"),   new('L', ".-.."),
        new('M', "--"),    new('N', "-."),    new('O', "---"),   new('P', ".--."),
        new('Q', "--.-"),  new('R', ".-."),   new('S', "..."),   new('T', "-"),
        new('U', "..-"),   new('V', "...-"),  new('W', ".--"),   new('X', "-..-"),
        new('Y', "-.--"),  new('Z', "--.."),

        new('0', "-----"), new('1', ".----"), new('2', "..---"), new('3', "...--"),
        new('4', "....-"), new('5', "....."), new('6', "-...."), new('7', "--..."),
        new('8', "---.."), new('9', "----."),

        new('.', ".-.-.-"), new(',', "--..--"), new('?', "..--.."), new('\'', ".----."),
        new('!', "-.-.--"), new('/', "-..-."),  new('(', "-.--."),  new(')', "-.--.-"),
        new('&', ".-..."),  new(':', "---..."), new(';', "-.-.-."), new('=', "-...-"),
        new('+', ".-.-."),  new('-', "-....-"), new('_', "..--.-"), new('"', ".-..-."),
        new('$', "...-..-"), new('@', ".--.-.")
    };

    private static readonly Dictionary<string, char> Map =
        Entries.ToDictionary(e => e.Code, e => e.Symbol, StringComparer.Ordinal);

    /// <summary>按展示顺序返回全部电码，供电码表界面使用。</summary>
    public static IReadOnlyList<MorseEntry> All => Entries;

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

        _commits.Add(new MorseCommit(symbol, code, false));
        _anyOutput = true;
        _lastWasSpace = false;
    }

    private void EmitWordSpace()
    {
        if (!_anyOutput || _lastWasSpace)
            return;

        _text.Append(' ');

        _commits.Add(new MorseCommit(' ', string.Empty, true));
        _lastWasSpace = true;
    }
}
