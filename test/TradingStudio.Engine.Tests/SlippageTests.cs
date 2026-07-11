using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// Bar 模式滑点按波动（ATR）缩放的回归测试。
///
/// 修复前：市价单滑点恒为 1 跳，违背项目"实盘用 2-3 倍回测滑点"的忠告——
/// 高波动/跳空品种的成本被系统性低估。
///
/// 修复后：滑点 = max(1 跳, SlippageAtrFactor × ATR)，ATR 由此前 Bar 算出（无未来函数）。
/// ATR 未就绪或因子=0 时退回固定 1 跳，保持既有单 Bar 行为不变。
/// </summary>
public class SlippageTests
{
    private const long S = TickRecord.PriceScale;
    private static long P(decimal p) => (long)(p * S);
    private static readonly DateOnly D = new(2026, 1, 5);

    private static Future Rb => new()
    {
        Code = "rb", TradingUnit = 10, TickSize = 1, TickValue = 10,
        MarginRate = 0.08m, PriceLimitPct = 0.10m, FeeRate = 0.0001
    };

    // Open 固定 3500，H-L=range，Close=Open（无跳空）→ 每根 TR=range
    private static Bar Bar(decimal range, long vol = 1000) => new()
    {
        InstrumentId = "rb", TradingDay = D, BarTime = new DateTime(2026, 1, 5, 9, 0, 0),
        Open = P(3500), High = P(3500 + range / 2m), Low = P(3500 - range / 2m),
        Close = P(3500), Volume = vol
    };

    private static Order Mkt(OrderDirection dir) => new()
    {
        InstrumentId = "rb", Direction = dir, Type = OrderType.Market, Quantity = 1, StrategyId = "s"
    };

    // 预热 ATR：喂 count 根 TR=range 的 Bar（无订单，只累积 ATR）
    private static void WarmupAtr(ExecutionHandler h, decimal range, int count = 15)
    {
        for (int i = 0; i < count; i++) h.ProcessBar(Bar(range), Rb);
    }

    [Fact]
    public void FirstBar_NoAtr_SlippageIsOneTick()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(Mkt(OrderDirection.Buy), "s");
        var fills = h.ProcessBar(Bar(20), Rb);
        Assert.Single(fills);
        Assert.Equal(3501m, fills[0].FillPrice); // ATR 未就绪 → Open + 1 跳
    }

    [Fact]
    public void HighVolatility_SlippageScalesWithAtr()
    {
        var h = new ExecutionHandler(new RiskController());
        WarmupAtr(h, range: 20);                       // ATR = 20
        h.Submit(Mkt(OrderDirection.Buy), "s");
        var fills = h.ProcessBar(Bar(20), Rb);
        Assert.Single(fills);
        Assert.Equal(3510m, fills[0].FillPrice);       // Open + max(1, 0.5×20) = Open + 10
    }

    [Fact]
    public void HighVolatility_SellSide_Symmetric()
    {
        var h = new ExecutionHandler(new RiskController());
        WarmupAtr(h, range: 20);
        h.Submit(Mkt(OrderDirection.Sell), "s");
        var fills = h.ProcessBar(Bar(20), Rb);
        Assert.Single(fills);
        Assert.Equal(3490m, fills[0].FillPrice);       // Open − 10
    }

    [Fact]
    public void LowVolatility_OneTickFloorHolds()
    {
        var h = new ExecutionHandler(new RiskController());
        WarmupAtr(h, range: 1);                         // ATR = 1 → 0.5×1 < 1 → 地板 1 跳
        h.Submit(Mkt(OrderDirection.Buy), "s");
        var fills = h.ProcessBar(Bar(1), Rb);
        Assert.Single(fills);
        Assert.Equal(3501m, fills[0].FillPrice);
    }

    [Fact]
    public void SlippageFactorZero_DisablesScaling()
    {
        var h = new ExecutionHandler(new RiskController()) { SlippageAtrFactor = 0m };
        WarmupAtr(h, range: 20);
        h.Submit(Mkt(OrderDirection.Buy), "s");
        var fills = h.ProcessBar(Bar(20), Rb);
        Assert.Single(fills);
        Assert.Equal(3501m, fills[0].FillPrice);       // 缩放关闭 → 固定 1 跳
    }
}
