namespace CWTrainer.Desktop.Qso;

/// <summary>
/// 按 ITU 呼号结构生成呼号：前缀（国家）+ 分区数字 + 后缀（1–4 个字母）。
///
/// 前缀、数字范围、后缀长度都按各国真实分配来，不能随便拼。举几个容易搞错的：
///   · 中国    B[G/H/I/D/A/B/C/E/F/K/L] + 1 位分区 + 2–3 字母（BG6ABC、BY1AA）
///   · 日本    J[A-Z] + 0–9 + 2–3 字母（JA1ABC）
///   · 美国    K/W/N 或两位 A[A-K] + 0–9 + 2–3 字母（K1ABC、W7QQ）
///   · 德国    D[A-Z] + 0–9 + 1–3 字母（DL2XX）
///   · 俄罗斯  R[A-Z]/U[A-Z] + 0–9 + 2–3 字母（UA9QQ）
///   · 英国    G/M + 0–9 + 2–3 字母（G4ABC）
///   · 瑞士    HB9 + 1–3 字母（分区数字固定 9）
///
/// 低波段只通得到附近的国家，高波段才有 DX —— 所以低频段优先抽"邻国"，
/// 20m 以上放开全世界。偶尔还会带 /P（便携）、/QRP（小功率）后缀，真实通联里很常见。
/// </summary>
public static class CallsignFactory
{
    private sealed record Region(
        string Name,
        string[] Prefixes,
        int[] Digits,
        int MinSuffix,
        int MaxSuffix,
        int LocalWeight,
        int DxWeight);

    private static readonly Region[] Regions =
    {
        // 中国及其周边（低波段常听到）
        new("中国", new[] { "BG", "BH", "BI", "BD", "BY" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 30, 14),
        new("日本", new[] { "JA", "JE", "JF", "JG", "JH", "JR", "JS" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 24, 18),
        new("韩国", new[] { "HL", "DS", "DT", "6K", "6L" }, new[] { 0, 1, 2, 3, 4, 5 }, 2, 3, 10, 6),
        new("俄罗斯", new[] { "RA", "RV", "RW", "RX", "UA", "UB", "UD", "UF", "UG", "UI", "UK", "UL", "UM", "UN", "UO", "UQ", "UR", "UT", "UV", "UW", "UX", "UZ" }, new[] { 0, 1, 3, 4, 6, 9 }, 2, 3, 14, 12),
        new("中国台湾", new[] { "BV", "BW", "BX" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 6, 4),
        new("印度尼西亚", new[] { "YB", "YC", "YD", "YE", "YF", "YG", "YH" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 6, 4),
        new("菲律宾", new[] { "DU", "DV", "DW", "DX", "DZ" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 4, 3),
        new("泰国", new[] { "HS", "E2" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 4, 2),
        new("马来西亚", new[] { "9M2", "9M6", "9W" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 3, 3, 2),
        new("越南", new[] { "3W", "XV" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 3, 2),
        new("印度", new[] { "VU" }, new[] { 2, 3, 4 }, 2, 3, 4, 4),
        new("澳大利亚", new[] { "VK" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 3, 5, 6),
        new("新西兰", new[] { "ZL" }, new[] { 1, 2, 3, 4 }, 2, 3, 3, 4),

        // 欧洲
        new("德国", new[] { "DA", "DB", "DC", "DD", "DF", "DG", "DH", "DJ", "DK", "DL", "DM", "DO", "DP", "DQ", "DR" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 1, 3, 3, 14),
        new("英国", new[] { "G", "M", "2E0", "2E1" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 3, 3, 12),
        new("意大利", new[] { "I", "IK", "IZ", "IW" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 2, 9),
        new("法国", new[] { "F" }, new[] { 1, 2, 3, 4, 5, 6, 8, 9 }, 2, 3, 2, 8),
        new("西班牙", new[] { "EA" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 2, 7),
        new("波兰", new[] { "SP", "SQ", "SO", "SN", "HF" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 7),
        new("芬兰", new[] { "OH" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 2, 6),
        new("荷兰", new[] { "PA", "PB", "PC", "PD", "PE", "PI" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 6),
        new("捷克", new[] { "OK", "OL" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 3, 1, 5),
        new("瑞典", new[] { "SA", "SB", "SC", "SD", "SE", "SF", "SG", "SH", "SI", "SJ", "SK", "SL", "SM" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, 2, 3, 1, 5),
        new("瑞士", new[] { "HB9" }, Array.Empty<int>(), 1, 3, 1, 4),
        new("奥地利", new[] { "OE" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 4),
        new("乌克兰", new[] { "UR", "US", "UT", "UU", "UV", "UW", "UX", "UY", "UZ" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 3, 2, 5),
        new("挪威", new[] { "LA", "LB", "LC", "LD", "LE", "LF", "LG", "LH", "LI", "LJ", "LK", "LL", "LM", "LN" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 4),
        new("丹麦", new[] { "OZ", "OU", "OV", "OW", "OX" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 3, 1, 3),
        new("比利时", new[] { "ON", "OO", "OP", "OQ", "OR", "OS", "OT" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 4),
        new("葡萄牙", new[] { "CT", "CS", "CR" }, new[] { 1, 2, 3, 4, 5 }, 2, 3, 1, 3),
        new("希腊", new[] { "SV", "SX", "SY", "SZ" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 3),
        new("土耳其", new[] { "TA", "TB", "TC", "YM" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 2, 3),

        // 美洲 / 非洲
        new("美国", new[] { "K", "W", "N", "AA", "AB", "AC", "AD", "AE", "AF", "AG", "AI", "AJ", "AK" }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 2, 14),
        new("加拿大", new[] { "VE", "VA", "VO", "VY" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 7),
        new("巴西", new[] { "PY", "PP", "PT", "PU", "PV", "PW", "PX" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 7),
        new("阿根廷", new[] { "LU", "LW", "LO", "LP", "LQ" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 4),
        new("南非", new[] { "ZS", "ZR", "ZT" }, new[] { 1, 2, 3, 4, 5, 6 }, 2, 3, 1, 4),
        new("以色列", new[] { "4X", "4Z" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 2, 3, 1, 3)
    };

    private static readonly char[] Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

    /// <summary>抽一个符合现实规律的呼号。local = true 时偏向同一地区（低波段）。</summary>
    public static string Next(Random random, bool local)
    {
        Region region = Pick(random, local);

        string prefix = region.Prefixes[random.Next(region.Prefixes.Length)];

        // HB9 这类前缀自带分区数字
        string digit = region.Digits.Length == 0
            ? string.Empty
            : region.Digits[random.Next(region.Digits.Length)].ToString();

        int length = region.MinSuffix == region.MaxSuffix
            ? region.MinSuffix
            : random.Next(region.MinSuffix, region.MaxSuffix + 1);

        var suffix = new char[length];

        for (int i = 0; i < length; i++)
            suffix[i] = Letters[random.Next(Letters.Length)];

        string call = prefix + digit + new string(suffix);

        // 真实通联里常见的后缀：/P 便携、/QRP 小功率、/M 车载
        double roll = random.NextDouble();

        if (roll < 0.05)
            call += "/P";
        else if (roll < 0.08)
            call += "/QRP";
        else if (roll < 0.09)
            call += "/M";

        return call;
    }

    /// <summary>该呼号的"国家"名称，用于界面提示（也方便按地区分组）。</summary>
    public static string CountryOf(string call)
    {
        foreach (Region region in Regions)
            foreach (string prefix in region.Prefixes)
                if (call.StartsWith(prefix, StringComparison.Ordinal))
                    return region.Name;

        return "未知";
    }

    private static Region Pick(Random random, bool local)
    {
        int total = 0;

        foreach (Region region in Regions)
            total += local ? region.LocalWeight : region.DxWeight;

        int ticket = random.Next(Math.Max(1, total));

        foreach (Region region in Regions)
        {
            ticket -= local ? region.LocalWeight : region.DxWeight;

            if (ticket < 0)
                return region;
        }

        return Regions[0];
    }
}
