using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// 涨跌停锁定撮合回归测试。
///
/// 修复前的 Bug：
/// - Bar 路径用 `prevClose = bar.Open` 近似前结算价，涨停基准 = Open×1.10，
///   而封板 Bar 的 O=H=L=C 全等，`High >= Open×1.10` 恒假 → 封涨停照样成交。
/// - Tick 路径完全没有涨跌停检查。
///
/// 修复后：
/// - Bar 路径用"上一交易日收盘价"作基准（近似前结算价），封板日正确拦截。
/// - Tick 路径用 CTP 采集时标记的 IsUpperLimit/IsLowerLimit 精确拦截。
/// 拦截是方向性的：涨停只挡买、跌停只挡卖；反向仍可成交。
/// </summary>
public class LimitLockTests
{
    private const long S = TickRecord.PriceScale;
    private static long P(decimal p) => (long)(p * S);
    private const long Ts = 1_700_000_000_000;

    // PriceLimitPct = 0.10 → 涨跌停 = 基准价 ±10%
    private static Future Rb => new()
    {
        Code = "rb", TradingUnit = 10, TickSize = 1, TickValue = 10,
        MarginRate = 0.08m, PriceLimitPct = 0.10m, FeeRate = 0.0001
    };

    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly Day2 = new(2026, 1, 6);

    private static Bar DayBar(DateOnly day, decimal o, decimal h, decimal l, decimal c, long v = 10000)
        => new()
        {
            InstrumentId = "rb", TradingDay = day, BarTime = day.ToDateTime(TimeOnly.MinValue),
            Open = P(o), High = P(h), Low = P(l), Close = P(c), Volume = v
        };

    private static Order Mkt(OrderDirection dir, int qty = 1)
        => new() { InstrumentId = "rb", Direction = dir, Type = OrderType.Market, Quantity = qty, StrategyId = "s" };

    private static TickRecord LimitTick(decimal price, long vol, int flags)
        => new()
        {
            LastPrice = P(price), BidPrice1 = P(price), AskPrice1 = P(price),
            Volume = vol, Flags = flags, ExchangeTimestamp = Ts
        };

    // ═══ Bar 路径：用上一交易日收盘价作涨跌停基准 ═══

    [Fact]
    public void Bar_SealedLimitUp_NextDay_BuyBlocked()
    {
        var h = new ExecutionHandler(new RiskController());
        // Day1 正常收盘 3500 → 成为 Day2 涨跌停基准 (上限 3850)
        h.ProcessBar(DayBar(Day1, 3500, 3520, 3480, 3500), Rb);
        h.Submit(Mkt(OrderDirection.Buy), "s");
        // Day2 跳空封涨停并锁死：O=H=L=C=3850
        var fills = h.ProcessBar(DayBar(Day2, 3850, 3850, 3850, 3850), Rb);
        Assert.Empty(fills); // 涨停锁死，买不进（修复前会成交）
    }

    [Fact]
    public void Bar_NextDay_NotAtLimit_BuyFills()
    {
        var h = new ExecutionHandler(new RiskController());
        h.ProcessBar(DayBar(Day1, 3500, 3520, 3480, 3500), Rb);
        h.Submit(Mkt(OrderDirection.Buy), "s");
        // Day2 正常波动，High 3560 远低于涨停 3850 → 正常成交
        var fills = h.ProcessBar(DayBar(Day2, 3520, 3560, 3500, 3540), Rb);
        Assert.Single(fills);
        Assert.Equal(3521m, fills[0].FillPrice); // Open 3520 + 1 tick
    }

    [Fact]
    public void Bar_SealedLimitDown_NextDay_StopSellBlocked()
    {
        var h = new ExecutionHandler(new RiskController());
        h.ProcessBar(DayBar(Day1, 3500, 3520, 3480, 3500), Rb);
        // 止损卖单：正常情况下 Low 触破 3400 会触发
        h.Submit(new Order { InstrumentId = "rb", Direction = OrderDirection.Sell,
            Type = OrderType.Stop, Quantity = 1, StopPrice = 3400, StrategyId = "s" }, "s");
        // Day2 封跌停锁死：O=H=L=C=3150 (=3500×0.90)
        var fills = h.ProcessBar(DayBar(Day2, 3150, 3150, 3150, 3150), Rb);
        Assert.Empty(fills); // 跌停锁死，止损打不出（真实且致命的场景）
    }

    [Fact]
    public void Bar_FirstDay_NoReference_FallsBackToOpenHeuristic()
    {
        // 首日无上一交易日基准 → 退回"用本 Bar 开盘价"的旧启发式。
        // 本 Bar 从 3500 冲到 3850 (Open×1.10) → atUpperLimit → 买不进。
        var h = new ExecutionHandler(new RiskController());
        h.Submit(Mkt(OrderDirection.Buy), "s");
        var fills = h.ProcessBar(DayBar(Day1, 3500, 3850, 3500, 3850), Rb);
        Assert.Empty(fills);
    }

    // ═══ Tick 路径：用 CTP 标记的涨跌停 Flag 精确拦截 ═══

    [Fact]
    public void Tick_UpperLimit_BuyBlocked()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(Mkt(OrderDirection.Buy), "s");
        var fills = h.ProcessTick(LimitTick(3850, vol: 100, TickRecord.FlagUpperLimit), "rb", Rb);
        Assert.Empty(fills); // 涨停买不进（修复前 Tick 路径无检查会成交）
    }

    [Fact]
    public void Tick_UpperLimit_SellStillFills()
    {
        // 涨停时仍有大量买盘 → 卖单可成交（拦截必须是方向性的）
        var h = new ExecutionHandler(new RiskController());
        h.Submit(Mkt(OrderDirection.Sell), "s");
        var fills = h.ProcessTick(LimitTick(3850, vol: 100, TickRecord.FlagUpperLimit), "rb", Rb);
        Assert.Single(fills);
    }

    [Fact]
    public void Tick_LowerLimit_SellBlocked()
    {
        var h = new ExecutionHandler(new RiskController());
        h.Submit(Mkt(OrderDirection.Sell), "s");
        var fills = h.ProcessTick(LimitTick(3150, vol: 100, TickRecord.FlagLowerLimit), "rb", Rb);
        Assert.Empty(fills); // 跌停卖不出
    }

    [Fact]
    public void Tick_NoLimitFlag_BuyFills()
    {
        // 无涨跌停标记 → 正常成交（对照，证明拦截不是"一刀切"）
        var h = new ExecutionHandler(new RiskController());
        h.Submit(Mkt(OrderDirection.Buy), "s");
        var fills = h.ProcessTick(LimitTick(3850, vol: 100, flags: 0), "rb", Rb);
        Assert.Single(fills);
    }
}
