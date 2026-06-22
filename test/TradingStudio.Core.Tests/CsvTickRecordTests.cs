using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

public class CsvTickRecordTests
{
    [Fact]
    public void FromTickRecord_SetsL1Depth()
    {
        var tick = new TickRecord
        {
            ExchangeTimestamp = 1719100000000,
            LastPrice = 35_630_000_000L,
            BidPrice1 = 35_620_000_000L, BidVolume1 = 12,
            AskPrice1 = 35_640_000_000L, AskVolume1 = 8,
            Volume = 1234, Turnover = 567890, OpenInterest = 99999,
        };

        var row = CsvTickRecord.FromTickRecord(tick, "ag2608", "SHFE", new DateOnly(2026, 6, 23));

        Assert.Equal("ag2608", row.InstrumentId);
        Assert.Equal("SHFE", row.ExchangeId);
        Assert.Equal("20260623", row.TradingDay);
        Assert.Equal(3563.0, row.LastPrice);
        Assert.Equal(3562.0, row.BidPrice1);
        Assert.Equal(12, row.BidVolume1);
        Assert.Equal(3564.0, row.AskPrice1);
        Assert.Equal(8, row.AskVolume1);
        Assert.Equal(1234, row.Volume);
        Assert.Equal(567890.0, row.Turnover);
        Assert.Equal(99999.0, row.OpenInterest);
        Assert.Equal(0.0, row.BidPrice2);  // L2-L5 default to 0
        Assert.Equal(0, row.BidVolume5);
    }

    [Fact]
    public void FromTickRecord_UsesLastPriceForOHLC()
    {
        var tick = new TickRecord { ExchangeTimestamp = 0, LastPrice = 10_000_000_000L };
        var row = CsvTickRecord.FromTickRecord(tick, "cu", "", new DateOnly(2026, 1, 1));
        Assert.Equal(1000.0, row.LastPrice);
        Assert.Equal(1000.0, row.OpenPrice);
        Assert.Equal(1000.0, row.HighestPrice);
        Assert.Equal(1000.0, row.LowestPrice);
    }

    [Fact]
    public void FromTickRecord_ZeroVolumePrice_Works()
    {
        var tick = new TickRecord { ExchangeTimestamp = 0 };
        var row = CsvTickRecord.FromTickRecord(tick, "rb", "", new DateOnly(2026, 1, 1));
        Assert.Equal(0.0, row.LastPrice);
        Assert.Equal(0, row.Volume);
    }

    [Fact]
    public void Struct_IsReadonlyRecord()
    {
        var r1 = new CsvTickRecord { InstrumentId = "ag" };
        var r2 = r1 with { InstrumentId = "cu" };
        Assert.Equal("ag", r1.InstrumentId); // unchanged
        Assert.Equal("cu", r2.InstrumentId); // copy
    }
}
