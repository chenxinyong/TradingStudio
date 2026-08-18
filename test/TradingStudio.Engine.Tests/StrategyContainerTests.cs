using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Tests;

public class StrategyContainerTests
{
    private static Bar Bar(string inst) => new() { InstrumentId = inst, BarTime = DateTime.Today };
    private static TickEvent Tick(string inst) => new() { InstrumentId = inst, Tick = new TickRecord(), TradingDay = DateOnly.FromDateTime(DateTime.Today), Time = DateTimeOffset.Now };

    private class SpyStrategy : IStrategy
    {
        public string Name => "Spy";
        public int BarCalls, TickCalls, OrderCalls, EndCalls;
        public void Initialize(StrategyContext ctx) { }
        public void OnBar(Bar bar) { BarCalls++; }
        public void OnTick(TickRecord tick, string inst) { TickCalls++; }
        public void OnOrderEvent(OrderEvent evt) { OrderCalls++; }
        public void OnEndOfAlgorithm() { EndCalls++; }
    }

    private static StrategyConfig Config(string id, params string[] instruments) => new()
    { StrategyId = id, Instruments = instruments, Priority = 1 };

    [Fact] public void Register_AddsToAllSlots() { var c = new StrategyContainer(); c.Register(new SpyStrategy(), Config("s1", "rb"), null!); Assert.Single(c.AllSlots); }
    [Fact] public void Register_MultipleStrategies() { var c = new StrategyContainer(); c.Register(new SpyStrategy(), Config("s1", "rb"), null!); c.Register(new SpyStrategy(), Config("s2", "ma"), null!); Assert.Equal(2, c.AllSlots.Count); }
    [Fact] public void DispatchBar_RoutesToSubscribed() { var c = new StrategyContainer(); var s = new SpyStrategy(); c.Register(s, Config("s1", "rb"), null!); c.DispatchBar(new BarEvent { Bar = Bar("rb") }); Assert.Equal(1, s.BarCalls); }
    [Fact] public void DispatchBar_SkipsUnsubscribed() { var c = new StrategyContainer(); var s = new SpyStrategy(); c.Register(s, Config("s1", "rb"), null!); c.DispatchBar(new BarEvent { Bar = Bar("ma") }); Assert.Equal(0, s.BarCalls); }
    [Fact] public void DispatchBar_MultipleStrategies() { var c = new StrategyContainer(); var s1 = new SpyStrategy(); var s2 = new SpyStrategy(); c.Register(s1, Config("s1", "rb"), null!); c.Register(s2, Config("s2", "rb"), null!); c.DispatchBar(new BarEvent { Bar = Bar("rb") }); Assert.Equal(1, s1.BarCalls); Assert.Equal(1, s2.BarCalls); }
    [Fact] public void DispatchTick_RoutesToSubscribed() { var c = new StrategyContainer(); var s = new SpyStrategy(); c.Register(s, Config("s1", "rb"), null!); c.DispatchTick(Tick("rb")); Assert.Equal(1, s.TickCalls); }
    [Fact] public void DispatchTick_SkipsUnsubscribed() { var c = new StrategyContainer(); var s = new SpyStrategy(); c.Register(s, Config("s1", "rb"), null!); c.DispatchTick(Tick("ma")); Assert.Equal(0, s.TickCalls); }
    [Fact] public void DispatchOrderEvent_RoutesByStrategyId() { var c = new StrategyContainer(); var s1 = new SpyStrategy(); var s2 = new SpyStrategy(); c.Register(s1, Config("s1", "rb"), null!); c.Register(s2, Config("s2", "ma"), null!); c.DispatchOrderEvent(new OrderEvent { StrategyId = "s1" }); Assert.Equal(1, s1.OrderCalls); Assert.Equal(0, s2.OrderCalls); }
    [Fact] public void Pause_StopsDispatch() { var c = new StrategyContainer(); var s = new SpyStrategy(); c.Register(s, Config("s1", "rb"), null!); c.Pause("s1"); c.DispatchBar(new BarEvent { Bar = Bar("rb") }); Assert.Equal(0, s.BarCalls); }
    [Fact] public void Resume_RestoresDispatch() { var c = new StrategyContainer(); var s = new SpyStrategy(); c.Register(s, Config("s1", "rb"), null!); c.Pause("s1"); c.Resume("s1"); c.DispatchBar(new BarEvent { Bar = Bar("rb") }); Assert.Equal(1, s.BarCalls); }
    [Fact] public void IsActive_DefaultsTrue() { var c = new StrategyContainer(); c.Register(new SpyStrategy(), Config("s1", "rb"), null!); Assert.True(c.IsActive("s1")); }
    [Fact] public void IsActive_AfterPause_False() { var c = new StrategyContainer(); c.Register(new SpyStrategy(), Config("s1", "rb"), null!); c.Pause("s1"); Assert.False(c.IsActive("s1")); }
    [Fact] public void OnEndOfAlgorithm_CalledForAll() { var c = new StrategyContainer(); var s1 = new SpyStrategy(); var s2 = new SpyStrategy(); c.Register(s1, Config("s1", "rb"), null!); c.Register(s2, Config("s2", "ma"), null!); foreach (var slot in c.AllSlots) slot.Strategy.OnEndOfAlgorithm(); Assert.Equal(1, s1.EndCalls); Assert.Equal(1, s2.EndCalls); }

    // ═══ 热加载 (Pause → 改参 → Resume) 集成测试 ═══

    /// <summary>带 StrategyParam&lt;T&gt; 的真实策略，OnBar 每次解引用最新参数值。</summary>
    private class HotReloadStrategy : IStrategy
    {
        public string Name => "HotReload";
        public StrategyParam<double> StopAtr { get; } = new("StopAtr", 2.0) { Group = "Risk" };
        public StrategyParam<int> FastPeriod { get; } = new("FastPeriod", 5) { Group = "Entry", Validator = v => v >= 2 && v <= 50 };
        public double LastSeenStopAtr;
        public int LastSeenFastPeriod;
        public int BarCalls;
        public void Initialize(StrategyContext ctx) { }
        public void OnBar(Bar bar) { BarCalls++; LastSeenStopAtr = StopAtr; LastSeenFastPeriod = FastPeriod; }
        public void OnTick(TickRecord tick, string inst) { }
        public void OnOrderEvent(OrderEvent evt) { }
        public void OnEndOfAlgorithm() { }
    }

    [Fact]
    public void HotReload_PauseUpdateResume_NewValueReadOnNextBar()
    {
        var c = new StrategyContainer();
        var s = new HotReloadStrategy();
        c.Register(s, Config("s1", "rb"), null!);

        // 初始值
        c.DispatchBar(new BarEvent { Bar = Bar("rb") });
        Assert.Equal(2.0, s.LastSeenStopAtr);

        // Pause → 改参 → Resume
        c.Pause("s1");
        var (ok, err) = c.UpdateParameter("s1", "StopAtr", 3.5);
        Assert.True(ok, err ?? "update should succeed");

        // Paused 期间 OnBar 不触发（改参不打断暂停态）
        c.DispatchBar(new BarEvent { Bar = Bar("rb") });
        Assert.Equal(1, s.BarCalls);
        Assert.Equal(2.0, s.LastSeenStopAtr); // 未被新 bar 覆盖

        // Resume → 下次 OnBar 读到新值
        Assert.True(c.Resume("s1"));
        c.DispatchBar(new BarEvent { Bar = Bar("rb") });
        Assert.Equal(2, s.BarCalls);
        Assert.Equal(3.5, s.LastSeenStopAtr);
    }

    [Fact]
    public void HotReload_UpdateWhileActive_NewValueReadOnNextBar()
    {
        // 热加载不强制 Pause：最终一致性，API 线程写入后下次 DispatchBar 自动读新值
        var c = new StrategyContainer();
        var s = new HotReloadStrategy();
        c.Register(s, Config("s1", "rb"), null!);

        var (ok, _) = c.UpdateParameter("s1", "StopAtr", 4.0);
        Assert.True(ok);

        c.DispatchBar(new BarEvent { Bar = Bar("rb") });
        Assert.Equal(4.0, s.LastSeenStopAtr);
    }

    [Fact]
    public void HotReload_UnknownParam_ReturnsFalse()
    {
        var c = new StrategyContainer();
        c.Register(new HotReloadStrategy(), Config("s1", "rb"), null!);

        var (ok, err) = c.UpdateParameter("s1", "NoSuchParam", 1.0);
        Assert.False(ok);
        Assert.Contains("NoSuchParam", err);
    }

    [Fact]
    public void HotReload_ValidatorRejects_ValueUnchanged()
    {
        var c = new StrategyContainer();
        var s = new HotReloadStrategy();
        c.Register(s, Config("s1", "rb"), null!);

        // FastPeriod 校验器: 2..50，传 100 应被拒绝
        var (ok, err) = c.UpdateParameter("s1", "FastPeriod", 100);
        Assert.False(ok);
        Assert.Contains("Validation failed", err);

        c.DispatchBar(new BarEvent { Bar = Bar("rb") });
        Assert.Equal(5, s.LastSeenFastPeriod); // 保持默认值 5
    }

    [Fact]
    public void HotReload_UpdateParameters_Batch()
    {
        var c = new StrategyContainer();
        var s = new HotReloadStrategy();
        c.Register(s, Config("s1", "rb"), null!);

        var (ok, err) = c.UpdateParameters("s1", new Dictionary<string, object>
        {
            ["StopAtr"] = 3.0,
            ["FastPeriod"] = 10,
        });
        Assert.True(ok, err ?? "batch should succeed");

        c.DispatchBar(new BarEvent { Bar = Bar("rb") });
        Assert.Equal(3.0, s.LastSeenStopAtr);
        Assert.Equal(10, s.LastSeenFastPeriod);
    }

    [Fact]
    public void HotReload_UnknownStrategy_ReturnsFalse()
    {
        var c = new StrategyContainer();
        var (ok, err) = c.UpdateParameter("ghost", "StopAtr", 1.0);
        Assert.False(ok);
        Assert.Contains("not found", err);
    }
}
