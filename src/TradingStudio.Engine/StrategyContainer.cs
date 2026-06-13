using TradingStudio.Core.Engine;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 多策略容器。管理策略注册、事件路由、生命周期。
/// 策略之间完全隔离，各自持有独立的 StrategyContext。
/// </summary>
public class StrategyContainer
{
    // instrumentId → 订阅该品种的策略列表
    private readonly SortedDictionary<string, List<StrategySlot>> _subscriptions = new();

    public IReadOnlyList<StrategySlot> AllSlots => _subscriptions.Values.SelectMany(v => v).ToList();

    public void Register(IStrategy strategy, StrategyConfig config, StrategyContext context)
    {
        var slot = new StrategySlot(strategy, config, context);
        foreach (var inst in config.Instruments)
        {
            if (!_subscriptions.ContainsKey(inst))
                _subscriptions[inst] = new();
            _subscriptions[inst].Add(slot);
        }
    }

    public void Pause(string strategyId) { /* Phase 2a 实现 */ }
    public bool Resume(string strategyId) => false;
    public bool IsActive(string strategyId) => true;

    public void DispatchTick(TickEvent tickEvt) { /* Phase 2b 实现 */ }
    public void DispatchBar(BarEvent barEvt) { /* Phase 2a 实现 */ }
    public void DispatchOrderEvent(OrderEvent evt) { /* Phase 2a 实现 */ }
    public void DispatchAlert(IReadOnlyList<MonitorAlert> alerts) { /* Phase 2b 实现 */ }
    public void DispatchEndOfAlgorithm() { /* Phase 2a 实现 */ }

    public IReadOnlyList<StrategySnapshot> GetAllSnapshots() => [];
    public StrategySnapshot? GetSnapshot(string strategyId) => null;
    public bool TightenRisk(string strategyId, string ruleName, object newValue) => false;
}

public class StrategySlot
{
    public IStrategy Strategy { get; }
    public StrategyConfig Config { get; }
    public StrategyContext Context { get; }
    public bool IsActive { get; set; } = true;

    public StrategySlot(IStrategy strategy, StrategyConfig config, StrategyContext context)
    {
        Strategy = strategy;
        Config = config;
        Context = context;
    }
}

/// <summary>策略快照（供 API/报告用）</summary>
public record StrategySnapshot
{
    public string StrategyId { get; init; } = "";
    public string Status { get; init; } = "Running";
    public decimal AllocatedCapital { get; init; }
    public decimal CurrentEquity { get; init; }
    public double TodayPnl { get; init; }
    public int PositionCount { get; init; }
    public int ActiveOrderCount { get; init; }
}
