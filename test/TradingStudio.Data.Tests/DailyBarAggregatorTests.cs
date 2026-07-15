using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;

namespace TradingStudio.Data.Tests;

public class DailyBarAggregatorTests
{
    // Note: Volume/Turnover in CTP are CUMULATIVE (当日累计).
    // Tests must use monotonically increasing values.
    private long _cumVol, _cumTO;

    private static readonly DateOnly Day = new(2026, 7, 15);
    private static readonly DateOnly NextDay = new(2026, 7, 16);

    private TickRecord MakeTick(long price, long incrVol = 1, double oi = 0)
    {
        _cumVol += incrVol;
        _cumTO += (long)((double)price / TickRecord.PriceScale * incrVol);
        var ts = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero)
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
    public void 多Tick同交易日_OHLCV累计正确()
    {
        var bars = new List<Bar>();
        using var agg = new DailyBarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(30_000_000_000L, 10), "rb2610", Day);   // 首tick建bar, Volume=0基线
        agg.Feed(MakeTick(31_000_000_000L, 20), "rb2610", Day);
        agg.Feed(MakeTick(29_000_000_000L, 15), "rb2610", Day);
        agg.Feed(MakeTick(30_500_000_000L, 5, 88), "rb2610", Day);
        agg.FlushAll();

        var bar = Assert.Single(bars);
        Assert.Equal(3000.0, bar.OpenDouble);
        Assert.Equal(3100.0, bar.HighDouble);
        Assert.Equal(2900.0, bar.LowDouble);
        Assert.Equal(3050.0, bar.CloseDouble);
        Assert.Equal(40L, bar.Volume);   // 首tick增量不计: 20+15+5
        Assert.Equal(88.0, bar.OpenInterest);
    }

    [Fact]
    public void EmitSnapshots_发射快照且不清状态_后续Tick继续累计()
    {
        var bars = new List<Bar>();
        using var agg = new DailyBarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(30_000_000_000L, 10), "rb2610", Day);
        agg.Feed(MakeTick(31_000_000_000L, 20), "rb2610", Day);

        agg.EmitSnapshots();                                       // 重连前快照
        Assert.Single(bars);
        Assert.Equal(20L, bars[0].Volume);

        // 重连后继续喂（累计量继续单调递增，模拟断连期间场内继续成交）
        agg.Feed(MakeTick(32_000_000_000L, 30), "rb2610", Day);

        var snap = agg.GetSnapshot("rb2610", Day)!.Value;
        Assert.Equal(3000.0, snap.OpenDouble);                     // Open 未被重建
        Assert.Equal(3200.0, snap.HighDouble);
        Assert.Equal(50L, snap.Volume);                            // 20 + 30，dV 跨快照正确
    }

    [Fact]
    public void EmitSnapshots两次_快照递进_终版完整()
    {
        var bars = new List<Bar>();
        using var agg = new DailyBarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(30_000_000_000L, 10), "rb2610", Day);
        agg.Feed(MakeTick(31_000_000_000L, 20), "rb2610", Day);
        agg.EmitSnapshots();

        agg.Feed(MakeTick(29_000_000_000L, 5), "rb2610", Day);
        agg.EmitSnapshots();

        // INSERT OR REPLACE 语义下最后一版获胜：Open 不变、Volume 单调增、Low 补全
        Assert.Equal(2, bars.Count);
        Assert.Equal(bars[0].OpenDouble, bars[1].OpenDouble);
        Assert.True(bars[1].Volume > bars[0].Volume);
        Assert.Equal(2900.0, bars[1].LowDouble);
    }

    [Fact]
    public void FlushAll_清空状态_再喂同键Tick_视为首Tick()
    {
        var bars = new List<Bar>();
        using var agg = new DailyBarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(30_000_000_000L, 10), "rb2610", Day);
        agg.Feed(MakeTick(31_000_000_000L, 20), "rb2610", Day);
        agg.FlushAll();

        agg.Feed(MakeTick(32_000_000_000L, 30), "rb2610", Day);    // 清空后 → 首tick分支
        var snap = agg.GetSnapshot("rb2610", Day)!.Value;
        Assert.Equal(3200.0, snap.OpenDouble);                     // 重建，Open=新首tick价
        Assert.Equal(0L, snap.Volume);                             // Volume 归零基线
    }

    [Fact]
    public void FlushDay_旧交易日发射移除_当前交易日保留()
    {
        var bars = new List<Bar>();
        using var agg = new DailyBarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(30_000_000_000L, 10), "rb2610", Day);
        agg.Feed(MakeTick(31_000_000_000L, 20), "rb2610", Day);
        agg.Feed(MakeTick(40_000_000_000L, 5), "ag2608", NextDay);

        agg.FlushDay(NextDay);                                     // 只清 Day < NextDay

        var bar = Assert.Single(bars);
        Assert.Equal("rb2610", bar.InstrumentId);
        Assert.Null(agg.GetSnapshot("rb2610", Day));               // 旧日已移除
        Assert.NotNull(agg.GetSnapshot("ag2608", NextDay));        // 当前交易日保留
    }

    [Fact]
    public void 重连场景_快照tick被闸门拦截时_日线不退化()
    {
        // 完整复现 2026-07-15 损坏机制的反面：
        // 正常ticks → 重连(EmitSnapshots, 不清状态; 闸门拦掉快照tick即"不喂入") → 恢复后继续喂
        var bars = new List<Bar>();
        using var agg = new DailyBarAggregator();
        agg.OnBar += bars.Add;

        agg.Feed(MakeTick(30_000_000_000L, 10), "rb2610", Day);    // 早盘
        agg.Feed(MakeTick(31_500_000_000L, 40), "rb2610", Day);
        agg.EmitSnapshots();                                       // 重连#1
        agg.EmitSnapshots();                                       // 重连#2（午休循环，快照tick全被闸门拦截）
        agg.Feed(MakeTick(30_800_000_000L, 25), "rb2610", Day);    // 午盘恢复
        agg.EmitSnapshots();                                       // 收盘快照

        var final = bars[^1];
        Assert.Equal(3000.0, final.OpenDouble);                    // O ≠ 快照价
        Assert.Equal(3150.0, final.HighDouble);
        Assert.Equal(3080.0, final.CloseDouble);
        Assert.True(final.OpenDouble != final.HighDouble || final.HighDouble != final.LowDouble); // 非退化单点
        Assert.Equal(65L, final.Volume);                           // 40+25 全程累计，非0
    }
}
