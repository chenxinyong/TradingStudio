using System.Globalization;

namespace ChanAnalysis;

/// <summary>将日K线聚合为周线 / 月线。</summary>
public static class TimeFrame
{
    /// <summary>聚合为周线（按 ISO 周，与 Python 的 %G-W%V 对齐）。</summary>
    public static List<Bar> ToWeekly(List<Bar> daily)
    {
        var result = new List<Bar>();
        int? currentWeek = null;
        int? currentYear = null;
        Bar current = default;

        foreach (var k in daily)
        {
            int wk = ISOWeek.GetWeekOfYear(k.Date);
            int yr = ISOWeek.GetYear(k.Date);

            if (currentWeek != wk || currentYear != yr)
            {
                if (currentWeek is not null) result.Add(current);
                currentWeek = wk;
                currentYear = yr;
                current = k;
            }
            else
            {
                if (k.High > current.High) current = current with { High = k.High };
                if (k.Low < current.Low) current = current with { Low = k.Low };
                current = current with { Close = k.Close, Date = k.Date };
            }
        }
        if (currentWeek is not null) result.Add(current);
        return result;
    }

    /// <summary>聚合为月线（按 yyyy-MM）。</summary>
    public static List<Bar> ToMonthly(List<Bar> daily)
    {
        var result = new List<Bar>();
        string? currentMonth = null;
        Bar current = default;

        foreach (var k in daily)
        {
            string mk = k.Date.ToString("yyyy-MM");

            if (currentMonth != mk)
            {
                if (currentMonth is not null) result.Add(current);
                currentMonth = mk;
                current = k;
            }
            else
            {
                if (k.High > current.High) current = current with { High = k.High };
                if (k.Low < current.Low) current = current with { Low = k.Low };
                current = current with { Close = k.Close, Date = k.Date };
            }
        }
        if (currentMonth is not null) result.Add(current);
        return result;
    }
}
