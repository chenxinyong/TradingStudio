using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

public class TickExecutionTests
{
    private const long S = TickRecord.PriceScale; // 10^7
    private static long P(decimal p) => (long)(p * S);

    private static Future Rb => new()
    {
        Code = "rb", TradingUnit = 10, TickSize = 1, TickValue = 10,
        MarginRate = 0.08m, PriceLimitPct = 0.10m, FeeRate = 0.0001
    };

    /// <summary>构造 Tick：Bid=卖一, Ask=买一, Vol=当日累计, Last=最新价</summary>
    private static TickRecord Tick(decimal last, decimal bid, decimal ask, long vol, long ts = 0)
        => new()
        {
            LastPrice = P(last),
            BidPrice1 = P(bid),
            AskPrice1 = P(ask),
            Volume = vol,
            ExchangeTimestamp = ts > 0 ? ts : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

    // ═══ 市价单 ═══

    [Fact]
    public void MarketBuy_FillsAtAskPlusOneTick()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 2, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 100), "rb", Rb);
        Assert.Single(fills);
        Assert.Equal(3501m, fills[0].FillPrice); // Ask(3500) + TickSize(1) = 3501
        Assert.Equal(2, fills[0].FilledQty);
    }

    [Fact]
    public void MarketSell_FillsAtBidMinusOneTick()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3500, bid: 3500, ask: 3501, vol: 100), "rb", Rb);
        Assert.Single(fills);
        Assert.Equal(3499m, fills[0].FillPrice); // Bid(3500) - TickSize(1) = 3499
    }

    [Fact]
    public void MarketOrder_ZeroLiquidity_NoFill()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        // Vol 没变化 → IncrementalVolume = 0 → 无成交
        h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 0), "rb", Rb); // 初始化累计量
        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 0), "rb", Rb); // 增量=0
        Assert.Empty(fills);
    }

    [Fact]
    public void MarketOrder_PartialFill_LiquidityConstraint()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 10, StrategyId = "s" }, "s");

        // 先初始化累计量
        h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 0), "rb", Rb);
        // 增量=3，订单要10手 → 部分成交3手
        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 3), "rb", Rb);

        Assert.Single(fills);
        Assert.Equal(3, fills[0].Quantity);    // 本次成交
        Assert.Equal(3, fills[0].FilledQty);   // 累计已成交
        Assert.Equal(OrderEventType.PartiallyFilled, fills[0].Type);
    }

    [Fact]
    public void MarketOrder_MultipleTicks_FullyFilled()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 5, StrategyId = "s" }, "s");

        // 第一次 Tick: 增量 2, 成交 2, 剩余 3
        var f1 = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 2), "rb", Rb);
        Assert.Single(f1);
        Assert.Equal(2, f1[0].Quantity);    // 本次成交量
        Assert.Equal(2, f1[0].FilledQty);   // 累计已成交
        Assert.Equal(OrderEventType.PartiallyFilled, f1[0].Type);

        // 第二次 Tick: 增量 4 (vol: 2→6), 再成交 3 → 全部成交
        var f2 = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 6), "rb", Rb);
        Assert.Single(f2);
        Assert.Equal(3, f2[0].Quantity);    // 本次成交 3
        Assert.Equal(5, f2[0].FilledQty);   // 累计 2+3=5
        Assert.Equal(OrderEventType.Filled, f2[0].Type);
    }

    // ═══ 限价单 ═══

    [Fact]
    public void LimitBuy_FillsWhenAskWithinLimit()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3500, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 100), "rb", Rb);
        Assert.Single(fills);
        Assert.Equal(3500m, fills[0].FillPrice);
    }

    [Fact]
    public void LimitBuy_NoFillWhenAskAboveLimit()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3499, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 100), "rb", Rb);
        Assert.Empty(fills); // Ask=3500 > Limit=3499 → 不成交
    }

    [Fact]
    public void LimitSell_FillsWhenBidAtOrAboveLimit()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3500, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3500, bid: 3500, ask: 3501, vol: 100), "rb", Rb);
        Assert.Single(fills);
        Assert.Equal(3500m, fills[0].FillPrice);
    }

    [Fact]
    public void LimitSell_NoFillWhenBidBelowLimit()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3500, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3500, bid: 3498, ask: 3500, vol: 100), "rb", Rb);
        Assert.Empty(fills); // Bid=3498 < Limit=3500 → 不成交
    }

    // ═══ 止损单 ═══

    [Fact]
    public void StopBuy_TriggersWhenAskAtOrAboveStop()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Stop, Quantity = 1, StopPrice = 3520, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3525, bid: 3524, ask: 3525, vol: 100), "rb", Rb);
        Assert.Single(fills);
        Assert.Equal(3525m, fills[0].FillPrice);
    }

    [Fact]
    public void StopBuy_NoTriggerWhenAskBelowStop()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Stop, Quantity = 1, StopPrice = 3520, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 100), "rb", Rb);
        Assert.Empty(fills);
    }

    [Fact]
    public void StopSell_TriggersWhenBidAtOrBelowStop()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Stop, Quantity = 1, StopPrice = 3480, StrategyId = "s" }, "s");

        var fills = h.ProcessTick(Tick(last: 3475, bid: 3476, ask: 3478, vol: 100), "rb", Rb);
        Assert.Single(fills);
        Assert.Equal(3476m, fills[0].FillPrice);
    }

    // ═══ 滑点验证 ═══

    [Fact]
    public void MarketBuy_SlippageEqualsOneTick()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        // Ask=3500, TickSize=1 → fillPrice=3501, 滑点=1 tick
        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 100), "rb", Rb);
        Assert.Equal(3501m, fills[0].FillPrice);
        // 滑点 = FillPrice - Ask = 3501 - 3500 = 1 tick ✓
    }

    [Fact]
    public void MarketSell_SlippageEqualsOneTick()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Market, Quantity = 1, StrategyId = "s" }, "s");

        // Bid=3500, TickSize=1 → fillPrice=3499, 滑点=1 tick
        var fills = h.ProcessTick(Tick(last: 3500, bid: 3500, ask: 3501, vol: 100), "rb", Rb);
        Assert.Equal(3499m, fills[0].FillPrice);
    }

    // ═══ 多订单优先级 ═══

    [Fact]
    public void MultipleOrders_FifoOrder()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 5, StrategyId = "s" }, "s");
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Market, Quantity = 3, StrategyId = "s" }, "s");

        // 增量=6，先到先得：第1单5手全成交，第2单成交1手（剩余2手）
        h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 0), "rb", Rb); // init
        var fills = h.ProcessTick(Tick(last: 3500, bid: 3499, ask: 3500, vol: 6), "rb", Rb);

        Assert.Equal(2, fills.Count);
        Assert.Equal(5, fills[0].FilledQty);
        Assert.Equal(OrderEventType.Filled, fills[0].Type);
        Assert.Equal(1, fills[1].FilledQty);
        Assert.Equal(OrderEventType.PartiallyFilled, fills[1].Type);
    }
}
