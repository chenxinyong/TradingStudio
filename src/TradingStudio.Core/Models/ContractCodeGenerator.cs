namespace TradingStudio.Core.Models;

/// <summary>
/// 根据品种的合约月份模式，生成当前活跃的合约代码列表。
/// </summary>
public static class ContractCodeGenerator
{
    public static IReadOnlyList<string> Generate(Future f, int? year = null)
    {
        year ??= DateTime.Today.Year;
        var months = ParseMonths(f.Months);
        var codes = new List<string>();

        // 当年 + 下一年
        foreach (var y in new[] { year.Value, year.Value + 1 })
        {
            foreach (var m in months)
            {
                codes.Add(MakeCode(f.Code, f.Exchange, y, m));
            }
        }

        // 过滤：只保留当前已上市的合约
        var now = DateTime.Today;
        return codes.Where(c => !IsExpired(c, now)).ToList();
    }

    /// <summary>生成所有品种的合约代码</summary>
    public static IReadOnlyList<string> GenerateAll(IEnumerable<Future> futures, int? year = null)
    {
        var all = new List<string>();
        foreach (var f in futures)
            all.AddRange(Generate(f, year));
        return all;
    }

    /// <summary>CTP 一次最多订阅 100 个合约，分批</summary>
    public static IEnumerable<string[]> BatchSubscribe(IEnumerable<Future> futures, int batchSize = 80)
    {
        var all = GenerateAll(futures);
        for (int i = 0; i < all.Count; i += batchSize)
            yield return all.Skip(i).Take(batchSize).ToArray();
    }

    // === internal ===

    private static string MakeCode(string symbolCode, ExchangeCode exchange, int year, int month)
    {
        return exchange == ExchangeCode.CZCE
            ? $"{symbolCode}{(year % 100) / 10}{month:D2}"  // TA608
            : $"{symbolCode}{year % 100:D2}{month:D2}";       // cu2607
    }

    private static List<int> ParseMonths(string months)
    {
        if (string.IsNullOrEmpty(months)) return Enumerable.Range(1, 12).ToList();

        var m = months.Trim();

        // "1～12月" or "1~12月"
        if (m.Contains("～") || m.Contains("~"))
        {
            var parts = m.Split('～', '~', '月');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var s) && int.TryParse(parts[1].Trim('月'), out var e))
                return Enumerable.Range(s, e - s + 1).ToList();
        }

        // "1,3,5,7,9,11"
        if (m.All(c => char.IsDigit(c) || c == ','))
            return m.Split(',').Select(int.Parse).ToList();

        // "季月(3,6,9,12)" or "双月(2,4,6,8,10,12)"
        if (m.Contains("(") && m.Contains(")"))
        {
            var inner = m[(m.IndexOf('(') + 1)..m.IndexOf(')')];
            return inner.Split(',').Select(int.Parse).ToList();
        }

        // "当月,下月,后两个季月" (CFFEX) — 近似处理
        var now = DateTime.Today;
        if (m.Contains("当月")) return CffexMonths(now);

        // Fallback
        return Enumerable.Range(1, 12).ToList();
    }

    private static List<int> CffexMonths(DateTime now)
    {
        var months = new HashSet<int>();
        // 当月
        months.Add(now.Month);
        // 下月
        months.Add(now.Month == 12 ? 1 : now.Month + 1);
        // 后两个季月 (3,6,9,12)
        var qms = new[] { 3, 6, 9, 12 };
        var active = qms.Where(m => m > now.Month).Take(2).ToList();
        if (active.Count < 2)
            active.AddRange(qms.Take(2 - active.Count));
        foreach (var m in active) months.Add(m);
        return months.OrderBy(m => m).ToList();
    }

    private static bool IsExpired(string code, DateTime now)
    {
        // 简单判断：如果是去年的合约且月份很早，就过期了
        // 实际到期日需要查 contracts 表，这里做粗略过滤
        var (sym, year, month) = ParseCode(code);
        var expiry = new DateTime(year, month, 15); // 粗估：每月15号到期
        return expiry.AddMonths(1) < now; // 到期后1个月就不再活跃
    }

    /// <summary>
    /// 郑商所短码 → 标准四位数年份码。
    /// SR609 → SR2609, TA608 → TA2608, FG511 → FG2511
    /// </summary>
    public static string Normalize(string instrumentId)
    {
        if (string.IsNullOrEmpty(instrumentId)) return instrumentId;
        // CZCE 短码：2大写字母 + 1位年代 + 2位月份
        if (instrumentId.Length == 5
            && char.IsUpper(instrumentId[0])
            && char.IsUpper(instrumentId[1])
            && char.IsDigit(instrumentId[2])
            && char.IsDigit(instrumentId[3])
            && char.IsDigit(instrumentId[4]))
        {
            var decade = int.Parse(instrumentId[2..3]);  // "6" → 202x
            var month  = int.Parse(instrumentId[3..5]);  // "09" → 9
            var year = decade + (decade >= 2 ? 2020 : 2030); // 2-9 → 2022-2029, 0-1 → 2030-2031
            return $"{instrumentId[..2]}{year % 100:D2}{month:D2}";
        }
        return instrumentId;
    }

    private static (string sym, int year, int month) ParseCode(string code)
    {
        // cu2607 → (cu, 2026, 7)
        // TA608 → (TA, 2026, 8)
        var numPart = code[^4..]; // "2607" or "A608" — need to handle CZCE
        if (char.IsLetter(numPart[0]))
        {
            // CZCE: TA608
            var d = int.Parse(code[^3..^2]);     // decade
            var m = int.Parse(code[^2..]);        // month
            return (code[..^3], 2020 + d, m);
        }
        else
        {
            var y = 2000 + int.Parse(code[^4..^2]);
            var m = int.Parse(code[^2..]);
            return (code[..^4], y, m);
        }
    }
}
