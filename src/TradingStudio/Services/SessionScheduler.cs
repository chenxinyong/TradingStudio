namespace TradingStudio.Services;

/// <summary>
/// 期货交易时段调度 — 日盘 8:30-15:30，夜盘 20:30-03:00，周末休市。
/// 所有时间为北京时间 (UTC+8)。
/// </summary>
public class SessionScheduler
{
    private static readonly TimeSpan DayStart   = new(8, 30, 0);
    private static readonly TimeSpan DayEnd     = new(15, 30, 0);
    private static readonly TimeSpan NightStart = new(20, 30, 0);
    private static readonly TimeSpan NightEnd   = new(3, 0, 0);   // 次日凌晨

    private readonly HashSet<DateOnly> _holidays = new();

    /// <summary>北京时间现在</summary>
    public static DateTime BeijingNow => DateTime.UtcNow.AddHours(8);

    /// <summary>当前是否在交易时段内</summary>
    public bool IsInSession()
    {
        var now = BeijingNow;
        var t = now.TimeOfDay;
        var today = DateOnly.FromDateTime(now);

        // 夜盘属于次日交易日：>= 20:30 时检查明天是否休息日
        // （如周日 20:30 夜盘属于周一，周五 20:30 无夜盘因为周六休息）
        var effectiveDay = t >= NightStart ? today.AddDays(1) : today;
        if (IsRestDay(effectiveDay)) return false;

        // 日盘
        if (t >= DayStart && t <= DayEnd) return true;

        // 夜盘：>= 20:30 到今天结束，或从 00:00 到 03:00
        if (t >= NightStart) return true;
        if (t <= NightEnd) return !IsRestDay(today.AddDays(-1)); // 前一晚夜盘的前置条件

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

        // 尝试今天夜盘（夜盘属于明天交易日，检查明天是否休息日）
        if (!IsRestDay(today.AddDays(1)) && t < NightStart)
            return NightStart - t;

        // 找下一个有效交易日（日盘）
        var nextDay = today.AddDays(1);
        while (IsRestDay(nextDay)) nextDay = nextDay.AddDays(1);

        // 检查 nextDay 前一天的夜盘（夜盘属于 nextDay，在前一天 20:30 开始）
        var nightDay = nextDay.AddDays(-1);
        if (nightDay >= today)
        {
            var nightTime = nightDay.ToDateTime(new TimeOnly(20, 30));
            if (nightTime > now)
                return nightTime - now;
        }

        // 回退到 nextDay 的日盘
        return (nextDay.ToDateTime(new TimeOnly(8, 30)) - now).Duration();
    }

    /// <summary>是否是休息日（周末或节假日）</summary>
    public bool IsRestDay(DateOnly day)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        if (_holidays.Contains(day)) return true;
        return false;
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
        // 夜盘结束 03:00（次日）
        if (t >= NightStart)
            return now.Date.AddDays(1).Add(NightEnd);
        // 夜盘延续 00:00-03:00
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
