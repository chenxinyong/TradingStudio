using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// TradeSignal → TargetCombiner → Rebalancer → Order 端到端回归测试。
/// 每个环节独立测试，确保信号管线正确性。
/// </summary>
public class SignalPipelineTests
{
    private static readonly Future Rb = new()
    {
        Code = "rb", Exchange = ExchangeCode.SHFE, TradingUnit = 10,
        TickSize = 1, TickValue = 10, MarginRate = 0.08m, PriceLimitPct = 0.10m
    };

    private static readonly Future Ag = new()
    {
        Code = "ag", Exchange = ExchangeCode.SHFE, TradingUnit = 15,
        TickSize = 1, TickValue = 15, MarginRate = 0.10m, PriceLimitPct = 0.10m
    };

    private static FutureRegistry MakeRegistry() =>
        FutureRegistry.LoadFromJson("""
        {"symbols":[
            {"id":1,"exchange":"SHFE","code":"rb","name":"螺纹钢","category":"黑色金属",
             "deliveryType":"PHYSICAL","tradingUnit":10,"unitName":"吨","tickSize":1,
             "tickValue":10,"priceLimitPct":0.10,"marginRate":0.08,"months":"1~12月"},
            {"id":2,"exchange":"SHFE","code":"ag","name":"白银","category":"贵金属",
             "deliveryType":"PHYSICAL","tradingUnit":15,"unitName":"千克","tickSize":1,
             "tickValue":15,"priceLimitPct":0.10,"marginRate":0.10,"months":"1~12月"}
        ]}
        """);

    // ═══ SimpleTargetCombiner ═══

    [Fact]
    public void Combiner_EmptySignals_ReturnsEmpty()
    {
        var combiner = new SimpleTargetCombiner();
        var targets = combiner.Combine([], new FakePortfolio(100_000), MakeRegistry());
        Assert.Empty(targets);
    }

    [Fact]
    public void Combiner_SingleLongSignal_ReturnsLongTarget()
    {
        var combiner = new SimpleTargetCombiner { MaxPositions = 5, MaxLots = 5 };
        var signals = new[] { MakeSignal("rb", SignalDirection.Long, conviction: 0.8) };
        var targets = combiner.Combine(signals, new FakePortfolio(100_000), MakeRegistry());

        Assert.Single(targets);
        Assert.Equal("rb", targets[0].InstrumentId);
        Assert.True(targets[0].TargetQuantity > 0, "Long should give positive quantity");
        Assert.Equal(0.8, targets[0].CompositeConviction);
        Assert.Contains("s", targets[0].SourceStrategyIds);
    }

    [Fact]
    public void Combiner_FlatSignal_ReturnsZeroQuantity()
    {
        var combiner = new SimpleTargetCombiner();
        var signals = new[] { MakeSignal("rb", SignalDirection.Flat, conviction: 1.0) };
        var targets = combiner.Combine(signals, new FakePortfolio(100_000), MakeRegistry());

        Assert.Single(targets);
        Assert.Equal(0, targets[0].TargetQuantity);
    }

    [Fact]
    public void Combiner_LowConviction_FilteredOut()
    {
        var combiner = new SimpleTargetCombiner { MinConviction = 0.3 };
        var signals = new[] { MakeSignal("rb", SignalDirection.Long, conviction: 0.1) };
        var targets = combiner.Combine(signals, new FakePortfolio(100_000), MakeRegistry());

        Assert.Empty(targets);
    }

    [Fact]
    public void Combiner_SameInstrumentTwoStrategies_ConflictResolved()
    {
        // Same instrument, conflicting directions → Flat wins
        var combiner = new SimpleTargetCombiner { MaxPositions = 5, MaxLots = 5 };
        var signals = new[]
        {
            MakeSignal("rb", SignalDirection.Long, conviction: 0.9, priority: 1),
            MakeSignal("rb", SignalDirection.Flat, conviction: 1.0, priority: 2, sid: "s2"),
        };
        var targets = combiner.Combine(signals, new FakePortfolio(100_000), MakeRegistry());

        Assert.Single(targets);
        Assert.Equal(0, targets[0].TargetQuantity); // Flat wins
    }

    [Fact]
    public void Combiner_MultipleInstruments_RespectsMaxPositions()
    {
        var combiner = new SimpleTargetCombiner { MaxPositions = 2, MaxLots = 3 };
        var signals = new[]
        {
            MakeSignal("rb", SignalDirection.Long, conviction: 0.9, priority: 1),
            MakeSignal("ag", SignalDirection.Long, conviction: 0.8, priority: 2),
            MakeSignal("rb", SignalDirection.Long, conviction: 0.7, priority: 3, sid: "s3"), // duplicate, lower prio
        };
        var targets = combiner.Combine(signals, new FakePortfolio(100_000), MakeRegistry());

        Assert.Equal(2, targets.Count);
    }

    [Fact]
    public void Combiner_SameDirection_MergesToHighestConviction()
    {
        var combiner = new SimpleTargetCombiner { MaxPositions = 5, MaxLots = 5 };
        var signals = new[]
        {
            MakeSignal("rb", SignalDirection.Long, conviction: 0.5, priority: 1),
            MakeSignal("rb", SignalDirection.Long, conviction: 0.9, priority: 1, sid: "s2"),
        };
        // Same priority, same direction → higher conviction wins
        var targets = combiner.Combine(signals, new FakePortfolio(100_000), MakeRegistry());

        Assert.Single(targets);
        Assert.Equal(0.9, targets[0].CompositeConviction);
        Assert.Contains("s2", targets[0].SourceStrategyIds); // higher conviction signal wins
    }

    // ═══ Rebalancer ═══

    [Fact]
    public void Rebalancer_NoCurrentPosition_OpensFullTarget()
    {
        var reb = new Rebalancer { MinTradeLots = 1 };
        var targets = new[] { new PortfolioTarget { InstrumentId = "rb", TargetQuantity = 3 } };
        var orders = reb.GenerateOrders(targets, [], "s");

        Assert.Single(orders);
        Assert.Equal("rb", orders[0].InstrumentId);
        Assert.Equal(OrderDirection.Buy, orders[0].Direction);
        Assert.Equal(3, orders[0].Quantity);
    }

    [Fact]
    public void Rebalancer_DeltaTooSmall_NoOrder()
    {
        var reb = new Rebalancer { MinTradeLots = 2 };
        var targets = new[] { new PortfolioTarget { InstrumentId = "rb", TargetQuantity = 3 } };
        var current = new[] { MakePosition("rb", 2, OrderDirection.Buy) }; // Δ = 1 < 2 → skip
        var orders = reb.GenerateOrders(targets, current, "s");

        Assert.Empty(orders);
    }

    [Fact]
    public void Rebalancer_ReducePosition_GeneratesSellOrder()
    {
        var reb = new Rebalancer { MinTradeLots = 1 };
        var targets = new[] { new PortfolioTarget { InstrumentId = "rb", TargetQuantity = 1 } };
        var current = new[] { MakePosition("rb", 3, OrderDirection.Buy) }; // Δ = -2
        var orders = reb.GenerateOrders(targets, current, "s");

        Assert.Single(orders);
        Assert.Equal(OrderDirection.Sell, orders[0].Direction);
        Assert.Equal(2, orders[0].Quantity);
        Assert.True(orders[0].IsCloseOrder);
    }

    [Fact]
    public void Rebalancer_FlipLongToShort_GeneratesCloseOrder()
    {
        var reb = new Rebalancer { MinTradeLots = 1 };
        var targets = new[] { new PortfolioTarget { InstrumentId = "rb", TargetQuantity = -2 } };
        var current = new[] { MakePosition("rb", 3, OrderDirection.Buy) }; // Long→Short: flip
        var orders = reb.GenerateOrders(targets, current, "s");

        Assert.Single(orders);
        Assert.True(orders[0].IsCloseOrder, "Flip should close current position first");
        Assert.Equal(3, orders[0].Quantity);
    }

    [Fact]
    public void Rebalancer_TargetZero_FlattensPosition()
    {
        var reb = new Rebalancer { MinTradeLots = 1 };
        var targets = new[] { new PortfolioTarget { InstrumentId = "rb", TargetQuantity = 0 } };
        var current = new[] { MakePosition("rb", 3, OrderDirection.Buy) };
        var orders = reb.GenerateOrders(targets, current, "s");

        Assert.Single(orders);
        Assert.True(orders[0].IsCloseOrder);
        Assert.Equal(3, orders[0].Quantity);
    }

    [Fact]
    public void Rebalancer_UnchangedPosition_NoOrder()
    {
        var reb = new Rebalancer { MinTradeLots = 1 };
        var targets = new[] { new PortfolioTarget { InstrumentId = "rb", TargetQuantity = 3 } };
        var current = new[] { MakePosition("rb", 3, OrderDirection.Buy) }; // Δ = 0
        var orders = reb.GenerateOrders(targets, current, "s");

        Assert.Empty(orders);
    }

    [Fact]
    public void Rebalancer_EmptyTargets_NoOrders()
    {
        var reb = new Rebalancer();
        var current = new[] { MakePosition("rb", 3, OrderDirection.Buy) };
        var orders = reb.GenerateOrders([], current, "s");
        Assert.Empty(orders);
    }

    // ═══ Helpers ═══

    private static TradeSignal MakeSignal(string inst, SignalDirection dir, double conviction = 0.5,
        int priority = 0, string sid = "s") => new()
    {
        StrategyId = sid,
        InstrumentId = inst,
        Direction = dir,
        Conviction = conviction,
        Priority = priority,
        Reason = "test",
        Timestamp = DateTime.UtcNow,
    };

    private static Position MakePosition(string inst, int qty, OrderDirection dir) => new()
    {
        InstrumentId = inst,
        Quantity = dir == OrderDirection.Buy ? qty : -qty,
        AvgPrice = 3500,
        StrategyId = "s",
        CreatedTime = DateTimeOffset.UtcNow,
    };

    /// <summary>最小 IPortfolioState 假实现，仅用于单元测试。</summary>
    private sealed class FakePortfolio : IPortfolioState
    {
        public decimal Cash { get; }
        public decimal Equity { get; }
        public decimal MarginUsed => 0;
        public decimal StartingCapital { get; }
        public decimal PeakEquity => Equity;
        public decimal TodayPnL => 0;
        public decimal TotalPnL => 0;
        public Position? GetPosition(string id) => null;
        public IReadOnlyList<Position> AllPositions => [];
        public IReadOnlyList<Order> ActiveOrders => [];
        public IReadOnlyList<Trade> TradeHistory => [];
        public IReadOnlyList<SubPortfolioState> SubPortfolios => [];

        public FakePortfolio(decimal equity) { Equity = Cash = equity; StartingCapital = equity; }
    }
}
