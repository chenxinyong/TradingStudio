using TradingStudio.Core.Models;

namespace TradingStudio.Data.Aggregation;

/// <summary>
/// 多周期 Bar 聚合器 — 将 1min Bar 合成 N 分钟 Bar。
/// 输入: 1min Bar 流（按时间顺序）→ 输出: N-min Bar。
/// </summary>
public class MultiBarAggregator
{
    private readonly int _periodMinutes;

    public MultiBarAggregator(int periodMinutes = 5)
    {
        if (periodMinutes < 2) throw new ArgumentException("Period must be >= 2 minutes");
        _periodMinutes = periodMinutes;
    }

    /// <summary>
    /// 流式聚合。输入有序 1min Bar，yield N-min Bar。
    /// 调用方按 instrumentId 分组后传入。
    /// </summary>
    public IEnumerable<Bar> Aggregate(IEnumerable<Bar> minuteBars)
    {
        Bar? current = null;
        var bucketStart = DateTime.MinValue;

        foreach (var bar in minuteBars)
        {
            // 计算该 Bar 所属的时间桶
            var tick = bar.BarTime.Ticks;
            var periodTicks = TimeSpan.FromMinutes(_periodMinutes).Ticks;
            var bucket = new DateTime(tick / periodTicks * periodTicks, bar.BarTime.Kind);

            if (current == null || bucket != bucketStart)
            {
                // 发射上一桶
                if (current != null)
                    yield return current.Value;

                // 开始新桶
                bucketStart = bucket;
                current = new Bar
                {
                    InstrumentId = bar.InstrumentId,
                    TradingDay = bar.TradingDay,
                    BarTime = bucket,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume,
                    Turnover = bar.Turnover,
                    OpenInterest = bar.OpenInterest,
                    TickCount = bar.TickCount,
                };
            }
            else
            {
                // 更新当前桶
                var c = current.Value;
                if (bar.High > c.High) c.High = bar.High;
                if (bar.Low < c.Low) c.Low = bar.Low;
                c.Close = bar.Close;
                c.Volume += bar.Volume;
                c.Turnover += bar.Turnover;
                c.OpenInterest = bar.OpenInterest;
                c.TickCount += bar.TickCount;
                current = c;
            }
        }

        if (current != null)
            yield return current.Value;
    }
}
