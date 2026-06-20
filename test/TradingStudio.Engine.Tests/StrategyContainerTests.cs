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
}
