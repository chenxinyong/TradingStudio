using TradingStudio.Core.Engine;

namespace TradingStudio.Engine.Tests;

public class TradeDeduplicatorTests
{
    private static OrderEvent Fill(string tradeId, string tradeDate = "20260918", string exchange = "SHFE")
        => new()
        {
            Type = OrderEventType.Filled,
            InstrumentId = "rb",
            Direction = OrderDirection.Buy,
            Quantity = 1,
            TradeId = tradeId,
            TradeDate = tradeDate,
            ExchangeId = exchange,
        };

    [Fact]
    public void DuplicateTradeId_SecondIsDuplicate()
    {
        var dedup = new TradeDeduplicator();
        Assert.False(dedup.IsDuplicate(Fill("T123")));
        Assert.True(dedup.IsDuplicate(Fill("T123")));
    }

    [Fact]
    public void SameTradeId_DifferentExchange_NotDuplicate()
    {
        var dedup = new TradeDeduplicator();
        Assert.False(dedup.IsDuplicate(Fill("T123", exchange: "SHFE")));
        Assert.False(dedup.IsDuplicate(Fill("T123", exchange: "INE")));
    }

    [Fact]
    public void SameTradeId_DifferentTradeDate_NotDuplicate()
    {
        var dedup = new TradeDeduplicator();
        Assert.False(dedup.IsDuplicate(Fill("T123", tradeDate: "20260918")));
        Assert.False(dedup.IsDuplicate(Fill("T123", tradeDate: "20260919")));
    }

    [Fact]
    public void EmptyTradeId_Backtest_NotDeduplicated()
    {
        var dedup = new TradeDeduplicator();
        Assert.False(dedup.IsDuplicate(Fill("")));
        Assert.False(dedup.IsDuplicate(Fill("")));
    }

    [Fact]
    public void NonFillEvent_NeverDeduplicated()
    {
        var dedup = new TradeDeduplicator();
        var submitted = new OrderEvent
        {
            Type = OrderEventType.Submitted,
            TradeId = "T123",
            TradeDate = "20260918",
            ExchangeId = "SHFE",
        };
        Assert.False(dedup.IsDuplicate(submitted));
        Assert.False(dedup.IsDuplicate(submitted));
    }
}
