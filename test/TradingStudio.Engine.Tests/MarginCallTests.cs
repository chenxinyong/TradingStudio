using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// 保证金强平（爆仓）回归测试。
///
/// 修复前：PortfolioManager 没有任何购买力/爆仓约束——现金可为负、权益穿仓也不清算，
/// 高杠杆策略的回撤/风险被系统性低估。
///
/// 修复后：CheckMarginCall 在权益跌破占用保证金（风险度≥100%）时，生成"全部持仓
/// 按当前价平仓"的强平单，由引擎 ProcessFill，使强平亏损真实进入权益与 Trade 记录。
/// </summary>
public class MarginCallTests
{
    private static readonly FutureRegistry Reg = FutureRegistry.LoadFromJson("""
        {"symbols":[{"id":1,"exchange":"SHFE","code":"rb","name":"螺纹钢","category":"black",
        "deliveryType":"physical","tradingUnit":10,"unitName":"吨","tickSize":1,"tickValue":10,
        "priceLimitPct":0.10,"marginRate":0.08,"months":"1-12"}]}
        """);

    private static Future Rb => Reg.Resolve("rb")!;
    private const long S = TickRecord.PriceScale;
    private static long P(decimal p) => (long)(p * S);

    private static Bar RbBar(decimal close) => new()
    {
        InstrumentId = "rb", TradingDay = new(2026, 1, 5), BarTime = new DateTime(2026, 1, 5, 9, 0, 0),
        Open = P(close), High = P(close), Low = P(close), Close = P(close), Volume = 1000
    };

    // 30 手 rb @3500：保证金 = 3500×10×30×0.08 = 84,000；开仓后现金 ≈ 100k−84k−50 = 15,950
    private static OrderEvent BuyOpen(int qty, decimal price) => new()
    {
        InstrumentId = "rb", Direction = OrderDirection.Buy, Quantity = qty,
        Type = OrderEventType.Filled, FillPrice = price, Fee = 50,
        StrategyId = "s1", Time = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero)
    };

    private static PortfolioManager LeveragedLong()
    {
        var pm = new PortfolioManager(100_000);
        pm.CreateSubPortfolio("s1", 100_000);
        pm.ProcessFill(BuyOpen(30, 3500), Reg); // 占用保证金 84,000
        return pm;
    }

    [Fact]
    public void MarginCall_EquityBelowMargin_LiquidatesAll()
    {
        var pm = LeveragedLong();
        // 盯市到 3440：浮亏 (3440−3500)×30×10 = −18,000 → 权益 ≈ 81,950 < 占用保证金 84,000
        pm.UpdateMarketPrice(RbBar(3440), Rb);

        var fills = pm.CheckMarginCall(RbBar(3440));

        Assert.Single(fills);
        Assert.Equal(-2, fills[0].OrderId);               // 系统强平标记
        Assert.Equal(OrderDirection.Sell, fills[0].Direction); // 平多
        Assert.Equal(30, fills[0].Quantity);
        Assert.Contains("Margin call", fills[0].Message);
    }

    [Fact]
    public void MarginCall_LiquidationClosesPositionAndRealizesLoss()
    {
        var pm = LeveragedLong();
        pm.UpdateMarketPrice(RbBar(3440), Rb);

        // 引擎会 ProcessFill 强平单
        foreach (var fill in pm.CheckMarginCall(RbBar(3440)))
            pm.ProcessFill(fill, Reg);

        Assert.Null(pm.GetPosition("s1", "rb"));            // 持仓被强平清空
        Assert.Equal(0m, pm.MarginUsed);              // 保证金释放
        Assert.Single(pm.TradeHistory);
        Assert.True(pm.TradeHistory[0].PnL < 0);      // 强平亏损进入 Trade
        Assert.True(pm.Equity < 100_000m);            // 权益反映真实亏损
    }

    [Fact]
    public void MarginCall_EquityAboveMargin_NoLiquidation()
    {
        var pm = LeveragedLong();
        // 盯市到 3490：浮亏仅 −3,000 → 权益 ≈ 96,950 > 占用保证金 84,000 → 不强平
        pm.UpdateMarketPrice(RbBar(3490), Rb);
        Assert.Empty(pm.CheckMarginCall(RbBar(3490)));
    }

    [Fact]
    public void MarginCall_NoPositions_NoLiquidation()
    {
        var pm = new PortfolioManager(100_000);
        Assert.Empty(pm.CheckMarginCall(RbBar(3000)));
    }
}
