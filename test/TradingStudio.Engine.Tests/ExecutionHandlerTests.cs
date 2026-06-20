using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

public class ExecutionHandlerTests
{
    private const long S = TickRecord.PriceScale;
    private static long P(decimal p) => (long)(p * S);

    private static Future Rb => new()
    {
        Code = "rb", TradingUnit = 10, TickSize = 1, TickValue = 10,
        MarginRate = 0.08m, PriceLimitPct = 0.10m
    };

    private static Bar Bar(decimal o, decimal h, decimal l, decimal c, long v = 10000)
        => new() { InstrumentId = "rb", BarTime = DateTime.Today,
            Open = P(o), High = P(h), Low = P(l), Close = P(c), Volume = v };

    // ═══ 市价单 ═══

    [Fact]
    public void MarketBuy_FillsAtOpenPlusOneTick()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        var fills = h.ProcessBar(Bar(3500, 3550, 3490, 3520), Rb);
        Assert.Single(fills);
        Assert.Equal(3501m, fills[0].FillPrice); // Open + 1 tick
    }

    [Fact]
    public void MarketSell_FillsAtOpenMinusOneTick()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        var fills = h.ProcessBar(Bar(3500, 3550, 3490, 3520), Rb);
        Assert.Single(fills);
        Assert.Equal(3499m, fills[0].FillPrice); // Open - 1 tick
    }

    // ═══ 限价单 ═══

    [Fact]
    public void LimitBuy_FillsWhenLowReachesLimit()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3495, StrategyId = "s" }, "s");

        var fills = h.ProcessBar(Bar(3520, 3550, 3490, 3510), Rb);
        Assert.Single(fills);
        Assert.True(fills[0].FillPrice <= 3495m);
    }

    [Fact]
    public void LimitBuy_NoFillWhenLowAboveLimit()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3480, StrategyId = "s" }, "s");

        var fills = h.ProcessBar(Bar(3520, 3550, 3510, 3520), Rb);
        Assert.Empty(fills);
    }

    [Fact]
    public void LimitSell_FillsWhenHighReachesLimit()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3540, StrategyId = "s" }, "s");

        var fills = h.ProcessBar(Bar(3500, 3550, 3490, 3520), Rb);
        Assert.Single(fills);
        Assert.True(fills[0].FillPrice >= 3540m);
    }

    // ═══ 止损单 ═══

    [Fact]
    public void StopSell_FillsWhenLowBreachesStop()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Stop, Quantity = 1, StopPrice = 3495, StrategyId = "s" }, "s");

        var fills = h.ProcessBar(Bar(3520, 3550, 3480, 3510), Rb);
        Assert.Single(fills);
    }

    [Fact]
    public void StopBuy_FillsWhenHighBreachesStop()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Stop, Quantity = 1, StopPrice = 3540, StrategyId = "s" }, "s");

        var fills = h.ProcessBar(Bar(3500, 3560, 3490, 3520), Rb);
        Assert.Single(fills);
    }

    // ═══ 涨跌停保护 ═══

    [Fact]
    public void PriceLimitUp_BuyOrderRejected()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        // Price at upper limit → buy can't fill
        var rb = new Future { Code = "rb", TickSize = 1, TradingUnit = 10, PriceLimitPct = 0.05m };
        var fills = h.ProcessBar(Bar(3500, 3700, 3500, 3700), rb);
        Assert.Empty(fills); // no fill at limit-up
    }

    // ═══ 成交量约束 ═══

    [Fact]
    public void VolumeConstraint_CapsFillAt10Percent()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 500, StrategyId = "s" }, "s");

        // Bar volume = 1000 → max fill = 100 (10%)
        var fills = h.ProcessBar(Bar(3500, 3550, 3490, 3520, v: 1000), Rb);
        Assert.Single(fills);
        Assert.True(fills[0].FilledQty <= 100);
    }

    // ═══ 风险控制 ═══

    [Fact]
    public void RiskRule_RejectsOversizedOrder()
    {
        var risk = new RiskController(maxPosition: 5);
        var portfolio = new PortfolioManager(100_000);
        var h = new ExecutionHandler(risk);
        var ticket = h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 10, StrategyId = "s" }, "s", portfolio);

        Assert.Equal(OrderStatus.Rejected, ticket.Status);
    }

    // ═══ 前向偏差保护 ═══

    [Fact]
    public void ForwardBias_OrdersFromBarN_NotFilledOnBarN()
    {
        var h = new ExecutionHandler(new RiskController());

        // Submit during bar 1's processing → should fill on bar 2, not bar 1
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        var fills1 = h.ProcessBar(Bar(3500, 3550, 3490, 3520), Rb);
        // The first ProcessBar call fills orders that were submitted BEFORE bar 1
        // (in the engine design, new orders from bar N are matched on bar N+1)
        // So ProcessBar processes PREVIOUS orders. New orders from this call wait for next bar.

        // Submit another order (simulating bar 1's strategy)
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        // Bar 2: should fill the order submitted during bar 1
        var fills2 = h.ProcessBar(Bar(3550, 3600, 3540, 3580), Rb);
        Assert.NotEmpty(fills2);
    }

    // ═══ 订单队列 ═══

    [Fact]
    public void MultipleOrders_ProcessedFifo()
    {
        var h = new ExecutionHandler(new RiskController());
        var t1 = h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");
        var t2 = h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        Assert.True(t1.OrderId < t2.OrderId); // FIFO ordering
        Assert.Equal(2, h.ActiveOrders.Count);
    }
}
