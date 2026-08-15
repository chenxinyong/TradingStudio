namespace TradingStudio.Services;

/// <summary>
/// 期货交易时段调度 — 日盘 8:30-15:30，夜盘 21:00-次日 02:30，周末休市。
/// 所有时间为北京时间 (UTC+8)。
///
/// 夜盘归属规则（关键）：夜盘归属「下一个交易日」而非下一个自然日。
///   - 周一至周五晚都有夜盘；周五晚夜盘跳过周末归属下周一。
///   - 仅「法定节假日前最后一个工作日晚」夜盘暂停（下一工作日是节假日）。
/// </summary>
public class SessionScheduler
{
    private static readonly TimeSpan DayStart   = new(8, 30, 0);
    private static readonly TimeSpan DayEnd     = new(15, 30, 0);
    private static readonly TimeSpan NightStart = new(21, 0, 0);   // 夜盘统一 21:00 开盘
    private static readonly TimeSpan NightEnd   = new(2, 30, 0);   // 次日凌晨，最晚 02:30（金银/原油）

    private readonly HashSet<DateOnly> _holidays = new();

    /// <summary>北京时间现在</summary>
    public static DateTime BeijingNow => DateTime.UtcNow.AddHours(8);

    /// <summary>当前是否在交易时段内</summary>
    public bool IsInSession()
    {
        var now = BeijingNow;
        var t = now.TimeOfDay;
        var today = DateOnly.FromDateTime(now);

        // 日盘
        if (t >= DayStart && t <= DayEnd) return !IsRestDay(today);

        // 夜盘开始段：交易日当晚 21:00 起
        if (t >= NightStart) return HasNightSession(today);

        // 夜盘延续段：00:00-02:30，是前一晚夜盘的尾部
        if (t <= NightEnd) return HasNightSession(today.AddDays(-1));

        return false;
    }

    /// <summary>距下一次会话开始的等待时间</summary>
    public TimeSpan WaitUntilNextSession()
    {
        var now = BeijingNow;
        var today = DateOnly.FromDateTime(now);
        var t = now.TimeOfDay;

        // 尝试今天日盘
        if (!IsRestDay(today) && t < DayStart)
            return DayStart - t;

        // 尝试今天夜盘（今晚是交易日且节前不暂停）
        if (HasNightSession(today) && t < NightStart)
            return NightStart - t;

        // 找下一个交易日日盘
        var nextDay = today.AddDays(1);
        while (IsRestDay(nextDay)) nextDay = nextDay.AddDays(1);
        return nextDay.ToDateTime(new TimeOnly(8, 30)) - now;
    }

    /// <summary>是否是休息日（周末或节假日）</summary>
    public bool IsRestDay(DateOnly day) => IsWeekend(day) || IsHoliday(day);

    private bool IsWeekend(DateOnly day) =>
        day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private bool IsHoliday(DateOnly day) => _holidays.Contains(day);

    /// <summary>下一个工作日（跳过周末，保留节假日，供节前判断用）</summary>
    private DateOnly NextWorkingDay(DateOnly day)
    {
        var d = day.AddDays(1);
        while (IsWeekend(d)) d = d.AddDays(1);
        return d;
    }

    /// <summary>某自然日当晚是否有夜盘。</summary>
    /// 夜盘归属下一个交易日（跳过周末）——周五晚夜盘属下周一；
    /// 仅「法定节假日前最后一个工作日晚」暂停（下一工作日是节假日）。
    private bool HasNightSession(DateOnly day)
    {
        if (IsRestDay(day)) return false;          // 周末/节假日当晚无夜盘
        return !IsHoliday(NextWorkingDay(day));    // 节前最后工作日晚暂停
    }

    /// <summary>注册节假日（如 2026-01-01 元旦）</summary>
    public void AddHolidays(params DateOnly[] days)
    {
        foreach (var d in days) _holidays.Add(d);
    }

    /// <summary>返回当前时段名称</summary>
    public string SessionName()
    {
        if (!IsInSession()) return "休市";
        var t = BeijingNow.TimeOfDay;
        return t >= NightStart || t <= NightEnd ? "夜盘" : "日盘";
    }

    /// <summary>当前交易时段的结束时间（北京时间），不在时段内返回 null</summary>
    public DateTime? GetSessionEndTime()
    {
        if (!IsInSession()) return null;
        var now = BeijingNow;
        var t = now.TimeOfDay;
        // 日盘结束 15:30
        if (t >= DayStart && t <= DayEnd)
            return now.Date.Add(DayEnd);
        // 夜盘结束 02:30（次日）
        if (t >= NightStart)
            return now.Date.AddDays(1).Add(NightEnd);
        // 夜盘延续 00:00-02:30
        return now.Date.Add(NightEnd);
    }

    #region 节假日数据 (2026)

    public static SessionScheduler CreateWithHolidays()
    {
        var s = new SessionScheduler();
        // 2026 中国法定节假日（简化版，不含调休）
        s.AddHolidays(
            new(2026, 1, 1),   // 元旦
            new(2026, 2, 16), new(2026, 2, 17), new(2026, 2, 18),
            new(2026, 2, 19), new(2026, 2, 20), // 春节
            new(2026, 4, 3),   // 清明
            new(2026, 5, 1), new(2026, 5, 2), new(2026, 5, 3),
            new(2026, 5, 4),   // 劳动节
            new(2026, 6, 19),  // 端午
            new(2026, 9, 25),  // 中秋
            new(2026, 10, 1), new(2026, 10, 2), new(2026, 10, 3),
            new(2026, 10, 4), new(2026, 10, 5), new(2026, 10, 6),
            new(2026, 10, 7), new(2026, 10, 8)  // 国庆
        );
        return s;
    }

    #endregion
}
