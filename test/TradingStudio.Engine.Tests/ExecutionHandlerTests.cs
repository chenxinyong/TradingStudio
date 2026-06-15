using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

public class ExecutionHandlerTests
{
    private static DateTime T => DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
    private const long S = TickRecord.PriceScale;
    private static long P(decimal price) => (long)(price * S);

    private static Future MakeFut(string code = "rb", decimal limit = 0.10m, decimal tickSz = 1)
        => new() { Code = code, TickSize = tickSz, TradingUnit = 10, PriceLimitPct = limit, MarginRate = 0.08m };

    [Fact]
    public void Submit_MarketBuy_CreatesOrder()
    {
        var h = new ExecutionHandler(new RiskController());
        var t = h.Submit(new Order { InstrumentId = "rb2608", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "test" }, "test");
        Assert.True(t.OrderId > 0);
        Assert.Single(h.ActiveOrders);
    }

    [Fact]
    public void ProcessBar_MarketBuy_FillsAtOpen()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");
        var fills = h.ProcessBar(new Bar { InstrumentId = "rb", BarTime = T,
            Open = P(3500), High = P(3550), Low = P(3490), Close = P(3520), Volume = 1000 }, MakeFut());
        Assert.Single(fills);
        Assert.Equal(3500m, fills[0].FillPrice);
    }

    [Fact]
    public void ProcessBar_LimitBuy_FillsWhenLowBreaches()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3500, StrategyId = "s" }, "s");
        var fills = h.ProcessBar(new Bar { InstrumentId = "rb", BarTime = T,
            Open = P(3520), High = P(3550), Low = P(3490), Close = P(3510), Volume = 1000 }, MakeFut());
        Assert.Single(fills);
        Assert.True(fills[0].FillPrice <= 3500m);
    }

    [Fact]
    public void ProcessBar_PriceLimitUp_BuyRejected()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");
        var fills = h.ProcessBar(new Bar { InstrumentId = "rb", BarTime = T,
            Open = P(3500), High = P(3700), Low = P(3500), Close = P(3700), Volume = 1000 }, MakeFut(limit: 0.05m));
        Assert.Empty(fills);
    }

    [Fact]
    public void Cancel_ActiveOrder_RemovesFromActive()
    {
        var h = new ExecutionHandler(new RiskController());
        var t = h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");
        var ok = h.Cancel(t.OrderId);
        Assert.True(ok);
        Assert.Empty(h.ActiveOrders);
    }
}
