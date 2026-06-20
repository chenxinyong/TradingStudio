using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Engine;

namespace TradingStudio.Engine.Tests;

public class IndicatorManagerTests
{
    /// <summary>简单累加器指标 — 每根 Bar 累加 Close，Ready 条件 = 至少 N 根 Bar</summary>
    private class SumIndicator : IIndicator
    {
        private readonly int _warmup;
        private int _count;
        public string Name { get; }
        public string Tag { get; }
        public bool IsReady => _count >= _warmup;
        public double CurrentValue { get; private set; }
        public int WarmupPeriod => _warmup;
        public IReadOnlyList<double> Values => [_count > 0 ? CurrentValue : 0];

        public SumIndicator(string name = "SUM", string tag = "", int warmup = 3)
        { Name = name; Tag = tag; _warmup = warmup; }

        public void Update(Bar bar) { CurrentValue += bar.CloseDouble; _count++; }
        public void Reset() { CurrentValue = 0; _count = 0; }
    }

    private static Bar Bar(string inst, double close)
        => new() { InstrumentId = inst, Close = (long)(close * TickRecord.PriceScale), BarTime = DateTime.Today };

    // ═══ 注册 ═══

    [Fact]
    public void Register_NewIndicator_ReturnsSameInstance()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM");
        var result = mgr.Register("rb", ind, "s1");
        Assert.Same(ind, result);
    }

    [Fact]
    public void Register_Duplicate_ReusesExisting()
    {
        var mgr = new IndicatorManager();
        var ind1 = new SumIndicator("SUM");
        var ind2 = new SumIndicator("SUM");
        mgr.Register("rb", ind1, "s1");
        var result = mgr.Register("rb", ind2, "s1");  // 同名同品种 → 复用
        Assert.Same(ind1, result);
        Assert.NotSame(ind2, result);
    }

    [Fact]
    public void Register_DifferentTag_CreatesNew()
    {
        var mgr = new IndicatorManager();
        var ind1 = new SumIndicator("SUM", "fast");
        var ind2 = new SumIndicator("SUM", "slow");
        mgr.Register("rb", ind1, "s1", "fast");
        var result = mgr.Register("rb", ind2, "s1", "slow");
        Assert.Same(ind2, result);  // 不同 tag → 不冲突
    }

    [Fact]
    public void Register_DifferentInstrument_Independent()
    {
        var mgr = new IndicatorManager();
        var rbInd = new SumIndicator("SUM");
        var maInd = new SumIndicator("SUM");
        mgr.Register("rb", rbInd, "s1");
        mgr.Register("ma", maInd, "s1");
        // 两个品种各自独立
        Assert.NotSame(rbInd, maInd);
    }

    // ═══ Feed ═══

    [Fact]
    public void Feed_UpdatesRegisteredIndicator()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM");
        mgr.Register("rb", ind, "s1");
        mgr.Feed(Bar("rb", 100));
        mgr.Feed(Bar("rb", 200));
        Assert.Equal(300, ind.CurrentValue);
    }

    [Fact]
    public void Feed_OnlyUpdatesMatchingInstrument()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM");
        mgr.Register("rb", ind, "s1");
        mgr.Feed(Bar("ma", 999));  // 不同品种
        mgr.Feed(Bar("rb", 100));
        Assert.Equal(100, ind.CurrentValue);  // MA 的 Bar 没影响 RB 指标
    }

    [Fact]
    public void Feed_NoIndicator_NoOp()
    {
        var mgr = new IndicatorManager();
        mgr.Feed(Bar("rb", 100));  // 没注册 → 不抛异常
    }

    // ═══ Warmup ═══

    [Fact]
    public void Warmup_FeedsAllBars()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM");
        mgr.Register("rb", ind, "s1");
        var history = new[] { Bar("rb", 10), Bar("rb", 20), Bar("rb", 30), Bar("rb", 40) };
        mgr.Warmup(history, "rb");
        Assert.Equal(100, ind.CurrentValue);
    }

    // ═══ Query ═══

    [Fact]
    public void GetValue_AfterFeed_ReturnsCurrentValue()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM", warmup: 1);
        mgr.Register("rb", ind, "s1");
        mgr.Feed(Bar("rb", 3500));
        Assert.Equal(3500, mgr.GetValue("rb", "SUM"));
    }

    [Fact]
    public void GetValue_NotReady_ReturnsNaN()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM", warmup: 5);
        mgr.Register("rb", ind, "s1");
        mgr.Feed(Bar("rb", 100));
        Assert.True(double.IsNaN(mgr.GetValue("rb", "SUM")));
    }

    [Fact]
    public void GetValue_NotRegistered_ReturnsNaN()
    {
        var mgr = new IndicatorManager();
        Assert.True(double.IsNaN(mgr.GetValue("rb", "RSI")));
    }

    // ═══ Typed Query ═══

    [Fact]
    public void Get_Typed_ReturnsIndicator()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM");
        mgr.Register("rb", ind, "s1");
        var result = mgr.Get<SumIndicator>("rb");
        Assert.Same(ind, result);
    }

    [Fact]
    public void Get_Typed_NotFound_ReturnsNull()
    {
        var mgr = new IndicatorManager();
        Assert.Null(mgr.Get<SumIndicator>("rb"));
    }

    // ═══ Reset ═══

    [Fact]
    public void Reset_ClearsAllIndicators()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM");
        mgr.Register("rb", ind, "s1");
        mgr.Feed(Bar("rb", 100));
        mgr.Reset();
        Assert.Equal(0, ind.CurrentValue);
    }

    // ═══ 去重验证 ═══

    [Fact]
    public void Dedup_TwoStrategiesSameIndicator_Reuses()
    {
        var mgr = new IndicatorManager();
        var ind = new SumIndicator("SUM");
        mgr.Register("rb", ind, "s1");
        // 策略2注册同一品种同一指标 → 应复用
        var ind2 = new SumIndicator("SUM");
        var result = mgr.Register("rb", ind2, "s2");
        Assert.Same(ind, result);
    }
}
