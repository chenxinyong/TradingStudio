using TradingStudio.Core.Models;

namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// Bar 适配器 — TradingStudio.Core.Models.Bar ↔ ChanLunBar 互转。
/// Core.Bar 使用 long×10^7 存储价格，ChanLunBar 使用 double 存储原始价格。
/// </summary>
public static class BarAdapter
{
    /// <summary>Core.Bar → ChanLunBar</summary>
    public static ChanLunBar FromCoreBar(Bar bar) => new()
    {
        Dt = bar.BarTime,
        Open = bar.OpenDouble,
        High = bar.HighDouble,
        Low = bar.LowDouble,
        Close = bar.CloseDouble,
        Volume = bar.Volume,
    };

    /// <summary>批量转换</summary>
    public static List<ChanLunBar> FromCoreBars(IEnumerable<Bar> bars) =>
        bars.Select(FromCoreBar).ToList();

    /// <summary>
    /// 聚合到指定周期 (从 1min Bar 流)。
    /// 使用与 pandas resample('Xmin', closed='left', label='left') 完全相同的边界逻辑。
    /// </summary>
    /// <param name="oneMinBars">1分钟K线（已按时间排序）</param>
    /// <param name="periodMinutes">目标周期（分钟），如15</param>
    public static List<ChanLunBar> Resample(IReadOnlyList<Bar> oneMinBars, int periodMinutes = 15)
    {
        if (oneMinBars.Count == 0)
            return [];

        var period = TimeSpan.FromMinutes(periodMinutes);

        // 使用 Floor 对齐到周期边界（与 pandas .floor() 一致）
        // 例: 00:01 → 00:00, 00:15 → 00:15
        static DateTime FloorToPeriod(DateTime dt, int periodMin)
        {
            // 计算当天从零点开始的分钟数，向下取整到 periodMin 的倍数
            int totalMinutes = dt.Hour * 60 + dt.Minute;
            int floored = totalMinutes / periodMin * periodMin;
            return new DateTime(dt.Year, dt.Month, dt.Day, floored / 60, floored % 60, 0);
        }

        // 按 floor 边界分组 (closed='left': 包含左边界，不含右边界)
        var groups = oneMinBars.GroupBy(b => FloorToPeriod(b.BarTime, periodMinutes));

        var result = new List<ChanLunBar>();
        foreach (var g in groups)
        {
            var bars = g.OrderBy(b => b.BarTime).ToList();
            result.Add(new ChanLunBar
            {
                Dt = g.Key,
                Open = bars[0].OpenDouble,
                High = bars.Max(b => b.HighDouble),
                Low = bars.Min(b => b.LowDouble),
                Close = bars[^1].CloseDouble,
                Volume = bars.Sum(b => b.Volume),
            });
        }

        return result.OrderBy(b => b.Dt).ToList();
    }
}
