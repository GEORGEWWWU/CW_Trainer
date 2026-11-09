using System.Text;

namespace CWTrainer.Desktop;

/// <summary>
/// 把用户输入整理成设备能拍发的字符序列。
/// 纯函数、不碰界面，方便脱离 WPF 单独测试。
/// </summary>
public static class MorseText
{
    /// <summary>中文标点统一折成电码表里的 ASCII 标点。</summary>
    private static readonly Dictionary<char, char> PunctuationMap = new()
    {
        ['，'] = ',', ['。'] = '.', ['、'] = '/', ['；'] = ';', ['：'] = ':',
        ['？'] = '?', ['！'] = '!', ['（'] = '(', ['）'] = ')',
        ['“'] = '"', ['”'] = '"', ['‘'] = '\'', ['’'] = '\'',
        ['—'] = '-', ['–'] = '-', ['…'] = '.', ['《'] = '(', ['》'] = ')',
        ['·'] = '.', ['～'] = '-'
    };

    private static readonly HashSet<char> Supported =
        new(MorseTable.All.Select(e => e.Symbol));

    /// <summary>电码表里是否存在这个字符（转大写后）。</summary>
    public static bool IsSupported(char ch) => Supported.Contains(char.ToUpperInvariant(ch));

    /// <summary>
    /// 折标点、转大写、把连续空白压成一个空格、丢掉电码表里没有的字符。
    /// dropped 会带回被丢掉的字符（去重），供界面提示。
    /// </summary>
    public static string Normalize(string? raw, out List<char> dropped)
    {
        dropped = new List<char>();

        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        bool lastWasSpace = true;   // 开头不留空格

        foreach (char original in raw)
        {
            char ch = original;

            if (ch is '\r' or '\n' or '\t')
                ch = ' ';

            if (PunctuationMap.TryGetValue(ch, out char mapped))
                ch = mapped;

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            ch = char.ToUpperInvariant(ch);

            if (!Supported.Contains(ch))
            {
                if (!dropped.Contains(ch))
                    dropped.Add(ch);
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        while (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;

        return sb.ToString();
    }

    /// <summary>
    /// 输入框的实时过滤：只保留电码表里有的字符，字母立刻转大写，
    /// 中文标点就地折成 ASCII。空白（含换行）原样保留，交给发送前的 Normalize 压缩。
    /// </summary>
    public static string SanitizeInput(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);

        foreach (char original in raw)
        {
            if (char.IsWhiteSpace(original))
            {
                sb.Append(original);
                continue;
            }

            char ch = original;

            if (PunctuationMap.TryGetValue(ch, out char mapped))
                ch = mapped;

            ch = char.ToUpperInvariant(ch);

            if (Supported.Contains(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>按固定长度切分，供 SEND,TEXT 分片下发。</summary>
    public static IEnumerable<string> Chunk(string text, int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        for (int i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
