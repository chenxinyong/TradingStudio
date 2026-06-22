using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

public class TickRecordTests
{
    [Fact]
    public void PriceScale_Is10Million()
    {
        Assert.Equal(10_000_000L, TickRecord.PriceScale);
    }

    [Fact]
    public void RecordSize_Is80Bytes()
    {
        Assert.Equal(80, TickRecord.RecordSize);
    }

    [Fact]
    public void LastPriceDouble_ConvertsCorrectly()
    {
        var t = new TickRecord { LastPrice = 35_630_000_000L };
        Assert.Equal(3563.0, t.LastPriceDouble, 6);
    }

    [Fact]
    public void BidAskSpread_CalculatesCorrectly()
    {
        var t = new TickRecord { BidPrice1 = 35_620_000_000L, AskPrice1 = 35_720_000_000L };
        Assert.Equal(100_000_000L, t.Spread);
        Assert.Equal(10.0, t.SpreadDouble, 6);
    }

    [Fact]
    public void LatencyMs_ComputesExchangeMinusLocal()
    {
        var t = new TickRecord { ExchangeTimestamp = 1000, LocalTimestamp = 1050 };
        Assert.Equal(50, t.LatencyMs);
    }

    [Fact]
    public void Flags_UpperLimit_Detected()
    {
        var t = new TickRecord { Flags = TickRecord.FlagUpperLimit };
        Assert.True(t.IsUpperLimit);
        Assert.False(t.IsLowerLimit);
        Assert.False(t.IsAuction);
    }

    [Fact]
    public void Flags_LowerLimit_Detected()
    {
        var t = new TickRecord { Flags = TickRecord.FlagLowerLimit };
        Assert.True(t.IsLowerLimit);
        Assert.False(t.IsUpperLimit);
    }

    [Fact]
    public void Flags_Auction_Detected()
    {
        var t = new TickRecord { Flags = TickRecord.FlagAuction };
        Assert.True(t.IsAuction);
    }

    [Fact]
    public void Flags_Combined()
    {
        var t = new TickRecord { Flags = TickRecord.FlagUpperLimit | TickRecord.FlagAuction };
        Assert.True(t.IsUpperLimit);
        Assert.True(t.IsAuction);
        Assert.False(t.IsLowerLimit);
    }
}
