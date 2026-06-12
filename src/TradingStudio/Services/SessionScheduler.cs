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
        if (IsRestDay(DateOnly.FromDateTime(now))) return false;
        var t = now.TimeOfDay;
        var today = DateOnly.FromDateTime(now);

        // 日盘
        if (t >= DayStart && t <= DayEnd) return true;

        // 夜盘：>= 20:30 到今天结束，或从 00:00 到 03:00
        if (t >= NightStart) return true;
        if (t <= NightEnd) return !IsRestDay(today.AddDays(-1)); // 夜盘交易日的白天不是休息日

        return false;
    }

    /// <summary>距下一次会话开始的等待时间</summary>
    public TimeSpan WaitUntilNextSession()
    {
        var now = BeijingNow;
        var today = DateOnly.FromDateTime(now);

        // 尝试今天日盘
        if (!IsRestDay(today) && now.TimeOfDay < DayStart)
            return DayStart - now.TimeOfDay;

        // 尝试今天夜盘
        if (!IsRestDay(today) && now.TimeOfDay < NightStart && now.TimeOfDay < DayStart.Add(new TimeSpan(1,0,0,0)))
            return NightStart - now.TimeOfDay;

        // 尝试明天日盘
        var next = today.AddDays(1);
        while (IsRestDay(next)) next = next.AddDays(1);
        return (next.ToDateTime(new TimeOnly(8, 30)) - now).Duration();
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
