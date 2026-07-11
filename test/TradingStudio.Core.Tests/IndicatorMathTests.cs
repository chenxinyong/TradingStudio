using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;

namespace TradingStudio.Core.Tests;

/// <summary>
/// 技术指标数学正确性测试。策略的每一次进出场都建立在这些数值上——
/// 预热 off-by-one、平滑系数错、TR 漏算跳空，都会静默扭曲信号且回测/实盘一起错。
/// 这里用手工可验证的小序列钉死每个指标的数学。
///
/// 注：AtrIndicator 注释写"Wilder 平滑"，但实现是"TR 的简单移动平均"（滚动窗口和÷周期）。
/// 本测试按实际实现（SMA of TR）断言；若要改成真 Wilder，这些期望值需同步更新。
/// </summary>
public class IndicatorMathTests
{
    private const long S = TickRecord.PriceScale;
    private static long P(double p) => (long)(p * S);
    private static Bar C(double close) => new() { Open = P(close), High = P(close), Low = P(close), Close = P(close) };
    private static Bar HLC(double h, double l, double c) => new() { Open = P(c), High = P(h), Low = P(l), Close = P(c) };

    private static T Feed<T>(T ind, params double[] closes) where T : IIndicator
    {
        foreach (var c in closes) ind.Update(C(c));
        return ind;
    }

    // ═══════════════ SMA ═══════════════

    [Fact]
    public void Sma_ArithmeticMean_OverWindow()
    {
        var sma = Feed(new SmaIndicator(3), 10, 20, 30);
        Assert.True(sma.IsReady);
        Assert.Equal(20.0, sma.CurrentValue, 9);        // (10+20+30)/3
        sma.Update(C(40));
        Assert.Equal(30.0, sma.CurrentValue, 9);        // (20+30+40)/3，窗口滚动
    }

    [Fact]
    public void Sma_NotReady_BeforeWindowFilled()
    {
        var sma = Feed(new SmaIndicator(3), 10, 20);
        Assert.False(sma.IsReady);
        Assert.True(double.IsNaN(sma.CurrentValue));
    }

    // ═══════════════ EMA ═══════════════

    [Fact]
    public void Ema_SeedsWithFirstPrice_ThenSmooths()
    {
        // period=3 → α=0.5：10 → 15 → 22.5
        var ema = Feed(new EmaIndicator(3), 10, 20, 30);
        Assert.True(ema.IsReady);
        Assert.Equal(22.5, ema.CurrentValue, 9);
    }

    [Fact]
    public void Ema_NotReady_UntilPeriodBars()
    {
        var ema = Feed(new EmaIndicator(3), 10, 20);
        Assert.False(ema.IsReady);
        Assert.True(double.IsNaN(ema.CurrentValue));
    }

    [Fact]
    public void Ema_ConstantInput_ConvergesToConstant()
    {
        var ema = Feed(new EmaIndicator(5), 7, 7, 7, 7, 7, 7, 7);
        Assert.Equal(7.0, ema.CurrentValue, 9);
    }

    // ═══════════════ ATR / TrueRange ═══════════════

    [Fact]
    public void TrueRange_FirstBar_IsHighMinusLow()
    {
        Assert.Equal(2.0, AtrIndicator.TrueRange(HLC(12, 10, 11), double.NaN), 9);
    }

    [Fact]
    public void TrueRange_CapturesGap_BeyondHighLow()
    {
        // 跳空高开：H-L=2，但相对前收 15 的跳空使 TR=5（|20-15|）——TR 必须抓住跳空
        Assert.Equal(5.0, AtrIndicator.TrueRange(HLC(20, 18, 19), prevClose: 15), 9);
        // 跳空低开：|L-prevClose| 主导
        Assert.Equal(6.0, AtrIndicator.TrueRange(HLC(12, 10, 11), prevClose: 16), 9);
    }

    [Fact]
    public void Atr_IsSimpleMovingAverageOfTrueRange()
    {
        // TR: bar1=2(H-L), bar2=max(4,|15-11|,0)=4, bar3=max(3,|16-14|,|13-14|)=3 → ATR=(2+4+3)/3=3
        var atr = new AtrIndicator(3);
        atr.Update(HLC(12, 10, 11));
        atr.Update(HLC(15, 11, 14));
        atr.Update(HLC(16, 13, 13));
        Assert.True(atr.IsReady);
        Assert.Equal(3.0, atr.CurrentValue, 9);
    }

    [Fact]
    public void Atr_NotReady_BeforePeriod()
    {
        var atr = new AtrIndicator(3);
        atr.Update(HLC(12, 10, 11));
        atr.Update(HLC(15, 11, 14));
        Assert.False(atr.IsReady);
    }

    // ═══════════════ RSI (Wilder) ═══════════════

    [Fact]
    public void Rsi_AllGains_Is100()
    {
        var rsi = Feed(new RsiIndicator(3), 10, 11, 12, 13, 14);
        Assert.True(rsi.IsReady);
        Assert.Equal(100.0, rsi.CurrentValue, 9);
    }

    [Fact]
    public void Rsi_AllLosses_Is0()
    {
        var rsi = Feed(new RsiIndicator(3), 14, 13, 12, 11, 10);
        Assert.True(rsi.IsReady);
        Assert.Equal(0.0, rsi.CurrentValue, 9);
    }

    [Fact]
    public void Rsi_MixedSeries_MatchesWilderHandCalc()
    {
        // period=2, closes 10,12,11,13:
        // b2 +2: avgG=2,avgL=0; b3 -1: avgG=1,avgL=0.5; b4 +2(Wilder): avgG=1.5,avgL=0.25
        // RSI = 100 - 100/(1 + 1.5/0.25) = 100 - 100/7 ≈ 85.7143
        var rsi = Feed(new RsiIndicator(2), 10, 12, 11, 13);
        Assert.True(rsi.IsReady);
        Assert.Equal(85.7143, rsi.CurrentValue, 4);
    }

    [Fact]
    public void Rsi_StaysWithin0To100()
    {
        var rsi = Feed(new RsiIndicator(4), 10, 12, 11, 15, 9, 13, 8, 14, 10);
        Assert.True(rsi.IsReady);
        Assert.InRange(rsi.CurrentValue, 0.0, 100.0);
    }

    // ═══════════════ Bollinger ═══════════════

    [Fact]
    public void Bollinger_MiddleIsSma_BandsAreKSigma()
    {
        // period=4, k=2, closes 1,2,3,4: mean=2.5, σ=sqrt(5/4)=1.118034
        var boll = new BollingerIndicator(4, 2.0);
        foreach (var c in new[] { 1.0, 2, 3, 4 }) boll.Update(C(c));
        Assert.True(boll.IsReady);
        Assert.Equal(2.5, boll.Middle[^1], 9);
        Assert.Equal(4.736068, boll.Upper[^1], 5);
        Assert.Equal(0.263932, boll.Lower[^1], 5);
        // 带宽 = 2·k·σ，上下轨关于中轨对称
        Assert.Equal(boll.Upper[^1] - boll.Middle[^1], boll.Middle[^1] - boll.Lower[^1], 9);
    }

    [Fact]
    public void Bollinger_ConstantInput_ZeroWidth()
    {
        var boll = new BollingerIndicator(3, 2.0);
        foreach (var _ in new[] { 0, 1, 2 }) boll.Update(C(5));
        Assert.Equal(5.0, boll.Upper[^1], 9);
        Assert.Equal(5.0, boll.Middle[^1], 9);
        Assert.Equal(5.0, boll.Lower[^1], 9);
    }

    [Fact]
    public void Bollinger_MiddleMatchesSma()
    {
        var boll = new BollingerIndicator(3, 2.0);
        var sma = new SmaIndicator(3);
        foreach (var c in new[] { 3.0, 7, 5, 9, 11 }) { boll.Update(C(c)); sma.Update(C(c)); }
        Assert.Equal(sma.CurrentValue, boll.Middle[^1], 9);
    }

    // ═══════════════ MACD ═══════════════

    [Fact]
    public void Macd_DifEqualsFastMinusSlowEma()
    {
        // 用独立 EMA 交叉验证 DIF = EMA(fast) - EMA(slow)
        var macd = new MacdIndicator(3, 5, 3);
        var fast = new EmaIndicator(3);
        var slow = new EmaIndicator(5);
        for (int i = 1; i <= 20; i++)
        {
            var bar = C(i);
            macd.Update(bar); fast.Update(bar); slow.Update(bar);
        }
        Assert.True(macd.IsReady);
        Assert.Equal(fast.CurrentValue - slow.CurrentValue, macd.CurrentDif, 9);
    }

    [Fact]
    public void Macd_RisingSeries_PositiveDif()
    {
        var macd = new MacdIndicator(3, 5, 3);
        for (int i = 1; i <= 20; i++) macd.Update(C(i));
        Assert.True(macd.IsReady);
        Assert.True(macd.CurrentDif > 0);            // 快线在慢线上方
        Assert.Equal(2 * (macd.CurrentDif - macd.CurrentDea), macd.CurrentHist, 9);
    }

    [Fact]
    public void Macd_ConstantInput_ZeroHistogram()
    {
        var macd = new MacdIndicator(3, 5, 3);
        for (int i = 0; i < 15; i++) macd.Update(C(5));
        Assert.True(macd.IsReady);
        Assert.Equal(0.0, macd.CurrentDif, 9);
        Assert.Equal(0.0, macd.CurrentHist, 9);
    }
}
