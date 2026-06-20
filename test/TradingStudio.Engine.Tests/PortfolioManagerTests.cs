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

        Assert.Equal(5, pm.GetPosition("rb")!.Quantity);
        Assert.Equal(1_000_000m - 50m, pm.Equity);
    }

    [Fact]
    public void CloseLong_Profit_EquityIncreases()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Buy("rb", 5, 3500), Reg);
        pm.ProcessFill(Sell("rb", 5, 3600), Reg);

        Assert.Null(pm.GetPosition("rb"));
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

        Assert.Null(pm.GetPosition("rb"));
        Assert.True(pm.Equity < 1_000_000m);
    }

    // ═══ 做空 ═══

    [Fact]
    public void OpenShort_NegativeQuantity()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Sell("rb", 5, 3500), Reg);

        var pos = pm.GetPosition("rb")!;
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

        Assert.Null(pm.GetPosition("rb"));
        Assert.True(pm.Equity > 1_000_000m);
    }

    [Fact]
    public void CloseShort_Loss_PriceGoesUp()
    {
        var pm = new PortfolioManager(1_000_000);
        pm.CreateSubPortfolio("s1", 1_000_000);
        pm.ProcessFill(Sell("rb", 5, 3500), Reg);
        pm.ProcessFill(Buy("rb", 5, 3600), Reg);

        Assert.Null(pm.GetPosition("rb"));
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

        var pos = pm.GetPosition("rb")!;
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

        var pos = pm.GetPosition("rb")!;
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

        Assert.Equal(3, pm.GetPosition("rb")!.Quantity);
        Assert.Equal(5, pm.GetPosition("ta")!.Quantity);
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
        Assert.Equal(500_000m, pm.GetSubPortfolio("s2").Cash);
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

        Assert.True(pm.GetPosition("rb")!.UnrealizedPnl > 0);
        Assert.True(pm.Equity > pm.Cash + pm.MarginUsed);
    }
}
