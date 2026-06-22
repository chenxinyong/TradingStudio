using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;

namespace TradingStudio.Data.Tests;

public class BarAggregatorTests
{
    // Note: Volume/Turnover in CTP are CUMULATIVE (当日累计).
    // Tests must use monotonically increasing values.
    private long _cumVol, _cumTO;

    private TickRecord MakeTick(long price, long incrVol = 1, double oi = 0)
    {
        _cumVol += incrVol;
        _cumTO += (long)((double)price / TickRecord.PriceScale * incrVol);
        var ts = new DateTimeOffset(2026, 6, 23, 9, 0, 0, TimeSpan.Zero)
            .AddSeconds(_cumVol).ToUnixTimeMilliseconds();
        return new TickRecord
        {
            ExchangeTimestamp = ts,
            LastPrice = price,
            Volume = _cumVol,
            Turnover = _cumTO,
            OpenInterest = oi,
        };
    }

    [Fact]
    public void SingleTick_FlushesOneBar()
    {
        var bars = new List<Bar>();
        using var agg = new BarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(35_630_000_000L, 5, 100), "ag2608", new DateOnly(2026, 6, 23));
        agg.Flush();

        Assert.Single(bars);
        var bar = bars[0];
        Assert.Equal("ag2608", bar.InstrumentId);
        Assert.Equal(3563.0, bar.OpenDouble);
        Assert.Equal(3563.0, bar.CloseDouble);
        Assert.Equal(5L, bar.Volume);
    }

    [Fact]
    public void MultipleTicksSameMinute_Aggregates()
    {
        var bars = new List<Bar>();
        using var agg = new BarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(30_000_000_000L, 10, 50), "cu", new DateOnly(2026, 6, 23));
        agg.Feed(MakeTick(31_000_000_000L, 20, 60), "cu", new DateOnly(2026, 6, 23));
        agg.Feed(MakeTick(29_000_000_000L, 15, 55), "cu", new DateOnly(2026, 6, 23));
        agg.Feed(MakeTick(30_500_000_000L, 5, 70), "cu", new DateOnly(2026, 6, 23));
        agg.Flush();

        Assert.Single(bars);
        var bar = bars[0];
        Assert.Equal(3000.0, bar.OpenDouble);
        Assert.Equal(3100.0, bar.HighDouble);
        Assert.Equal(2900.0, bar.LowDouble);
        Assert.Equal(3050.0, bar.CloseDouble);
        Assert.Equal(50L, bar.Volume);   // sum of increments: 10+20+15+5
        Assert.Equal(70.0, bar.OpenInterest);
        Assert.Equal(4, bar.TickCount);
    }

    [Fact]
    public void DifferentInstruments_ProduceSeparateBars()
    {
        var bars = new List<Bar>();
        using var agg = new BarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(10_000_000_000L, 1), "ag", new DateOnly(2026, 6, 23));
        agg.Feed(MakeTick(20_000_000_000L, 1), "cu", new DateOnly(2026, 6, 23));
        agg.Feed(MakeTick(11_000_000_000L, 1), "ag", new DateOnly(2026, 6, 23));
        agg.Flush();

        Assert.Equal(2, bars.Count);
    }

    [Fact]
    public void Flush_ClearsBuffer()
    {
        var bars = new List<Bar>();
        using var agg = new BarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(10_000_000_000L, 1), "rb", new DateOnly(2026, 6, 23));
        agg.Flush();
        Assert.Single(bars);

        agg.Feed(MakeTick(20_000_000_000L, 1), "rb", new DateOnly(2026, 6, 23));
        agg.Flush();
        Assert.Equal(2, bars.Count);
    }

    [Fact]
    public void Dispose_CleansUpResources()
    {
        var bars = new List<Bar>();
        var agg = new BarAggregator();
        agg.OnBar += bars.Add;
        agg.Feed(MakeTick(10_000_000_000L, 1), "test", new DateOnly(2026, 6, 23));
        agg.Dispose();
        Assert.Single(bars);
    }
}
