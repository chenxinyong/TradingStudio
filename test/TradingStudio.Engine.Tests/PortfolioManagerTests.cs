using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

public class PortfolioManagerTests
{
    private static readonly FutureRegistry Reg = FutureRegistry.LoadFromJson("""
        {
          "symbols": [
            { "id":1, "exchange":"SHFE", "code":"rb", "name":"螺纹钢", "category":"black",
              "deliveryType":"physical", "tradingUnit":10, "unitName":"吨", "tickSize":1,
              "tickValue":10, "priceLimitPct":0.10, "marginRate":0.08, "months":"1-12" },
            { "id":2, "exchange":"CZCE", "code":"ta", "name":"PTA", "category":"chemical",
              "deliveryType":"physical", "tradingUnit":5, "unitName":"吨", "tickSize":2,
              "tickValue":10, "priceLimitPct":0.06, "marginRate":0.08, "months":"1-12" }
          ]
        }
        """);

    // Resolve strips trailing digits → "rb000" → "rb"
    private static OrderEvent Fill(string inst, OrderDirection dir, int qty, decimal price, string sid = "s1")
        => new() { InstrumentId = inst, Direction = dir, Quantity = qty,
            Type = OrderEventType.Filled, FillPrice = price, Fee = 50,
            StrategyId = sid, Time = DateTimeOffset.Now };

    private static OrderEvent Buy(string inst, int qty, decimal price, string sid = "s1")
        => Fill(inst, OrderDirection.Buy, qty, price, sid);
    private static OrderEvent Sell(string inst, int qty, decimal price, string sid = "s1")
        => Fill(inst, OrderDirection.Sell, qty, price, sid);

    // ═══ 开仓 → 平仓 ═══

    [Fact]
    public void OpenLong_EquityNearlyUnchanged()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);

        Assert.Equal(5, pm.GetPosition("s1", "rb")!.Quantity);
        Assert.Equal(1_000_000m - 50m, pm.Equity);
    }

    [Fact]
    public void CloseLong_Profit_EquityIncreases()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);
        pm.ProcessFill(Sell("rb", 5, 3600), Reg);

        Assert.Null(pm.GetPosition("s1", "rb"));
        Assert.True(pm.Equity > 1_000_000m);
        Assert.Equal(0m, pm.MarginUsed);
    }

    [Fact]
    public void CloseLong_Loss_EquityDecreases()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);
        pm.ProcessFill(Sell("rb", 5, 3400), Reg);

        Assert.Null(pm.GetPosition("s1", "rb"));
        Assert.True(pm.Equity < 1_000_000m);
    }

    // ═══ 做空 ═══

    [Fact]
    public void OpenShort_NegativeQuantity()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Sell("rb", 5, 3500), Reg);

        var pos = pm.GetPosition("s1", "rb")!;
        Assert.Equal(-5, pos.Quantity);
        Assert.Equal(3500m, pos.AvgPrice);
    }

    [Fact]
    public void CloseShort_Profit_PriceGoesDown()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Sell("rb", 5, 3500), Reg);
        pm.ProcessFill(Buy("rb", 5, 3400), Reg);

        Assert.Null(pm.GetPosition("s1", "rb"));
        Assert.True(pm.Equity > 1_000_000m);
    }

    [Fact]
    public void CloseShort_Loss_PriceGoesUp()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Sell("rb", 5, 3500), Reg);
        pm.ProcessFill(Buy("rb", 5, 3600), Reg);

        Assert.Null(pm.GetPosition("s1", "rb"));
        Assert.True(pm.Equity < 1_000_000m);
    }

    // ═══ 反向开仓 ═══

    [Fact]
    public void Reverse_LongToShort()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);
        pm.ProcessFill(Sell("rb", 10, 3600), Reg); // close 5 + open 5 short

        var pos = pm.GetPosition("s1", "rb")!;
        Assert.Equal(-5, pos.Quantity);
        Assert.Equal(3600m, pos.AvgPrice);
    }

    // ═══ 加仓 ═══

    [Fact]
    public void AddToPosition_AvgPriceWeighted()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);
        pm.ProcessFill(Buy("rb", 5, 3600), Reg);

        var pos = pm.GetPosition("s1", "rb")!;
        Assert.Equal(10, pos.Quantity);
        Assert.Equal(3550m, pos.AvgPrice);
    }

    // ═══ 多品种 ═══

    [Fact]
    public void MultiInstrument_IndependentPositions()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 3, 3500), Reg);
        pm.ProcessFill(Buy("ta", 5, 5000), Reg);

        Assert.Equal(3, pm.GetPosition("s1", "rb")!.Quantity);
        Assert.Equal(5, pm.GetPosition("s1", "ta")!.Quantity);
        Assert.True(pm.MarginUsed > 0);
    }

    // ═══ 保证金回归测试 ═══

    [Fact]
    public void Equity_Equals_Cash_Plus_Margin_WhenNoUnrealized()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);

        Assert.Equal(pm.Cash + pm.MarginUsed, pm.Equity, 1);
    }

    [Fact]
    public void OpenDeductsMarginFromCash()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        var cash0 = pm.Cash;

        pm.ProcessFill(Buy("rb", 5, 3500), Reg);

        var expectedMargin = 3500m * 10 * 5 * 0.08m;
        Assert.Equal(cash0 - 50m - expectedMargin, pm.Cash);
        Assert.Equal(expectedMargin, pm.MarginUsed);
    }

    [Fact]
    public void CloseReturnsMarginToCash()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);
        Assert.True(pm.MarginUsed > 0);

        pm.ProcessFill(Sell("rb", 5, 3600), Reg);

        Assert.Equal(0m, pm.MarginUsed);
        Assert.True(pm.Equity > 1_000_000m - 100m);
    }

    // ═══ 分账 ═══

    [Fact]
    public void SubPortfolio_Isolation()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 500_000);
        pm.CreateSubPortfolio("s2", 500_000);

        pm.ProcessFill(Buy("rb", 5, 3500, "s1"), Reg);
        pm.ProcessFill(Sell("rb", 5, 3600, "s1"), Reg);

        Assert.True(pm.GetSubPortfolio("s1").Equity > 500_000m);
        Assert.Equal(500_000m, pm.GetSubPortfolio("s2").Equity);  // s2 无持仓 → Equity == 未动用的分账资本
    }

    // ═══ 未实现盈亏 ═══

    [Fact]
    public void UnrealizedPnl_UpdatesEquity()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);

        var bar = new Bar { InstrumentId = "rb", Close = (long)(3550 * TickRecord.PriceScale) };
        pm.UpdateMarketPrice(bar, Reg.Find("rb")!);

        Assert.True(pm.GetPosition("s1", "rb")!.UnrealizedPnl > 0);
        Assert.True(pm.Equity > pm.Cash + pm.MarginUsed);
    }

    // ═══ 权益恢复 (进程重启) ═══

    [Fact]
    public void ReconcileEquity_PreservesTotalPnL_AcrossRestart()
    {
        var pm = new PortfolioManager(1_000_000);
        // 模拟重启：CTP 返回动态权益 993,800（累计亏损 6,200），昨结算 1,000,000，无持仓
        pm.ReconcileEquity(new BrokerAccountSnapshot { Balance = 993_800m, PositionProfit = 0m, PreBalance = 1_000_000m });

        Assert.Equal(993_800m, pm.Equity);
        Assert.Equal(993_800m, pm.Cash);        // 无持仓无保证金 → 现金 = 权益
        Assert.Equal(-6_200m, pm.TotalPnL);     // 累计盈亏跨重启保留，不再跳回 0
        Assert.Equal(-6_200m, pm.TodayPnL);     // Balance - PreBalance = 当日真实盈亏
    }

    [Fact]
    public void ReconcileEquity_PreservesMarginDecomposition()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.RestorePosition("rb", "s1", 0, 2, 15_000m, 45_000m, DateTime.Today, '2');
        Assert.Equal(45_000m, pm.MarginUsed);

        // CTP 返回动态权益 1,000,000（含 45,000 占用保证金，无浮盈），重构后现金 = 权益 - 保证金
        pm.ReconcileEquity(new BrokerAccountSnapshot { Balance = 1_000_000m, PositionProfit = 0m, PreBalance = 1_000_000m, CurrMargin = 45_000m });

        Assert.Equal(1_000_000m, pm.Equity);
        Assert.Equal(1_000_000m - 45_000m, pm.Cash);
    }

    [Fact]
    public void ReconcileEquity_WithFloatingProfit_DoesNotDoubleCount()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.RestorePosition("rb", "s1", 0, 2, 15_000m, 45_000m, DateTime.Today, '2');

        // CTP 报告：动态权益 1,010,000（含 10,000 浮盈），持仓盈亏 10,000，昨结算 1,000,000，占用保证金 45,000
        pm.ReconcileEquity(new BrokerAccountSnapshot { Balance = 1_010_000m, PositionProfit = 10_000m, PreBalance = 1_000_000m, CurrMargin = 45_000m });

        // 现金基 = Balance - PositionProfit - Margin = 955,000（浮盈扣除，避免双重计算）
        Assert.Equal(955_000m, pm.Cash);
        Assert.Equal(1_010_000m, pm.Equity);

        // Bar 驱动浮盈补回：收盘 15,500 → 浮盈 = (15500-15000)*2*10 = 10,000
        var bar = new Bar { InstrumentId = "rb", Close = (long)(15_500 * TickRecord.PriceScale) };
        pm.UpdateMarketPrice(bar, Reg.Find("rb")!);

        Assert.Equal(10_000d, pm.GetPosition("s1", "rb")!.UnrealizedPnl, 1);
        Assert.Equal(1_010_000m, pm.Equity);    // 收敛到 Balance，浮盈未双重计入
    }

    // ═══ 持仓对账（RestorePosition 本地账本 vs CTP 权威） ═══

    [Fact]
    public void RestorePosition_MismatchedQuantity_SetsReconcileMismatch()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.RestorePosition("rb", "s1", 0, 2, 15_000m, 45_000m, DateTime.Today, '2');

        pm.BeginReconcile();
        // CTP 报告 3 手，本地账本 2 手 → 数量不一致 → Mismatch
        pm.RestorePosition("rb", "s1", 0, 3, 15_000m, 45_000m, DateTime.Today, '2');

        Assert.Equal(ReconcileStatus.Mismatch, pm.ReconcileStatus);
    }

    [Fact]
    public void RestorePosition_PriceDeviation_SetsReconcileMismatch()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.RestorePosition("rb", "s1", 0, 2, 15_000m, 45_000m, DateTime.Today, '2');

        pm.BeginReconcile();
        // 均价 15150 vs 15000，偏离 1% > 0.5% 阈值 → Mismatch
        pm.RestorePosition("rb", "s1", 0, 2, 15_150m, 45_000m, DateTime.Today, '2');

        Assert.Equal(ReconcileStatus.Mismatch, pm.ReconcileStatus);
    }

    [Fact]
    public void RestorePosition_Matching_EndReconcile_Ok()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.RestorePosition("rb", "s1", 0, 2, 15_000m, 45_000m, DateTime.Today, '2');

        pm.BeginReconcile();
        pm.RestorePosition("rb", "s1", 0, 2, 15_000m, 45_000m, DateTime.Today, '2'); // 一致
        pm.EndReconcile();

        Assert.Equal(ReconcileStatus.Ok, pm.ReconcileStatus);
    }

    // ═══ P2 · 复合键 + 今昨拆分 + 不可变快照（黄金手算用例） ═══

    [Fact]
    public void TwoStrategies_SameInstrument_IsolatedPositions()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 500_000);
        pm.CreateSubPortfolio("s2", 500_000);

        pm.ProcessFill(Buy("rb", 5, 3500, "s1"), Reg);
        pm.ProcessFill(Buy("rb", 3, 3600, "s2"), Reg);

        // 复合键 (策略, 合约)：两策略同合约是两个独立仓位，不是撞成一个
        Assert.Equal(5, pm.GetPosition("s1", "rb")!.Quantity);
        Assert.Equal(3, pm.GetPosition("s2", "rb")!.Quantity);
        Assert.Equal(2, pm.AllPositions.Count);

        // 平掉 s2，s1 仓位不受影响
        pm.ProcessFill(Sell("rb", 3, 3650, "s2"), Reg);
        Assert.Null(pm.GetPosition("s2", "rb"));
        Assert.Equal(5, pm.GetPosition("s1", "rb")!.Quantity);
    }

    [Fact]
    public void SettleDaily_RollsTodayIntoYesterday()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);

        var before = pm.GetPosition("s1", "rb")!;
        Assert.Equal(5, before.QuantityToday);
        Assert.Equal(0, before.QuantityYesterday);

        pm.SettleDaily(Reg);

        var after = pm.GetPosition("s1", "rb")!;
        Assert.Equal(0, after.QuantityToday);   // 今仓滚入昨仓
        Assert.Equal(5, after.QuantityYesterday);
        Assert.Equal(5, after.Quantity);        // 净手数不变
    }

    [Fact]
    public void GetPosition_ReturnsImmutableSnapshot()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);

        var snap = pm.GetPosition("s1", "rb")!;
        Assert.IsType<PositionSnapshot>(snap);
        Assert.Equal(5, snap.Quantity);

        // 后续加仓，之前拿到的快照仍反映旧数量（证明是拷贝而非内部引用）
        pm.ProcessFill(Buy("rb", 5, 3600), Reg);
        Assert.Equal(5, snap.Quantity);
        Assert.Equal(10, pm.GetPosition("s1", "rb")!.Quantity);
    }

    [Fact]
    public void Reduce_CloseYesterday_ReducesYesterdayFirst()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        // 直接恢复混合仓：今 2 + 昨 3（共 5 手 @3500，保证金 3500×10×5×0.08 = 14,000）
        pm.RestorePosition("rb", "s1", 2, 3, 3500m, 14_000m, DateTime.Today, '1');

        // 平昨 1 手（OffsetFlag=CloseYesterday）→ 昨 3 → 2，今 2 不动
        pm.ProcessFill(new OrderEvent
        {
            InstrumentId = "rb", Direction = OrderDirection.Sell, Quantity = 1,
            Type = OrderEventType.Filled, FillPrice = 3600, Fee = 50,
            StrategyId = "s1", Time = DateTimeOffset.Now, OffsetFlag = "CloseYesterday",
        }, Reg);

        var pos = pm.GetPosition("s1", "rb")!;
        Assert.Equal(2, pos.QuantityToday);
        Assert.Equal(2, pos.QuantityYesterday);
        Assert.Equal(4, pos.Quantity);
    }

    [Fact]
    public void Reduce_CloseToday_ReducesTodayFirst()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.RestorePosition("rb", "s1", 2, 3, 3500m, 14_000m, DateTime.Today, '1');

        // 平今 2 手（OffsetFlag=CloseToday）→ 今 2 → 0，昨 3 不动
        pm.ProcessFill(new OrderEvent
        {
            InstrumentId = "rb", Direction = OrderDirection.Sell, Quantity = 2,
            Type = OrderEventType.Filled, FillPrice = 3550, Fee = 50,
            StrategyId = "s1", Time = DateTimeOffset.Now, OffsetFlag = "CloseToday",
        }, Reg);

        var pos = pm.GetPosition("s1", "rb")!;
        Assert.Equal(0, pos.QuantityToday);
        Assert.Equal(3, pos.QuantityYesterday);
        Assert.Equal(3, pos.Quantity);
    }
}
