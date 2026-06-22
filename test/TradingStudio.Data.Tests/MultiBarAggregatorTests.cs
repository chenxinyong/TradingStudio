using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;

namespace TradingStudio.Data.Tests;

public class MultiBarAggregatorTests
{
    private static Bar MakeMinBar(string inst, DateTime time, long o, long h, long l, long c, long v = 10, int ticks = 1)
    {
        return new Bar
        {
            InstrumentId = inst,
            BarTime = time,
            TradingDay = new DateOnly(time.Year, time.Month, time.Day),
            Open = o, High = h, Low = l, Close = c,
            Volume = v, TickCount = ticks,
        };
    }

    [Fact]
    public void FiveMinPeriod_AggregatesFiveBars()
    {
        var agg = new MultiBarAggregator(5);
        var input = new List<Bar>
        {
            MakeMinBar("ag", new DateTime(2026,6,23,9,0,0),   o:100,l:90,h:110,c:105, v:10, ticks:4),
            MakeMinBar("ag", new DateTime(2026,6,23,9,1,0),   o:106,l:95,h:115,c:110, v:12, ticks:3),
            MakeMinBar("ag", new DateTime(2026,6,23,9,2,0),   o:111,l:98,h:120,c:115, v:8,  ticks:2),
            MakeMinBar("ag", new DateTime(2026,6,23,9,3,0),   o:116,l:92,h:118,c:112, v:15, ticks:5),
            MakeMinBar("ag", new DateTime(2026,6,23,9,4,0),   o:113,l:96,h:117,c:108, v:11, ticks:3),
        };

        var result = agg.Aggregate(input).ToList();
        Assert.Single(result);

        var bar = result[0];
        Assert.Equal(100L, bar.Open);       // first bar's open
        Assert.Equal(120L, bar.High);       // max of all highs
        Assert.Equal(90L, bar.Low);         // min of all lows
        Assert.Equal(108L, bar.Close);      // last bar's close
        Assert.Equal(56L, bar.Volume);      // 10+12+8+15+11
        Assert.Equal(17, bar.TickCount);    // 4+3+2+5+3
    }

    [Fact]
    public void TwoPeriods_ProducesTwoBars()
    {
        var agg = new MultiBarAggregator(5);
        var input = new List<Bar>
        {
            MakeMinBar("ag", new DateTime(2026,6,23,9,0,0),  o:10,h:10,l:10,c:11, v:1),
            MakeMinBar("ag", new DateTime(2026,6,23,9,4,0),  o:11,h:15,l:11,c:14, v:2),
            MakeMinBar("ag", new DateTime(2026,6,23,9,5,0),  o:15,h:20,l:15,c:18, v:3),
            MakeMinBar("ag", new DateTime(2026,6,23,9,9,0),  o:18,h:22,l:17,c:20, v:4),
        };

        var result = agg.Aggregate(input).ToList();
        Assert.Equal(2, result.Count);

        Assert.Equal(10L, result[0].Open);
        Assert.Equal(15L, result[0].High);
        Assert.Equal(3L, result[0].Volume);   // 1+2

        Assert.Equal(15L, result[1].Open);
        Assert.Equal(22L, result[1].High);
        Assert.Equal(7L, result[1].Volume);   // 3+4
    }

    [Fact]
    public void FifteenMinPeriod_GroupsCorrectly()
    {
        var agg = new MultiBarAggregator(15);
        var input = new List<Bar>
        {
            MakeMinBar("rb", new DateTime(2026,6,23,9,0,0),  o:100,h:100,l:100,c:200, v:10),
            MakeMinBar("rb", new DateTime(2026,6,23,9,14,0), o:200,h:300,l:90,c:250, v:15),
            MakeMinBar("rb", new DateTime(2026,6,23,9,30,0), o:250,h:260,l:250,c:255, v:5),
        };

        var result = agg.Aggregate(input).ToList();
        Assert.Equal(2, result.Count);  // 9:00+9:14 in one bucket; 9:30 in next

        Assert.Equal(100L, result[0].Open);
        Assert.Equal(300L, result[0].High);
        Assert.Equal(90L, result[0].Low);
        Assert.Equal(250L, result[0].Close);
        Assert.Equal(25L, result[0].Volume);

        Assert.Equal(250L, result[1].Open);
        Assert.Equal(255L, result[1].Close);
        Assert.Equal(5L, result[1].Volume);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var agg = new MultiBarAggregator(5);
        Assert.Empty(agg.Aggregate([]));
    }

    [Fact]
    public void InvalidPeriod_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MultiBarAggregator(1));
    }
}
