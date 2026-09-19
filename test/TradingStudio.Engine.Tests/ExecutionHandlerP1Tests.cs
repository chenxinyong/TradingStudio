using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// P1 批（风控与执行完整性）测试：
/// P0-07/08 在途订单计入风控/购买力；P0-09 反向开仓保证金按净新增敞口；P0-10 实盘幻成交防护。
/// </summary>
public class ExecutionHandlerP1Tests
{
    private static readonly FutureRegistry Reg = FutureRegistry.LoadFromJson("""
        {
          "symbols": [
            { "id":1, "exchange":"SHFE", "code":"rb", "name":"螺纹钢", "category":"black",
              "deliveryType":"physical", "tradingUnit":10, "unitName":"吨", "tickSize":1,
              "tickValue":10, "priceLimitPct":0.10, "marginRate":0.08, "feePerLot":5, "months":"1-12" }
          ]
        }
        """);

    private class MockPortfolio : IPortfolioState
    {
        public decimal Cash { get; set; } = 100_000;
        public decimal Equity { get; set; } = 100_000;
        public decimal MarginUsed { get; set; }
        public decimal StartingCapital { get; set; } = 100_000;
        public decimal PeakEquity { get; set; } = 100_000;
        public decimal TodayPnL { get; set; }
        public decimal TotalPnL { get; set; }
        public PositionSnapshot? Position { get; set; }
        public PositionSnapshot? GetPosition(string strategyId, string instrumentId) => Position;
        public IReadOnlyList<PositionSnapshot> AllPositions => Position is null ? [] : [Position];
        public IReadOnlyList<Order> ActiveOrders => [];
        public IReadOnlyList<Trade> TradeHistory => [];
        public IReadOnlyList<SubPortfolioState> SubPortfolios { get; set; } = [];
        public ReconcileStatus ReconcileStatus { get; set; } = ReconcileStatus.NotReconciled;
    }

    // ═══════════════════════════════════════════
    // P0-10 幻成交防护：实盘禁止限价/止损单
    // ═══════════════════════════════════════════

    [Fact]
    public void Live_SubmitLimitOrder_Rejected()
    {
        var exec = new ExecutionHandler(new RiskController(100, 100, 1.0m)) { IsLive = true };
        var ticket = exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3500,
        }, "s");
        Assert.Equal(OrderStatus.Rejected, ticket.Status);
        Assert.Empty(exec.ActiveOrders);   // 未进入在途，不产生幻成交
    }

    [Fact]
    public void Live_SubmitStopOrder_Rejected()
    {
        var exec = new ExecutionHandler(new RiskController(100, 100, 1.0m)) { IsLive = true };
        var ticket = exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Stop, Quantity = 1, StopPrice = 3400,
        }, "s");
        Assert.Equal(OrderStatus.Rejected, ticket.Status);
        Assert.Empty(exec.ActiveOrders);
    }

    [Fact]
    public void Backtest_SubmitLimitOrder_Accepted()
    {
        // P0-10 只针对实盘：回测下限价/止损单仍是合法的撮合单类型
        var exec = new ExecutionHandler(new RiskController(100, 100, 1.0m)) { IsLive = false };
        var ticket = exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 1, LimitPrice = 3500,
        }, "s");
        Assert.Equal(OrderStatus.Submitted, ticket.Status);
        Assert.Single(exec.ActiveOrders);
    }

    // ═══════════════════════════════════════════
    // P0-07/08 在途订单计入风控（持仓上限）
    // ═══════════════════════════════════════════

    [Fact]
    public void Risk_InFlightOrders_CountedTowardPositionLimit()
    {
        var exec = new ExecutionHandler(new RiskController(maxPosition: 5, maxOrderQty: 100, maxDrawdown: 1.0m));
        var pf = new MockPortfolio();

        // 首单 5 手：无在途，0 + 5 = 5 ≤ 5 → 通过并进入在途
        Assert.Equal(OrderStatus.Submitted, exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Buy, Type = OrderType.Market, Quantity = 5,
        }, "s", pf).Status);

        // 第二单 2 手：有效仓位 = 在途 5 + 本单 2 = 7 > 5 → 拒（若不算在途，0+2=2 会误通过）
        Assert.Equal(OrderStatus.Rejected, exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Buy, Type = OrderType.Market, Quantity = 2,
        }, "s", pf).Status);
    }

    [Fact]
    public void Risk_InFlightOrders_NetOppositeDirectionCorrectly()
    {
        // 在途买 3 后卖 2：净敞口 3 - 2 = 1 ≤ 5 → 通过（方向感知净额，不是简单相加）
        var exec = new ExecutionHandler(new RiskController(maxPosition: 5, maxOrderQty: 100, maxDrawdown: 1.0m));
        var pf = new MockPortfolio();
        Assert.Equal(OrderStatus.Submitted, exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Buy, Type = OrderType.Market, Quantity = 3,
        }, "s", pf).Status);
        Assert.Equal(OrderStatus.Submitted, exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Sell, Type = OrderType.Market, Quantity = 2,
        }, "s", pf).Status);
    }

    // ═══════════════════════════════════════════
    // P0-09 反向开仓保证金：按净新增敞口计
    // ═══════════════════════════════════════════

    [Fact]
    public void BuyingPower_ReverseOpen_ChargesMarginOnNetNewExposure()
    {
        // 持多 2 手（在途）、卖 4 手 = 平 2 + 开空 2，净新增敞口 = 2。
        // 旧公式 |−2|−|+2|=0 会误判为纯平仓免保证金；新公式按 2 手收保证金（3500×10×2×0.08=5600）。
        var exec = new ExecutionHandler(new RiskController(100, 100, 1.0m), Reg);

        // 在途买 2（portfolio=null 跳过风控，仅进入在途）
        exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 2, LimitPrice = 3500,
        }, "s");

        // 现金 0：任何保证金都付不起。新公式 addedLots=2 → 保证金 5600 > 0 → 拒；
        // 旧公式 addedLots=0 → 免保证金直接放行（本测试若回归会失败）。
        var pf = new MockPortfolio { Cash = 0 };
        var ticket = exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Limit, Quantity = 4, LimitPrice = 3500,
        }, "s", pf);
        Assert.Equal(OrderStatus.Rejected, ticket.Status);
    }

    [Fact]
    public void BuyingPower_ReverseOpen_WithEnoughCash_Passes()
    {
        var exec = new ExecutionHandler(new RiskController(100, 100, 1.0m), Reg);
        exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = 2, LimitPrice = 3500,
        }, "s");

        // 现金充足：净新增 2 手保证金 5600 + 手续费 10 → 通过
        var pf = new MockPortfolio { Cash = 100_000 };
        var ticket = exec.Submit(new Order
        {
            InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Limit, Quantity = 4, LimitPrice = 3500,
        }, "s", pf);
        Assert.Equal(OrderStatus.Submitted, ticket.Status);
    }
}
