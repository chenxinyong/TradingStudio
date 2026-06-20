using TradingStudio.Core.Engine;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine.Tests;

public class RiskControllerTests
{
    // ═══ 轻量 Mock：只暴露风控需要的状态 ═══
    private class MockPortfolio : IPortfolioState
    {
        public decimal Cash { get; set; } = 100_000;
        public decimal Equity { get; set; } = 100_000;
        public decimal MarginUsed { get; set; }
        public decimal StartingCapital { get; set; } = 100_000;
        public decimal PeakEquity { get; set; } = 100_000;
        public decimal TodayPnL { get; set; }
        public decimal TotalPnL { get; set; }
        public Position? Position { get; set; }
        public Position? GetPosition(string instrumentId) => Position;
        public IReadOnlyList<Position> AllPositions => Position is null ? [] : [Position];
        public IReadOnlyList<Order> ActiveOrders => [];
        public IReadOnlyList<Trade> TradeHistory => [];
        public IReadOnlyList<SubPortfolioState> SubPortfolios { get; set; } = [];
    }

    private static Order Buy(string inst = "rb", int qty = 1, string sid = "s1")
        => new() { InstrumentId = inst, Direction = OrderDirection.Buy, Quantity = qty, StrategyId = sid };

    private static Order Sell(string inst = "rb", int qty = 1, string sid = "s1")
        => new() { InstrumentId = inst, Direction = OrderDirection.Sell, Quantity = qty, StrategyId = sid };

    // ═══════════════════════════════════════════
    // MaxPositionPerInstrumentRule
    // ═══════════════════════════════════════════

    [Fact]
    public void MaxPosition_BuyWithinLimit_Passes()
    {
        var rc = new RiskController(maxPosition: 5);
        var pf = new MockPortfolio();
        var result = rc.CheckPreOrder(Buy(qty: 2), pf);
        Assert.True(result.Passed);
    }

    [Fact]
    public void MaxPosition_BuyExceedsLimit_Rejected()
    {
        var rc = new RiskController(maxPosition: 3);
        var pf = new MockPortfolio();
        var result = rc.CheckPreOrder(Buy(qty: 5), pf);
        Assert.False(result.Passed);
    }

    [Fact]
    public void MaxPosition_BuyAddsToExisting_PassesAtLimit()
    {
        var rc = new RiskController(maxPosition: 5);
        var pf = new MockPortfolio
        {
            Position = new Position { InstrumentId = "rb", Quantity = 3, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Buy(qty: 2), pf);
        Assert.True(result.Passed); // 3 + 2 = 5 ≤ 5
    }

    [Fact]
    public void MaxPosition_BuyAddsToExisting_ExceedsLimit()
    {
        var rc = new RiskController(maxPosition: 5);
        var pf = new MockPortfolio
        {
            Position = new Position { InstrumentId = "rb", Quantity = 3, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Buy(qty: 3), pf);
        Assert.False(result.Passed); // 3 + 3 = 6 > 5
    }

    [Fact]
    public void MaxPosition_SellReducesLongPosition_Passes()
    {
        var rc = new RiskController(maxPosition: 5);
        var pf = new MockPortfolio
        {
            Position = new Position { InstrumentId = "rb", Quantity = 5, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Sell(qty: 3), pf);
        Assert.True(result.Passed); // 5 - 3 = 2 ≤ 5 (方向感知!)
    }

    [Fact]
    public void MaxPosition_SellOpensShort_WithinLimit()
    {
        var rc = new RiskController(maxPosition: 5);
        var pf = new MockPortfolio(); // flat
        var result = rc.CheckPreOrder(Sell(qty: 3), pf);
        Assert.True(result.Passed); // 0 - 3 = -3, abs = 3 ≤ 5
    }

    [Fact]
    public void MaxPosition_SellOpensShort_ExceedsLimit()
    {
        var rc = new RiskController(maxPosition: 3);
        var pf = new MockPortfolio(); // flat
        var result = rc.CheckPreOrder(Sell(qty: 5), pf);
        Assert.False(result.Passed); // 0 - 5 = -5, abs = 5 > 3
    }

    [Fact]
    public void MaxPosition_SellAddsToExistingShort_ExceedsLimit()
    {
        var rc = new RiskController(maxPosition: 5);
        var pf = new MockPortfolio
        {
            Position = new Position { InstrumentId = "rb", Quantity = -3, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Sell(qty: 3), pf);
        Assert.False(result.Passed); // -3 - 3 = -6, abs = 6 > 5
    }

    [Fact]
    public void MaxPosition_BuyClosesShort_Passes()
    {
        var rc = new RiskController(maxPosition: 5);
        var pf = new MockPortfolio
        {
            Position = new Position { InstrumentId = "rb", Quantity = -3, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Buy(qty: 3), pf);
        Assert.True(result.Passed); // -3 + 3 = 0 ≤ 5 (平仓!)
    }

    // ═══════════════════════════════════════════
    // MaxDrawdownRule
    // ═══════════════════════════════════════════

    [Fact]
    public void Drawdown_WithinLimit_Passes()
    {
        var rc = new RiskController(maxDrawdown: 0.25m); // 25%
        var pf = new MockPortfolio { Equity = 90_000, StartingCapital = 100_000 };
        var result = rc.CheckPreOrder(Buy(), pf);
        Assert.True(result.Passed); // DD = 10% < 25%
    }

    [Fact]
    public void Drawdown_AtLimit_Passes()
    {
        var rc = new RiskController(maxDrawdown: 0.25m);
        var pf = new MockPortfolio { Equity = 75_000, StartingCapital = 100_000 };
        var result = rc.CheckPreOrder(Buy(), pf);
        Assert.True(result.Passed); // DD = 25% ≤ 25%
    }

    [Fact]
    public void Drawdown_ExceedsLimit_RejectsNewLong()
    {
        var rc = new RiskController(maxDrawdown: 0.25m);
        var pf = new MockPortfolio { Equity = 70_000, StartingCapital = 100_000 };
        var result = rc.CheckPreOrder(Buy(), pf);
        Assert.False(result.Passed); // DD = 30% > 25%
    }

    [Fact]
    public void Drawdown_ExceedsLimit_RejectsNewShort()
    {
        var rc = new RiskController(maxDrawdown: 0.25m);
        var pf = new MockPortfolio { Equity = 70_000, StartingCapital = 100_000 };
        var result = rc.CheckPreOrder(Sell(), pf);
        Assert.False(result.Passed); // DD = 30% > 25%, 开空也拦截
    }

    [Fact]
    public void Drawdown_ExceedsLimit_AllowsCloseLong()
    {
        var rc = new RiskController(maxDrawdown: 0.25m);
        var pf = new MockPortfolio
        {
            Equity = 70_000, StartingCapital = 100_000,
            Position = new Position { InstrumentId = "rb", Quantity = 2, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Sell(qty: 2), pf);
        Assert.True(result.Passed); // 平多 = 减仓，允许穿透
    }

    [Fact]
    public void Drawdown_ExceedsLimit_AllowsCloseShort()
    {
        var rc = new RiskController(maxDrawdown: 0.25m);
        var pf = new MockPortfolio
        {
            Equity = 70_000, StartingCapital = 100_000,
            Position = new Position { InstrumentId = "rb", Quantity = -2, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Buy(qty: 2), pf);
        Assert.True(result.Passed); // 平空 = 减仓，允许穿透
    }

    [Fact]
    public void Drawdown_ExceedsLimit_StillRejectsAddToShort()
    {
        var rc = new RiskController(maxDrawdown: 0.25m);
        var pf = new MockPortfolio
        {
            Equity = 70_000, StartingCapital = 100_000,
            Position = new Position { InstrumentId = "rb", Quantity = -1, AvgPrice = 3500 }
        };
        var result = rc.CheckPreOrder(Sell(qty: 2), pf);
        Assert.False(result.Passed); // 加空 = 风险增加，仍然拦截
    }

    // ═══════════════════════════════════════════
    // MaxOrderQuantityRule
    // ═══════════════════════════════════════════

    [Fact]
    public void OrderQty_WithinLimit_Passes()
    {
        var rc = new RiskController(maxPosition: 20, maxOrderQty: 10); // maxPosition 放够，只测单笔上限
        var pf = new MockPortfolio();
        var result = rc.CheckPreOrder(Buy(qty: 10), pf);
        Assert.True(result.Passed);
    }

    [Fact]
    public void OrderQty_ExceedsLimit_Rejected()
    {
        var rc = new RiskController(maxPosition: 20, maxOrderQty: 10);
        var pf = new MockPortfolio();
        var result = rc.CheckPreOrder(Buy(qty: 11), pf);
        Assert.False(result.Passed);
    }

    [Fact]
    public void OrderQty_Zero_Allowed()
    {
        var rc = new RiskController(maxPosition: 20, maxOrderQty: 10);
        var pf = new MockPortfolio();
        var result = rc.CheckPreOrder(Buy(qty: 0), pf);
        Assert.True(result.Passed); // 0 ≤ 10
    }

    // ═══════════════════════════════════════════
    // 组合规则 — 多规则同时检查
    // ═══════════════════════════════════════════

    [Fact]
    public void MultipleRules_FirstFailureStopsCheck()
    {
        var rc = new RiskController(maxPosition: 5, maxOrderQty: 3, maxDrawdown: 0.25m);
        var pf = new MockPortfolio
        {
            Equity = 70_000, StartingCapital = 100_000  // DD = 30% > 25%
        };
        // 下单量和持仓都在限制内，但回撤超限 → 应被 DrawdownRule 拦截
        var result = rc.CheckPreOrder(Buy(qty: 2), pf);
        Assert.False(result.Passed);
        Assert.Contains("Drawdown", result.Reason);
    }

    [Fact]
    public void MultipleRules_AllPass_ReturnsPass()
    {
        var rc = new RiskController(maxPosition: 5, maxOrderQty: 10, maxDrawdown: 0.25m);
        var pf = new MockPortfolio(); // 全部正常
        var result = rc.CheckPreOrder(Buy(qty: 3), pf);
        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════
    // MaxStrategyDrawdownRule
    // ═══════════════════════════════════════════

    [Fact]
    public void StrategyDD_WithinLimit_Passes()
    {
        var rc = new RiskController(maxStrategyDrawdown: 0.20m); // 策略回撤上限20%
        var pf = new MockPortfolio
        {
            SubPortfolios = [new SubPortfolioState { StrategyId = "s1", PeakEquity = 100_000, Equity = 90_000 }]
        };
        var result = rc.CheckPreOrder(Buy(), pf);
        Assert.True(result.Passed); // DD=10% < 20%
    }

    [Fact]
    public void StrategyDD_ExceedsLimit_RejectsNewLong()
    {
        var rc = new RiskController(maxStrategyDrawdown: 0.20m);
        var pf = new MockPortfolio
        {
            SubPortfolios = [new SubPortfolioState { StrategyId = "s1", PeakEquity = 100_000, Equity = 70_000 }]
        };
        var result = rc.CheckPreOrder(Buy(), pf);
        Assert.False(result.Passed); // DD=30% > 20%
    }

    [Fact]
    public void StrategyDD_ExceedsLimit_AllowsCloseLong()
    {
        var rc = new RiskController(maxStrategyDrawdown: 0.20m);
        var pf = new MockPortfolio
        {
            Position = new Position { InstrumentId = "rb", Quantity = 2, AvgPrice = 3500 },
            SubPortfolios = [new SubPortfolioState { StrategyId = "s1", PeakEquity = 100_000, Equity = 70_000 }]
        };
        var result = rc.CheckPreOrder(Sell(qty: 2), pf);
        Assert.True(result.Passed); // 平多 = 减仓，允许穿透
    }

    [Fact]
    public void StrategyDD_ExceedsLimit_StillRejectsAddPosition()
    {
        var rc = new RiskController(maxStrategyDrawdown: 0.20m);
        var pf = new MockPortfolio
        {
            Position = new Position { InstrumentId = "rb", Quantity = 1, AvgPrice = 3500 },
            SubPortfolios = [new SubPortfolioState { StrategyId = "s1", PeakEquity = 100_000, Equity = 70_000 }]
        };
        var result = rc.CheckPreOrder(Buy(qty: 2), pf);
        Assert.False(result.Passed); // 加仓 = 风险增加，拦截
    }

    [Fact]
    public void StrategyDD_DifferentStrategy_Independent()
    {
        var rc = new RiskController(maxStrategyDrawdown: 0.20m);
        var pf = new MockPortfolio
        {
            SubPortfolios = [new SubPortfolioState { StrategyId = "s1", PeakEquity = 100_000, Equity = 70_000 }]
        };
        // s2 不在 SubPortfolios 中 → 无数据 → 放行
        var order = new Order { InstrumentId = "rb", Direction = OrderDirection.Buy, Quantity = 1, StrategyId = "s2" };
        var result = rc.CheckPreOrder(order, pf);
        Assert.True(result.Passed);
    }

    [Fact]
    public void StrategyDD_CombinedWithGlobalDD()
    {
        // 策略回撤=0.20, 全局回撤=0.25 — 两个规则都激活
        var rc = new RiskController(maxDrawdown: 0.25m, maxStrategyDrawdown: 0.20m);
        var pf = new MockPortfolio
        {
            Equity = 80_000, StartingCapital = 100_000, // 全局DD=20% < 25%
            SubPortfolios = [new SubPortfolioState { StrategyId = "s1", PeakEquity = 100_000, Equity = 70_000 }]
        };
        // 全局DD OK, 但策略DD=30% > 20% → 应被策略规则拦截
        var result = rc.CheckPreOrder(Buy(), pf);
        Assert.False(result.Passed);
    }
}
